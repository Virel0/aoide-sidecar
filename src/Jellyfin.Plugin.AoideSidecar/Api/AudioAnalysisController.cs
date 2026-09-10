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
/// One file's loudness and tempo on the wire. Every field is independently nullable: a
/// track can have a loudness and no usable tempo.
/// </summary>
/// <remarks>
/// Every property is written even when null. Jellyfin configures MVC's serialiser to drop
/// nulls, which would have quietly turned a documented <c>"bpm": null</c> into no <c>bpm</c>
/// key at all — the same class of mistake as the PascalCase responses this project shipped
/// once already. Both readings are equivalent to a lenient decoder and the contract says
/// the field is there, so the field is there.
/// </remarks>
public class AudioAnalysisDto
{
    /// <summary>Gets or sets EBU R128 integrated loudness over the whole track, in LUFS.</summary>
    [JsonPropertyName("loudnessLufs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? LoudnessLufs { get; set; }

    /// <summary>Gets or sets true peak, in dBFS.</summary>
    [JsonPropertyName("truePeakDbfs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? TruePeakDbfs { get; set; }

    /// <summary>Gets or sets beats per minute.</summary>
    [JsonPropertyName("bpm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Bpm { get; set; }

    /// <summary>Gets or sets how much the tempo is worth believing, zero to one.</summary>
    [JsonPropertyName("bpmConfidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? BpmConfidence { get; set; }

    /// <summary>
    /// Gets or sets how much of the track keeps that tempo, zero to one, or null when the
    /// track was too short to tell. One means a fixed grid would fit the whole thing.
    /// </summary>
    [JsonPropertyName("bpmStability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? BpmStability { get; set; }

    /// <summary>Gets a value indicating whether there is anything here worth sending.</summary>
    [JsonIgnore]
    public bool IsEmpty => LoudnessLufs is null && TruePeakDbfs is null && Bpm is null;
}

/// <summary>
/// Response for a lookup, and body for a client-supplied measurement.
/// </summary>
public class AudioAnalysisResponseDto
{
    /// <summary>
    /// Gets or sets the analysis per id. A null value means measured, nothing to report.
    /// An id missing altogether is unknown, not visible, or has no file to measure.
    /// </summary>
    [JsonPropertyName("analysis")]
    public Dictionary<string, AudioAnalysisDto?> Analysis { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets ids queued for measurement. Play them unmodified and ask again later.</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<string> Pending { get; set; } = Array.Empty<string>();
}

/// <summary>
/// How loud each track is and how fast, for the devices that do not hold the file to
/// measure it themselves.
/// </summary>
/// <remarks>
/// The same decode as <see cref="SoundBoundsController"/>, and deliberately the same
/// shape. Asking either endpoint about a file measures it for both.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/audio-analysis")]
[Produces(MediaTypeNames.Application.Json)]
public class AudioAnalysisController : ControllerBase
{
    private const int MaxIds = 200;

    /// <summary>Loudness below this is not a measurement, it is a broken file.</summary>
    private const double MinimumLoudnessLufs = -100;

    /// <summary>Tempi outside this range are not tempi.</summary>
    private const double MinimumBpm = 20;

    /// <summary>Tempi outside this range are not tempi.</summary>
    private const double MaximumBpm = 400;

    private readonly AudioAnalysisService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioAnalysisController"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility checks.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public AudioAnalysisController(
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
    /// Looks up loudness and tempo for up to 200 items, queuing any not yet measured.
    /// </summary>
    /// <param name="ids">Comma-separated Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analysis for what is known, and what is pending.</returns>
    /// <response code="200">The lookup.</response>
    /// <response code="400">More than 200 ids.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AudioAnalysisResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AudioAnalysisResponseDto>> Get(
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

        var lookups = await _service.LookupAnalysisAsync(files, cancellationToken).ConfigureAwait(false);
        var response = new AudioAnalysisResponseDto();
        var pending = new List<string>();

        foreach (var (id, lookup) in lookups)
        {
            if (lookup.Pending)
            {
                pending.Add(id);
            }
            else if (lookup.Row is { Error: null } row)
            {
                var dto = Project(row);
                response.Analysis[id] = dto.IsEmpty ? null : dto;
            }

            // A row with an error, or no file: omitted, like an unknown id. Nothing the
            // client can do differs, and it plays the file unmodified either way.
        }

        response.Pending = pending;
        return Ok(response);
    }

    /// <summary>
    /// Accepts measurements a client made itself, so they can be served without decoding.
    /// </summary>
    /// <param name="body">Analysis per id, in the same shape the lookup returns.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">Stored what could be stored.</response>
    /// <response code="400">Malformed analysis.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Post([FromBody] AudioAnalysisResponseDto body, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (body?.Analysis is null || body.Analysis.Count > MaxIds)
        {
            return StatusCode(StatusCodes.Status400BadRequest, new ProblemDetails
            {
                Title = "Malformed body",
                Detail = $"Send {{ analysis: {{ id: {{ loudnessLufs, truePeakDbfs, bpm, bpmConfidence, bpmStability }} | null }} }} "
                       + $"with at most {MaxIds} ids.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var user = _userManager.GetUserById(authorization.UserId);
        foreach (var (id, dto) in body.Analysis)
        {
            if (Invalid(dto) is { } reason)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new ProblemDetails
                {
                    Title = "Malformed analysis",
                    Detail = $"{id}: {reason}",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            if (AudioItems.Resolve(_libraryManager, id, user) is { } file)
            {
                var loudness = dto?.LoudnessLufs is { } lufs && dto.TruePeakDbfs is { } peak
                    ? new Loudness(lufs, peak)
                    : null;

                // A client that knows a tempo but not how sure it is is taken at its word.
                var tempo = dto?.Bpm is { } bpm ? new Tempo(bpm, dto.BpmConfidence ?? 1, dto.BpmStability) : null;

                await _service
                    .StoreClientAnalysisAsync(file.Id, file.Path, loudness, tempo, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Turns a cached row into what the client is told, applying the confidence threshold
    /// below which a tempo is worse than nothing.
    /// </summary>
    internal static AudioAnalysisDto Project(Data.AudioAnalysisRow row)
    {
        var dto = new AudioAnalysisDto
        {
            LoudnessLufs = row.Loudness?.LoudnessLufs,
            TruePeakDbfs = row.Loudness?.TruePeakDbfs
        };

        if (row.Tempo is { } tempo && tempo.Confidence >= TempoAnalyzer.MinimumConfidence)
        {
            dto.Bpm = tempo.Bpm;
            dto.BpmConfidence = tempo.Confidence;
            dto.BpmStability = tempo.Stability;
        }

        return dto;
    }

    /// <summary>
    /// Why a client's measurement cannot be stored, or null when it can.
    /// </summary>
    private static string? Invalid(AudioAnalysisDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        if (dto.LoudnessLufs is { } lufs && (!double.IsFinite(lufs) || lufs < MinimumLoudnessLufs || lufs > 0))
        {
            return $"loudnessLufs must be between {MinimumLoudnessLufs} and 0.";
        }

        if (dto.TruePeakDbfs is { } peak && !double.IsFinite(peak))
        {
            return "truePeakDbfs must be a number.";
        }

        if ((dto.LoudnessLufs is null) != (dto.TruePeakDbfs is null))
        {
            return "loudnessLufs and truePeakDbfs go together; send both or neither.";
        }

        if (dto.Bpm is { } bpm && (!double.IsFinite(bpm) || bpm < MinimumBpm || bpm > MaximumBpm))
        {
            return $"bpm must be between {MinimumBpm} and {MaximumBpm}.";
        }

        if (dto.BpmConfidence is { } confidence && (!double.IsFinite(confidence) || confidence < 0 || confidence > 1))
        {
            return "bpmConfidence must be between 0 and 1.";
        }

        if (dto.BpmStability is { } stability && (!double.IsFinite(stability) || stability < 0 || stability > 1))
        {
            return "bpmStability must be between 0 and 1.";
        }

        return null;
    }
}
