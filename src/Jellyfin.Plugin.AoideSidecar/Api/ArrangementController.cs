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
/// One part of a track on the wire.
/// </summary>
public class SectionDto
{
    /// <summary>Gets or sets where it begins, in milliseconds.</summary>
    [JsonPropertyName("startMs")]
    public double StartMs { get; set; }

    /// <summary>Gets or sets where it ends.</summary>
    [JsonPropertyName("endMs")]
    public double EndMs { get; set; }

    /// <summary>Gets or sets one of intro, build, drop, breakdown, outro or unknown.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = SectionKind.Unknown;

    /// <summary>Gets or sets zero to one, normalised within the track.</summary>
    [JsonPropertyName("energy")]
    public double Energy { get; set; }
}

/// <summary>
/// A stretch where somebody is probably singing, on the wire.
/// </summary>
public class VocalSpanDto
{
    /// <summary>Gets or sets where it begins, in milliseconds.</summary>
    [JsonPropertyName("startMs")]
    public double StartMs { get; set; }

    /// <summary>Gets or sets where it ends.</summary>
    [JsonPropertyName("endMs")]
    public double EndMs { get; set; }
}

/// <summary>
/// One track's arrangement on the wire.
/// </summary>
public class ArrangementDto
{
    /// <summary>Gets or sets the track's parts, contiguous and covering all of it.</summary>
    [JsonPropertyName("sections")]
    public IReadOnlyList<SectionDto> Sections { get; set; } = Array.Empty<SectionDto>();

    /// <summary>Gets or sets bars to a phrase, or null where the structure would not commit.</summary>
    [JsonPropertyName("phraseBars")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? PhraseBars { get; set; }

    /// <summary>Gets or sets a downbeat that begins a phrase, or null with the above.</summary>
    [JsonPropertyName("phraseAnchorMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? PhraseAnchorMs { get; set; }

    /// <summary>
    /// Gets or sets where singing was found. An empty list means none was found;
    /// <b>null means it could not be told</b>, which is not the same thing.
    /// </summary>
    [JsonPropertyName("vocals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<VocalSpanDto>? Vocals { get; set; }
}

/// <summary>
/// Response for a lookup.
/// </summary>
public class ArrangementResponseDto
{
    /// <summary>
    /// Gets or sets the arrangement per id. A null value means measured, no structure
    /// worth describing. An id missing altogether is unknown, not visible, or has no file.
    /// </summary>
    [JsonPropertyName("arrangements")]
    public Dictionary<string, ArrangementDto?> Arrangements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets ids queued for measurement. Ask again later.</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<string> Pending { get; set; } = Array.Empty<string>();
}

/// <summary>
/// What each track is made of and in what order, so a transition can happen somewhere the
/// music has room for it.
/// </summary>
/// <remarks>
/// The same decode as the rest of the family. Asking any of these endpoints about a file
/// measures it for all of them.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/arrangement")]
[Produces(MediaTypeNames.Application.Json)]
public class ArrangementController : ControllerBase
{
    private const int MaxIds = 200;

    private readonly AudioAnalysisService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrangementController"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility checks.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public ArrangementController(
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
    /// Looks up arrangements for up to 200 items, queuing any not yet measured.
    /// </summary>
    /// <param name="ids">Comma-separated Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The arrangements for what is known, and what is pending.</returns>
    /// <response code="200">The lookup.</response>
    /// <response code="400">More than 200 ids.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ArrangementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ArrangementResponseDto>> Get(
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

        var lookups = await _service.LookupArrangementAsync(files, cancellationToken).ConfigureAwait(false);
        var response = new ArrangementResponseDto();
        var pending = new List<string>();

        foreach (var (id, lookup) in lookups)
        {
            if (lookup.Pending)
            {
                pending.Add(id);
            }
            else if (lookup.Row is { Error: null } row)
            {
                response.Arrangements[id] = Project(row);
            }
        }

        response.Pending = pending;
        return Ok(response);
    }

    /// <summary>
    /// Turns a cached row into what the client is told.
    /// </summary>
    internal static ArrangementDto? Project(Data.ArrangementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Arrangement is not { Sections.Count: > 0 } arrangement)
        {
            return null;
        }

        return new ArrangementDto
        {
            Sections = arrangement.Sections.Select(s => new SectionDto
            {
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Kind = s.Kind,
                Energy = s.Energy
            }).ToList(),
            PhraseBars = arrangement.PhraseBars,
            PhraseAnchorMs = arrangement.PhraseAnchorMs,
            Vocals = arrangement.Vocals?
                .Select(v => new VocalSpanDto { StartMs = v.StartMs, EndMs = v.EndMs })
                .ToList()
        };
    }
}
