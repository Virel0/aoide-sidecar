using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Sound;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// One file's sounding span on the wire.
/// </summary>
public class SoundBoundsDto
{
    /// <summary>Gets or sets where to begin, in milliseconds.</summary>
    [JsonPropertyName("soundStartMs")]
    public long SoundStartMs { get; set; }

    /// <summary>Gets or sets where to stop, in milliseconds.</summary>
    [JsonPropertyName("soundEndMs")]
    public long SoundEndMs { get; set; }
}

/// <summary>
/// Response for a lookup, and body for a client-supplied measurement.
/// </summary>
public class SoundBoundsResponseDto
{
    /// <summary>
    /// Gets or sets bounds per id. A null value means measured, nothing to trim. An id
    /// missing altogether is unknown, not visible, or has no file to measure.
    /// </summary>
    [JsonPropertyName("bounds")]
    public Dictionary<string, SoundBoundsDto?> Bounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets ids queued for measurement. Play them whole and ask again later.</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<string> Pending { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Where each track's sound actually starts and stops, for the devices that do not hold
/// the file to measure it themselves.
/// </summary>
[ApiController]
[Authorize]
[Route("aoide/sound-bounds")]
[Produces(MediaTypeNames.Application.Json)]
public class SoundBoundsController : ControllerBase
{
    private const int MaxIds = 200;

    private readonly SoundBoundsService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundBoundsController"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility checks.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public SoundBoundsController(
        SoundBoundsService service,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IAuthorizationContext authorizationContext)
    {
        _service = service;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Looks up bounds for up to 200 items, queuing any not yet measured.
    /// </summary>
    /// <param name="ids">Comma-separated Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bounds for what is known, and what is pending.</returns>
    /// <response code="200">The lookup.</response>
    /// <response code="400">More than 200 ids.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(SoundBoundsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SoundBoundsResponseDto>> Get(
        [FromQuery] string? ids,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var requested = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count > MaxIds)
        {
            return StatusCode(StatusCodes.Status400BadRequest, new ProblemDetails
            {
                Title = "Too many ids",
                Detail = $"{requested.Count} ids exceeds the limit of {MaxIds}. Split the request.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var user = _userManager.GetUserById(authorization.UserId);
        var files = new List<(string Id, string Path)>();
        foreach (var id in requested)
        {
            if (Resolve(id, user) is { } file)
            {
                files.Add(file);
            }
        }

        var lookups = await _service.LookupAsync(files, cancellationToken).ConfigureAwait(false);
        var response = new SoundBoundsResponseDto();
        var pending = new List<string>();

        foreach (var (id, lookup) in lookups)
        {
            if (lookup.Pending)
            {
                pending.Add(id);
            }
            else if (lookup.Row is { Error: null } row)
            {
                response.Bounds[id] = row.Bounds is null
                    ? null
                    : new SoundBoundsDto { SoundStartMs = row.Bounds.SoundStartMs, SoundEndMs = row.Bounds.SoundEndMs };
            }

            // A row with an error, or no file: omitted, like an unknown id. Nothing the
            // client can do differs, and it plays the file whole either way.
        }

        response.Pending = pending;
        return Ok(response);
    }

    /// <summary>
    /// Accepts measurements a client made itself, so they can be served without decoding.
    /// </summary>
    /// <param name="body">Bounds per id, in the same shape the lookup returns.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">Stored what could be stored.</response>
    /// <response code="400">Malformed bounds.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Post([FromBody] SoundBoundsResponseDto body, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (body?.Bounds is null || body.Bounds.Count > MaxIds)
        {
            return StatusCode(StatusCodes.Status400BadRequest, new ProblemDetails
            {
                Title = "Malformed body",
                Detail = $"Send {{ bounds: {{ id: {{ soundStartMs, soundEndMs }} | null }} }} with at most {MaxIds} ids.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var user = _userManager.GetUserById(authorization.UserId);
        foreach (var (id, dto) in body.Bounds)
        {
            if (dto is not null && (dto.SoundStartMs < 0 || dto.SoundEndMs <= dto.SoundStartMs))
            {
                return StatusCode(StatusCodes.Status400BadRequest, new ProblemDetails
                {
                    Title = "Malformed bounds",
                    Detail = $"{id}: soundStartMs must be >= 0 and soundEndMs > soundStartMs.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            if (Resolve(id, user) is { } file)
            {
                await _service
                    .StoreClientMeasurementAsync(
                        file.Id,
                        file.Path,
                        dto is null ? null : new SoundBounds(dto.SoundStartMs, dto.SoundEndMs),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Turns a client-supplied id into a visible audio file, or nothing.
    /// </summary>
    /// <remarks>
    /// Nothing here may throw. An unknown id is omitted from the answer, not an error —
    /// and Jellyfin's <c>GetItemById</c> throws on an empty GUID, so without the guard one
    /// malformed id in a request of two hundred would fail all of them.
    /// </remarks>
    private (string Id, string Path)? Resolve(string id, object? user)
    {
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
        {
            return null;
        }

        try
        {
            if (_libraryManager.GetItemById(guid) is not Audio audio || string.IsNullOrEmpty(audio.Path))
            {
                return null;
            }

            if (user is Jellyfin.Database.Implementations.Entities.User jellyfinUser && !audio.IsVisible(jellyfinUser))
            {
                return null;
            }

            return (guid.ToString("N", CultureInfo.InvariantCulture), audio.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
