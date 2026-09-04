using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AoideSidecar.Match;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// One row of an import, as the client parsed it.
/// </summary>
public class ImportedTrackDto
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets every credited artist.</summary>
    [JsonPropertyName("artists")]
    public IReadOnlyList<string>? Artists { get; set; }

    /// <summary>Gets or sets the album, if the source had one.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets the duration in milliseconds, if known.</summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    /// <summary>Gets or sets the ISRC, if the source had one.</summary>
    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }
}

/// <summary>
/// The library track an import row was matched to, or null.
/// </summary>
public class MatchResultDto
{
    /// <summary>Gets or sets the matched Jellyfin item id, or null for no match.</summary>
    [JsonPropertyName("jellyfinId")]
    public string? JellyfinId { get; set; }

    /// <summary>Gets or sets the ranking score.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Gets or sets the title similarity.</summary>
    [JsonPropertyName("titleScore")]
    public double TitleScore { get; set; }

    /// <summary>Gets or sets the artist similarity.</summary>
    [JsonPropertyName("artistScore")]
    public double ArtistScore { get; set; }

    /// <summary>Gets or sets the duration similarity.</summary>
    [JsonPropertyName("durationScore")]
    public double DurationScore { get; set; }

    /// <summary>Gets or sets the matched track's title, so the client can show what it chose.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the matched track's artists.</summary>
    [JsonPropertyName("artists")]
    public IReadOnlyList<string>? Artists { get; set; }

    /// <summary>Gets or sets the matched track's album.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets the matched track's duration.</summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }
}

/// <summary>
/// Response body for a match: one result per input row, in input order.
/// </summary>
public class MatchResponseDto
{
    /// <summary>Gets or sets the results, aligned with the request rows.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<MatchResultDto?> Results { get; set; } = Array.Empty<MatchResultDto?>();

    /// <summary>Gets or sets how many rows found a track.</summary>
    [JsonPropertyName("matched")]
    public int Matched { get; set; }

    /// <summary>Gets or sets how many library tracks were searched.</summary>
    [JsonPropertyName("librarySize")]
    public int LibrarySize { get; set; }
}

/// <summary>
/// Matches an imported track list — a Spotify export, typically — against the library.
/// </summary>
/// <remarks>
/// The hard part of an import is not parsing it, it is finding a thousand rows in a
/// library of twenty thousand. From a phone that is a thousand round trips; here it is a
/// local lookup with the library already in memory. The client parses the source, sends
/// the rows once, builds a playlist from the hits, and shows the misses as not on the
/// server. Clients fall back to the same rules locally when this is unreachable.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/match")]
[Produces(MediaTypeNames.Application.Json)]
public class MatchController : ControllerBase
{
    private const int MaxRows = 5000;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<MatchController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchController"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for access scoping.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public MatchController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IAuthorizationContext authorizationContext,
        ILogger<MatchController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    /// <summary>
    /// Matches each row against the tracks the caller can see.
    /// </summary>
    /// <param name="rows">The imported rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per row, in order; null where nothing qualified.</returns>
    /// <response code="200">The results.</response>
    /// <response code="400">More rows than the limit, or no body.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost]
    [ProducesResponseType(typeof(MatchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MatchResponseDto>> Match(
        [FromBody] IReadOnlyList<ImportedTrackDto> rows,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (rows is null)
        {
            return Problem("Missing body", "Send a JSON array of rows.", StatusCodes.Status400BadRequest);
        }

        if (rows.Count > MaxRows)
        {
            return Problem(
                "Too many rows",
                $"{rows.Count} rows exceeds the limit of {MaxRows}. Split the import.",
                StatusCodes.Status400BadRequest);
        }

        var user = _userManager.GetUserById(authorization.UserId);
        var library = _libraryManager
            .GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true
            })
            .OfType<Audio>()
            .Select(ToLibraryTrack)
            .ToList();

        var matcher = new TrackMatcher(library);
        var results = new List<MatchResultDto?>(rows.Count);
        var matched = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = matcher.Match(new ImportedTrack(
                row?.Title,
                row?.Artists ?? Array.Empty<string>(),
                row?.Album,
                row?.DurationMs,
                row?.Isrc));

            if (match is null)
            {
                results.Add(null);
                continue;
            }

            matched++;
            results.Add(new MatchResultDto
            {
                JellyfinId = match.Track.Id,
                Confidence = match.Confidence,
                TitleScore = match.TitleScore,
                ArtistScore = match.ArtistScore,
                DurationScore = match.DurationScore,
                Title = match.Track.Title,
                Artists = match.Track.Artists,
                Album = match.Track.Album,
                DurationMs = match.Track.DurationMs
            });
        }

        _logger.LogInformation(
            "Matched {Matched} of {Rows} imported rows against {Library} tracks for {User}",
            matched,
            rows.Count,
            library.Count,
            authorization.UserId);

        return Ok(new MatchResponseDto
        {
            Results = results,
            Matched = matched,
            LibrarySize = library.Count
        });
    }

    private static LibraryTrack ToLibraryTrack(Audio audio)
    {
        // Track artists and album artists together, deduplicated: a compilation credits
        // the performer on the track and "Various Artists" on the album, and an import
        // may name either.
        var artists = audio.Artists
            .Concat(audio.AlbumArtists)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        audio.ProviderIds.TryGetValue("ISRC", out var isrc);

        return new LibraryTrack(
            audio.Id.ToString("N", CultureInfo.InvariantCulture),
            audio.Name,
            artists,
            audio.Album,
            audio.RunTimeTicks is { } ticks ? ticks / TimeSpan.TicksPerMillisecond : null,
            isrc);
    }

    private ObjectResult Problem(string title, string detail, int status) =>
        StatusCode(status, new ProblemDetails { Title = title, Detail = detail, Status = status });
}
