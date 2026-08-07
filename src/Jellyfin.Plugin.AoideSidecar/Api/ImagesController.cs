using System.Globalization;
using System.Security.Cryptography;
using Jellyfin.Plugin.AoideSidecar.Configuration;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// Playlist artwork, addressed by the SHA-256 of its own bytes.
/// </summary>
/// <remarks>
/// The op log carries only <c>image_hash</c> and <c>image_mime</c> on the playlist row;
/// the bytes travel here. That split is what keeps a full history sync small — the log
/// is replayed in full by every device, and artwork in it would grow without bound.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/images")]
public class ImagesController : ControllerBase
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly PlaylistImageRepository _images;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<ImagesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagesController"/> class.
    /// </summary>
    /// <param name="images">Artwork storage.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public ImagesController(
        PlaylistImageRepository images,
        IAuthorizationContext authorizationContext,
        ILogger<ImagesController> logger)
    {
        _images = images;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Uploads artwork under its own hash.
    /// </summary>
    /// <param name="hash">Lowercase hex SHA-256 of the body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Stored, or already present.</response>
    /// <response code="400">Bad hash, unsupported type, empty body, or bytes that do not match the hash.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="413">The image is over the configured limit.</response>
    [HttpPut("{hash}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult> Put(string hash, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!IsHash(hash))
        {
            return Problem("Invalid hash", "The path segment must be a lowercase hex SHA-256.", StatusCodes.Status400BadRequest);
        }

        var mimeType = Request.ContentType?.Split(';')[0].Trim() ?? string.Empty;
        if (!AllowedTypes.Contains(mimeType))
        {
            return Problem(
                "Unsupported image type",
                $"Content-Type must be one of: {string.Join(", ", AllowedTypes)}.",
                StatusCodes.Status400BadRequest);
        }

        var limit = Configuration.MaxImageBytes;
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                // Stop reading rather than buffering an unbounded body to find out how
                // big it was: the answer does not change the response.
                return Problem(
                    "Image too large",
                    $"Images are limited to {limit} bytes.",
                    StatusCodes.Status413PayloadTooLarge);
            }
        }

        if (buffer.Length == 0)
        {
            return Problem("Empty body", "No image bytes were sent.", StatusCodes.Status400BadRequest);
        }

        var bytes = buffer.ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
        {
            // Verified rather than trusted: without this the store is not content
            // addressed at all, and one client could park arbitrary bytes under a hash
            // every other device already believes it knows.
            return Problem(
                "Hash mismatch",
                $"The body hashes to {actual}, not {hash}.",
                StatusCodes.Status400BadRequest);
        }

        await _images
            .StoreAsync(authorization.UserId, actual, mimeType, bytes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Stored playlist artwork {Hash} ({Size} bytes)", actual, bytes.Length);
        return NoContent();
    }

    /// <summary>
    /// Fetches artwork by hash.
    /// </summary>
    /// <param name="hash">Lowercase hex SHA-256.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image bytes.</returns>
    /// <response code="200">The image.</response>
    /// <response code="400">The hash was malformed.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="404">No such image for this user.</response>
    [HttpGet("{hash}")]
    [HttpHead("{hash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(string hash, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!IsHash(hash))
        {
            return Problem("Invalid hash", "The path segment must be a lowercase hex SHA-256.", StatusCodes.Status400BadRequest);
        }

        var image = await _images.GetAsync(authorization.UserId, hash, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return NotFound();
        }

        // The bytes behind a content address never change, so this is safe to cache
        // for as long as the client likes.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.ETag = $"\"{hash}\"";

        return File(image.Bytes, image.MimeType);
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private ObjectResult Problem(string title, string detail, int status) =>
        StatusCode(status, new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = status
        });
}
