using System.Globalization;
using System.Security.Cryptography;
using Jellyfin.Plugin.AoideSidecar.Configuration;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Export;
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
    private readonly SyncRepository _repository;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<ImagesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagesController"/> class.
    /// </summary>
    /// <param name="images">Artwork storage.</param>
    /// <param name="repository">The op log, for working out which artwork is still in use.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public ImagesController(
        PlaylistImageRepository images,
        SyncRepository repository,
        IAuthorizationContext authorizationContext,
        ILogger<ImagesController> logger)
    {
        _images = images;
        _repository = repository;
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

    /// <summary>
    /// Reports stored artwork that no live playlist refers to. Deletes nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What is stored, and how much of it is unreferenced.</returns>
    /// <response code="200">The report.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet("orphans")]
    [ProducesResponseType(typeof(OrphanReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OrphanReportDto>> Orphans(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(await SurveyAsync(authorization.UserId, Configuration.ArtworkGraceDays, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes unreferenced artwork that has passed the grace period.
    /// </summary>
    /// <remarks>
    /// Never runs on its own. <paramref name="olderThanDays"/> may only make the sweep
    /// more cautious: it is raised to the configured grace period if a smaller value is
    /// asked for, because the grace period exists to cover a blob whose playlist row has
    /// not been pushed yet, and letting a caller waive it would defeat the point.
    /// </remarks>
    /// <param name="olderThanDays">Minimum age to delete; clamped upward to the configured grace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The survey, with what was actually removed.</returns>
    /// <response code="200">The sweep ran; see <c>reclaimed</c>.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost("orphans/reclaim")]
    [ProducesResponseType(typeof(OrphanReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OrphanReportDto>> Reclaim(
        [FromQuery] int? olderThanDays,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var grace = Math.Max(olderThanDays ?? Configuration.ArtworkGraceDays, Configuration.ArtworkGraceDays);
        var report = await SurveyAsync(authorization.UserId, grace, cancellationToken).ConfigureAwait(false);

        var doomed = report.Orphans.Where(o => o.Reclaimable).ToList();
        if (doomed.Count == 0)
        {
            return Ok(report);
        }

        var deleted = await _images
            .DeleteAsync(authorization.UserId, doomed.Select(o => o.ImageHash!).ToList(), cancellationToken)
            .ConfigureAwait(false);

        report.Reclaimed = deleted;
        report.ReclaimedBytes = doomed.Sum(o => o.SizeBytes);
        report.Orphans = report.Orphans.Where(o => !o.Reclaimable).ToList();
        report.OrphanBytes -= report.ReclaimedBytes;

        _logger.LogInformation(
            "Reclaimed {Count} orphaned playlist images ({Bytes} bytes) older than {Days} days for {User}",
            deleted,
            report.ReclaimedBytes,
            grace,
            authorization.UserId);

        return Ok(report);
    }

    private async Task<OrphanReportDto> SurveyAsync(Guid userId, int graceDays, CancellationToken cancellationToken)
    {
        var ops = await _repository.ReadPlaylistOpsAsync(userId, cancellationToken).ConfigureAwait(false);
        var referenced = ArtworkSweep.ReferencedHashes(PlaylistProjection.Build(ops));
        var blobs = await _images.ListAsync(userId, cancellationToken).ConfigureAwait(false);

        return ArtworkSweep.Build(blobs, referenced, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), graceDays);
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
