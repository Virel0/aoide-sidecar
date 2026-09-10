using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sound;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// How much of the library has been measured.
/// </summary>
public class CoverageDto
{
    /// <summary>Gets or sets how many audio items the library holds.</summary>
    [JsonPropertyName("tracks")]
    public long Tracks { get; set; }

    /// <summary>Gets or sets how many have sound bounds stored.</summary>
    [JsonPropertyName("soundBounds")]
    public long SoundBounds { get; set; }

    /// <summary>Gets or sets how many have loudness and tempo stored.</summary>
    [JsonPropertyName("audioAnalysis")]
    public long AudioAnalysis { get; set; }

    /// <summary>Gets or sets how many have been through beat detection.</summary>
    [JsonPropertyName("beatGrids")]
    public long BeatGrids { get; set; }

    /// <summary>
    /// Gets or sets how many of those came out with a grid. The rest were measured and
    /// found to have no beat worth publishing, which is finished rather than outstanding.
    /// </summary>
    [JsonPropertyName("gridded")]
    public long Gridded { get; set; }

    /// <summary>Gets or sets how many have been through structure detection.</summary>
    [JsonPropertyName("arrangements")]
    public long Arrangements { get; set; }

    /// <summary>Gets or sets how many of those came out with sections.</summary>
    [JsonPropertyName("described")]
    public long Described { get; set; }

    /// <summary>Gets or sets how many files are queued or decoding right now.</summary>
    [JsonPropertyName("measuring")]
    public int Measuring { get; set; }

    /// <summary>Gets or sets whether the nightly sweep is switched on.</summary>
    [JsonPropertyName("sweepEnabled")]
    public bool SweepEnabled { get; set; }

    /// <summary>Gets or sets a sentence describing the state, for a human reading the response.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}

/// <summary>
/// Answers "how far along is it" in one request, which nothing else here does.
/// </summary>
/// <remarks>
/// Measurement is lazy: a track is decoded the first time something asks about it. That is
/// the right default for a library of tens of thousands, and it makes progress invisible —
/// short of asking about all of them two hundred at a time, or reading the database on the
/// server, there was no way to see how much had been done.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/analysis/coverage")]
[Produces(MediaTypeNames.Application.Json)]
public class CoverageController : ControllerBase
{
    private readonly AudioAnalysisService _service;
    private readonly SoundBoundsRepository _bounds;
    private readonly AudioAnalysisRepository _analysis;
    private readonly BeatGridRepository _grids;
    private readonly ArrangementRepository _arrangements;
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageController"/> class.
    /// </summary>
    /// <param name="service">The measurement service, for what is in flight.</param>
    /// <param name="bounds">The sound-bounds cache.</param>
    /// <param name="analysis">The loudness and tempo cache.</param>
    /// <param name="grids">The beat-grid cache.</param>
    /// <param name="arrangements">The arrangement cache.</param>
    /// <param name="libraryManager">Jellyfin's library, for the total.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public CoverageController(
        AudioAnalysisService service,
        SoundBoundsRepository bounds,
        AudioAnalysisRepository analysis,
        BeatGridRepository grids,
        ArrangementRepository arrangements,
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext)
    {
        _service = service;
        _bounds = bounds;
        _analysis = analysis;
        _grids = grids;
        _arrangements = arrangements;
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Reports how much of the library has been measured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts.</returns>
    /// <response code="200">The counts.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(CoverageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CoverageDto>> Get(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var tracks = _libraryManager.GetCount(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        });

        var (measuredGrids, gridded) = await _grids.CountAsync(cancellationToken).ConfigureAwait(false);
        var (measuredArrangements, described) = await _arrangements.CountAsync(cancellationToken).ConfigureAwait(false);

        var coverage = new CoverageDto
        {
            Tracks = tracks,
            SoundBounds = await _bounds.CountAsync(cancellationToken).ConfigureAwait(false),
            AudioAnalysis = await _analysis.CountAsync(cancellationToken).ConfigureAwait(false),
            BeatGrids = measuredGrids,
            Gridded = gridded,
            Arrangements = measuredArrangements,
            Described = described,
            Measuring = _service.InFlight,
            SweepEnabled = Plugin.Instance?.Configuration.EnableSoundBoundsSweep ?? false
        };

        coverage.Summary = Describe(coverage);
        return Ok(coverage);
    }

    /// <summary>
    /// Says what the numbers mean, so the answer is readable without the documentation.
    /// </summary>
    private static string Describe(CoverageDto coverage)
    {
        if (coverage.Tracks == 0)
        {
            return "No audio in the library yet.";
        }

        var percent = coverage.BeatGrids * 100.0 / coverage.Tracks;
        var progress = string.Create(
            CultureInfo.InvariantCulture,
            $"{coverage.BeatGrids} of {coverage.Tracks} tracks measured ({percent:F0}%), {coverage.Gridded} with a beat grid");

        if (coverage.Measuring > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{progress}. {coverage.Measuring} decoding or queued right now.");
        }

        if (coverage.BeatGrids >= coverage.Tracks)
        {
            return $"{progress}. Nothing outstanding.";
        }

        return coverage.SweepEnabled
            ? $"{progress}. Nothing running; the nightly sweep will pick up the rest."
            : $"{progress}. Nothing running. Tracks are measured when a client asks about them, or turn on the nightly sweep to do the library in one go.";
    }
}
