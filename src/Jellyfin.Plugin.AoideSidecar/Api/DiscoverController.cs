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
/// The factors behind one Discover pick. Only taste is measured; the rest are
/// <c>/aoide/next</c>'s, null because Discover has no seed, and freshness 1 because
/// nothing in the pool has been heard.
/// </summary>
public class DiscoverFactorsDto
{
    /// <summary>Gets or sets genre and artist, clamped to 0…1.</summary>
    [JsonPropertyName("taste")]
    public double Taste { get; set; }

    /// <summary>Gets or sets nothing: there is no seed to be kin to.</summary>
    [JsonPropertyName("kinship")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Kinship { get; set; }

    /// <summary>Gets or sets 1: nothing in the pool has been heard.</summary>
    [JsonPropertyName("freshness")]
    public double Freshness { get; set; } = 1;

    /// <summary>Gets or sets nothing: there is no seed to be similar to.</summary>
    [JsonPropertyName("similarity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Similarity { get; set; }

    /// <summary>Gets or sets nothing: there is no record before it.</summary>
    [JsonPropertyName("mixability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Mixability { get; set; }

    /// <summary>Gets or sets nothing: there is no set to shape.</summary>
    [JsonPropertyName("arc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Arc { get; set; }
}

/// <summary>One record Discover proposes.</summary>
public class DiscoverCandidateDto
{
    /// <summary>Gets or sets the Jellyfin id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the score it was ranked on.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Gets or sets why.</summary>
    [JsonPropertyName("factors")]
    public DiscoverFactorsDto Factors { get; set; } = new();
}

/// <summary>The week's list.</summary>
public class DiscoverResponseDto
{
    /// <summary>Gets or sets the ISO week, UTC, e.g. <c>2026-W43</c>. The list changes when this does.</summary>
    [JsonPropertyName("week")]
    public string Week { get; set; } = string.Empty;

    /// <summary>Gets or sets the records, best first.</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<DiscoverCandidateDto> Candidates { get; set; } = Array.Empty<DiscoverCandidateDto>();

    /// <summary>Gets or sets what the taste term stood on.</summary>
    [JsonPropertyName("profile")]
    public NextProfileDto Profile { get; set; } = new();
}

/// <summary>
/// The records this listener owns and has not heard through, chosen by their taste, the
/// same all week.
/// </summary>
/// <remarks>
/// Jellyfin's own play count is not used: it counts a skip at four seconds as a play, and
/// knows only what reached the server. The op log holds the finishes and skips from every
/// device.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/discover")]
[Produces(MediaTypeNames.Application.Json)]
public class DiscoverController : ControllerBase
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    private readonly ListeningRepository _listening;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverController"/> class.
    /// </summary>
    /// <param name="listening">Listens and flags from the op log.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users, for visibility.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public DiscoverController(
        ListeningRepository listening,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IAuthorizationContext authorizationContext)
    {
        _listening = listening;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// This week's records owned and not yet heard, ranked by taste.
    /// </summary>
    /// <param name="limit">How many, 1 to 100; 30 when absent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The week, the records with their factors, and the profile.</returns>
    /// <response code="200">The list.</response>
    /// <response code="400">A limit out of range.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="503">Storage is unavailable.</response>
    [HttpGet]
    [ProducesResponseType(typeof(DiscoverResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DiscoverResponseDto>> Get([FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var wanted = limit ?? DefaultLimit;
        if (wanted < 1 || wanted > MaxLimit)
        {
            return Problem(StatusCodes.Status400BadRequest, "Out of range", $"limit must be 1 to {MaxLimit}.");
        }

        var user = _userManager.GetUserById(authorization.UserId);
        if (user is null)
        {
            return Unauthorized();
        }

        var userId = authorization.UserId.ToString("N", CultureInfo.InvariantCulture);
        IReadOnlyList<PlayEvent> history;
        IReadOnlySet<string> notInterested;
        try
        {
            history = await _listening.PlayEventsAsync(userId, cancellationToken).ConfigureAwait(false);
            notInterested = await _listening.NotInterestedAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Storage unavailable", "The sidecar's database could not be read.");
        }

        var result = DiscoverRanker.Rank(
            userId, Library(user), history, notInterested, wanted, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return Ok(new DiscoverResponseDto
        {
            Week = result.Week,
            Candidates = result.Candidates.Select(c => new DiscoverCandidateDto
            {
                Id = c.Id,
                Score = c.Score,
                Factors = new DiscoverFactorsDto { Taste = c.Taste }
            }).ToList(),
            Profile = new NextProfileDto { Events = result.ProfileEvents, Since = result.ProfileSince }
        });
    }

    /// <summary>
    /// Every audio track the user can see: the taste rule's artist (album artist, or first
    /// artist, or "Unknown Artist"), the track's own artist for the cap, and its album.
    /// </summary>
    private List<DiscoverTrack> Library(Jellyfin.Database.Implementations.Entities.User user)
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true
            })
            .OfType<Audio>()
            .Select(audio => new DiscoverTrack(
                new LibraryTrack(
                    audio.Id.ToString("N", CultureInfo.InvariantCulture),
                    audio.AlbumArtists.FirstOrDefault() ?? audio.Artists.FirstOrDefault() ?? "Unknown Artist",
                    audio.Genres ?? Array.Empty<string>()),
                audio.Artists.FirstOrDefault() ?? audio.AlbumArtists.FirstOrDefault() ?? "Unknown Artist",
                audio.AlbumEntity?.Id.ToString("N", CultureInfo.InvariantCulture)))
            .ToList();
    }

    private ObjectResult Problem(int status, string title, string detail) =>
        StatusCode(status, new ProblemDetails { Title = title, Detail = detail, Status = status });
}
