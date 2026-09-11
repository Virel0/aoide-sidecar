using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Next;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// What the client sends to ask what should play next.
/// </summary>
public class NextRequestDto
{
    /// <summary>Gets or sets the record playing — the one everything is asked to follow.</summary>
    [JsonPropertyName("seed")]
    public string? Seed { get; set; }

    /// <summary>Gets or sets what is already queued after the seed, in order.</summary>
    [JsonPropertyName("queue")]
    public List<string> Queue { get; set; } = new();

    /// <summary>Gets or sets what this device heard lately, newest first.</summary>
    [JsonPropertyName("recent")]
    public List<string> Recent { get; set; } = new();

    /// <summary>Gets or sets <c>infinity</c> or <c>autodj</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>Gets or sets how many to return.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

/// <summary>The five numbers behind one pick.</summary>
public class NextFactorsDto
{
    /// <summary>Gets or sets genre, artist and finish bias, clamped to 0…1.</summary>
    [JsonPropertyName("taste")]
    public double Taste { get; set; }

    /// <summary>Gets or sets 1 untouched, 0.5 by an artist heard lately, 0 heard lately.</summary>
    [JsonPropertyName("freshness")]
    public double Freshness { get; set; }

    /// <summary>Gets or sets whether it is the seed's kind: 1 sharing a genre, 0 tagged and not, 0.5 when either is untagged.</summary>
    [JsonPropertyName("kinship")]
    public double Kinship { get; set; }

    /// <summary>Gets or sets how alike the seed it is: tempo, key, energy.</summary>
    [JsonPropertyName("similarity")]
    public double Similarity { get; set; }

    /// <summary>Gets or sets the planner's score for following the record before it in the order. Null outside Auto DJ, or when either record is unread.</summary>
    [JsonPropertyName("mixability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Mixability { get; set; }

    /// <summary>Gets or sets how near it lands to the energy the set wants next.</summary>
    [JsonPropertyName("arc")]
    public double Arc { get; set; }
}

/// <summary>One record the ranking proposes.</summary>
public class NextCandidateDto
{
    /// <summary>Gets or sets the Jellyfin id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the score it was ranked on.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Gets or sets why.</summary>
    [JsonPropertyName("factors")]
    public NextFactorsDto Factors { get; set; } = new();
}

/// <summary>How much history the taste term stood on.</summary>
public class NextProfileDto
{
    /// <summary>Gets or sets how many finished plays were counted.</summary>
    [JsonPropertyName("events")]
    public int Events { get; set; }

    /// <summary>Gets or sets the start of the window, milliseconds since epoch.</summary>
    [JsonPropertyName("since")]
    public long Since { get; set; }
}

/// <summary>The answer.</summary>
public class NextResponseDto
{
    /// <summary>Gets or sets the chosen records, in the order they should play.</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<NextCandidateDto> Candidates { get; set; } = Array.Empty<NextCandidateDto>();

    /// <summary>Gets or sets what the taste term stood on.</summary>
    [JsonPropertyName("profile")]
    public NextProfileDto Profile { get; set; } = new();
}

/// <summary>
/// Chooses what plays next, from the whole library rather than a sample of it.
/// </summary>
/// <remarks>
/// Everything the answer needs is already here and nowhere else: the whole library, what
/// is measured about every track, and what this listener finished, skipped and flagged on
/// every device. The rule over it is the one the clients already follow, moved to where
/// the data is. Nothing is remembered between calls: the queue is the client's, and the
/// client sends it back.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/next")]
[Produces(MediaTypeNames.Application.Json)]
public class NextController : ControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 200;
    private const int MaxQueue = 1000;
    private const int MaxRecent = 40;

    private readonly ListeningRepository _listening;
    private readonly AudioAnalysisRepository _analysis;
    private readonly BeatGridRepository _grids;
    private readonly ArrangementRepository _arrangements;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextController"/> class.
    /// </summary>
    /// <param name="listening">Listens and flags from the op log.</param>
    /// <param name="analysis">The loudness and tempo cache.</param>
    /// <param name="grids">The beat-grid cache.</param>
    /// <param name="arrangements">The arrangement cache.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public NextController(
        ListeningRepository listening,
        AudioAnalysisRepository analysis,
        BeatGridRepository grids,
        ArrangementRepository arrangements,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IAuthorizationContext authorizationContext)
    {
        _listening = listening;
        _analysis = analysis;
        _grids = grids;
        _arrangements = arrangements;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Ranks the library for what should follow the seed.
    /// </summary>
    /// <param name="body">The seed, the queue, what was heard lately, the mode and a limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The best candidates, each with its score and factors.</returns>
    /// <response code="200">The ranking.</response>
    /// <response code="400">A malformed request.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="404">The seed is not a track this user can see.</response>
    /// <response code="503">Storage is unavailable.</response>
    [HttpPost]
    [ProducesResponseType(typeof(NextResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<NextResponseDto>> Post([FromBody] NextRequestDto body, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Seed))
        {
            return Problem(StatusCodes.Status400BadRequest, "Malformed request", "Send { seed, queue, recent, mode, limit }.");
        }

        var autoDj = body.Mode?.ToLowerInvariant() switch
        {
            null or "" or "infinity" => false,
            "autodj" => true,
            _ => (bool?)null
        };
        if (autoDj is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown mode", "mode must be \"infinity\" or \"autodj\".");
        }

        var limit = body.Limit ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit || body.Queue.Count > MaxQueue || body.Recent.Count > MaxRecent)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Out of range",
                $"limit must be 1 to {MaxLimit}; queue at most {MaxQueue} ids; recent at most {MaxRecent}.");
        }

        var user = _userManager.GetUserById(authorization.UserId);
        if (user is null)
        {
            return Unauthorized();
        }

        var seed = AudioIds.Normalise(body.Seed);
        var library = Library(user);
        if (!library.Any(t => string.Equals(t.Id, seed, StringComparison.OrdinalIgnoreCase)))
        {
            return Problem(StatusCodes.Status404NotFound, "Unknown seed", "The seed is not a track this user can see.");
        }

        var userId = authorization.UserId.ToString("N", CultureInfo.InvariantCulture);
        IReadOnlyList<PlayEvent> history;
        IReadOnlySet<string> notInterested;
        Dictionary<string, Measured> measured;
        try
        {
            history = await _listening.PlayEventsAsync(userId, cancellationToken).ConfigureAwait(false);
            notInterested = await _listening.NotInterestedAsync(userId, cancellationToken).ConfigureAwait(false);
            measured = await MeasuredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Storage unavailable", "The sidecar's database could not be read.");
        }

        var request = new NextRequest(
            seed,
            body.Queue.Select(AudioIds.Normalise).ToList(),
            body.Recent.Select(AudioIds.Normalise).ToList(),
            autoDj.Value,
            limit);

        var result = NextRanker.Rank(
            request, library, history, notInterested, measured, new PlannerMixability(measured),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return Ok(new NextResponseDto
        {
            Candidates = result.Candidates.Select(c => new NextCandidateDto
            {
                Id = c.Id,
                Score = c.Score,
                Factors = new NextFactorsDto
                {
                    Taste = c.Factors.Taste,
                    Freshness = c.Factors.Freshness,
                    Kinship = c.Factors.Kinship,
                    Similarity = c.Factors.Similarity,
                    Mixability = c.Factors.Mixability,
                    Arc = c.Factors.Arc
                }
            }).ToList(),
            Profile = new NextProfileDto { Events = result.ProfileEvents, Since = result.ProfileSince }
        });
    }

    /// <summary>
    /// Every audio track the user can see, described the way the clients describe one:
    /// album artist, or first artist, or "Unknown Artist", and its genre tags.
    /// </summary>
    private List<LibraryTrack> Library(Jellyfin.Database.Implementations.Entities.User user)
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true
            })
            .OfType<Audio>()
            .Select(audio => new LibraryTrack(
                audio.Id.ToString("N", CultureInfo.InvariantCulture),
                audio.AlbumArtists.FirstOrDefault() ?? audio.Artists.FirstOrDefault() ?? "Unknown Artist",
                audio.Genres ?? Array.Empty<string>()))
            .ToList();
    }

    private async Task<Dictionary<string, Measured>> MeasuredAsync(CancellationToken cancellationToken)
    {
        var analysis = await _analysis.AllAsync(cancellationToken).ConfigureAwait(false);
        var grids = await _grids.AllAsync(cancellationToken).ConfigureAwait(false);
        var arrangements = await _arrangements.AllAsync(cancellationToken).ConfigureAwait(false);

        var measured = new Dictionary<string, Measured>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in analysis.Keys.Concat(grids.Keys).Concat(arrangements.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var grid = grids.GetValueOrDefault(id);
            measured[id] = new Measured(
                analysis.GetValueOrDefault(id)?.Tempo,
                grid?.Grid,
                grid?.Key?.Camelot,
                arrangements.GetValueOrDefault(id)?.Arrangement);
        }

        return measured;
    }

    private ObjectResult Problem(int status, string title, string detail) =>
        StatusCode(status, new ProblemDetails { Title = title, Detail = detail, Status = status });
}
