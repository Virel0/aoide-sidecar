using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Sound;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// One stretch of a track over which a single fixed grid fits, on the wire.
/// </summary>
public class BeatSegmentDto
{
    /// <summary>Gets or sets where the segment begins in the track, in milliseconds.</summary>
    [JsonPropertyName("startMs")]
    public double StartMs { get; set; }

    /// <summary>Gets or sets where it ends.</summary>
    [JsonPropertyName("endMs")]
    public double EndMs { get; set; }

    /// <summary>Gets or sets the fitted position of beat zero. <c>beat(n) = anchorMs + n * 60000 / bpm</c>.</summary>
    [JsonPropertyName("anchorMs")]
    public double AnchorMs { get; set; }

    /// <summary>Gets or sets the fitted tempo, which may differ slightly from the reported bpm.</summary>
    [JsonPropertyName("bpm")]
    public double Bpm { get; set; }

    /// <summary>Gets or sets the RMS distance between the fitted beats and the beats detected.</summary>
    [JsonPropertyName("residualMs")]
    public double ResidualMs { get; set; }

    /// <summary>Gets or sets how many beats the fit spans.</summary>
    [JsonPropertyName("beats")]
    public int Beats { get; set; }
}

/// <summary>
/// One track's grid on the wire. Every field beyond the segments is independently nullable.
/// </summary>
public class BeatGridDto
{
    /// <summary>Gets or sets the fits: one, or several where the track changes tempo.</summary>
    [JsonPropertyName("segments")]
    public IReadOnlyList<BeatSegmentDto> Segments { get; set; } = Array.Empty<BeatSegmentDto>();

    /// <summary>Gets or sets beats to a bar, or null when the meter could not be established.</summary>
    [JsonPropertyName("beatsPerBar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BeatsPerBar { get; set; }

    /// <summary>Gets or sets which beat of a fit begins a bar. Zero whenever the meter is known.</summary>
    [JsonPropertyName("downbeatIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? DownbeatIndex { get; set; }

    /// <summary>Gets or sets where a blend may bring this track in.</summary>
    [JsonPropertyName("mixInMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? MixInMs { get; set; }

    /// <summary>Gets or sets where a blend may take this track out.</summary>
    [JsonPropertyName("mixOutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? MixOutMs { get; set; }

    /// <summary>Gets or sets the key on the Camelot wheel, such as <c>8A</c>.</summary>
    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Key { get; set; }

    /// <summary>Gets or sets how far ahead of the runner-up that key finished, zero to one.</summary>
    [JsonPropertyName("keyConfidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? KeyConfidence { get; set; }
}

/// <summary>
/// Response for a lookup.
/// </summary>
public class BeatGridResponseDto
{
    /// <summary>
    /// Gets or sets the grid per id. A null value means measured, no grid worth having.
    /// An id missing altogether is unknown, not visible, or has no file to measure.
    /// </summary>
    [JsonPropertyName("grids")]
    public Dictionary<string, BeatGridDto?> Grids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets ids queued for measurement. Ask again later.</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<string> Pending { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Where each track's beats actually fall, for anything that wants to line two tracks up.
/// </summary>
/// <remarks>
/// The same decode as <see cref="SoundBoundsController"/> and
/// <see cref="AudioAnalysisController"/>, and deliberately the same shape. Asking any of
/// the three about a file measures it for all of them.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/beat-grid")]
[Produces(MediaTypeNames.Application.Json)]
public class BeatGridController : ControllerBase
{
    private const int MaxIds = 200;

    private readonly AudioAnalysisService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="BeatGridController"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility checks.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public BeatGridController(
        AudioAnalysisService service,
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
    /// Looks up beat grids for up to 200 items, queuing any not yet measured.
    /// </summary>
    /// <param name="ids">Comma-separated Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The grids for what is known, and what is pending.</returns>
    /// <response code="200">The lookup.</response>
    /// <response code="400">More than 200 ids.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(BeatGridResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<BeatGridResponseDto>> Get(
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
            if (AudioItems.Resolve(_libraryManager, id, user) is { } file)
            {
                files.Add(file);
            }
        }

        var lookups = await _service.LookupGridAsync(files, cancellationToken).ConfigureAwait(false);
        var response = new BeatGridResponseDto();
        var pending = new List<string>();

        foreach (var (id, lookup) in lookups)
        {
            if (lookup.Pending)
            {
                pending.Add(id);
            }
            else if (lookup.Row is { Error: null } row)
            {
                response.Grids[id] = Project(row);
            }

            // A row with an error, or no file: omitted, like an unknown id. A client that
            // cannot get a grid falls back to a crossfade either way.
        }

        response.Pending = pending;
        return Ok(response);
    }

    /// <summary>
    /// Turns a cached row into what the client is told.
    /// </summary>
    /// <remarks>
    /// A track with a key but no grid still returns nothing. The key alone cannot support a
    /// transition, and an entry carrying only a key would read as a grid the client could
    /// use.
    /// </remarks>
    internal static BeatGridDto? Project(Data.BeatGridRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Grid is not { Segments.Count: > 0 } grid)
        {
            return null;
        }

        return new BeatGridDto
        {
            Segments = grid.Segments.Select(s => new BeatSegmentDto
            {
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                AnchorMs = s.AnchorMs,
                Bpm = s.Bpm,
                ResidualMs = s.ResidualMs,
                Beats = s.Beats
            }).ToList(),
            BeatsPerBar = grid.BeatsPerBar,
            DownbeatIndex = grid.DownbeatIndex,
            MixInMs = grid.MixInMs,
            MixOutMs = grid.MixOutMs,
            Key = row.Key?.Camelot,
            KeyConfidence = row.Key?.Confidence
        };
    }
}
