namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>
/// What this listener has actually been listening to, as numbers something can sort by.
/// A port of the clients' <c>TasteProfile</c>.
/// </summary>
/// <param name="Genres">Genre, lowercased, to how strongly it figures, 0…1 with the strongest at 1.</param>
/// <param name="Artists">The same for artists.</param>
/// <param name="Recent">Heard recently enough that hearing it again would feel like a repeat.</param>
/// <param name="RecentArtists">Whose records those were, lowercased.</param>
internal sealed record TasteProfile(
    IReadOnlyDictionary<string, double> Genres,
    IReadOnlyDictionary<string, double> Artists,
    IReadOnlySet<string> Recent,
    IReadOnlySet<string> RecentArtists)
{
    /// <summary>A profile with nothing in it.</summary>
    public static readonly TasteProfile Empty = new(
        new Dictionary<string, double>(),
        new Dictionary<string, double>(),
        new HashSet<string>(),
        new HashSet<string>());

    /// <summary>Gets a value indicating whether there is any history to speak from.</summary>
    public bool IsEmpty => Genres.Count == 0 && Artists.Count == 0;
}

/// <summary>
/// One song being considered, described by the little that matters here.
/// </summary>
/// <param name="Id">The Jellyfin id.</param>
/// <param name="Artist">Album artist, or first artist, or "Unknown Artist" — the clients' rule.</param>
/// <param name="Genres">Its genre tags, as tagged.</param>
internal sealed record TasteCandidate(string Id, string Artist, IReadOnlyList<string> Genres);

/// <summary>
/// Listens that reached a verdict, and how many of them reached the end.
/// </summary>
/// <param name="Starts">Played through or skipped. An event still open counts for nothing.</param>
/// <param name="Completed">Of those, the ones that reached the end.</param>
internal sealed record FinishCounts(int Starts, int Completed)
{
    /// <summary>Below this many decided listens the figure is noise.</summary>
    public const int MinimumSample = 3;

    /// <summary>Gets the fraction finished, or null when there is not enough to say.</summary>
    public double? Rate => Starts >= MinimumSample ? Completed / (double)Starts : null;
}

/// <summary>
/// Puts candidate songs in the order this listener is most likely to want them. A port of
/// the clients' <c>TasteRanking</c>, held to their sixteen-row parity table.
/// </summary>
/// <remarks>
/// Not a recommender and not trying to be. Everything here is arithmetic over what the
/// history already says, which means every position it produces can be explained in one
/// sentence — and when it is wrong, it is wrong in a way somebody can point at.
/// </remarks>
internal static class TasteRanking
{
    /// <summary>How much of the score a familiar genre is worth.</summary>
    public const double GenreWeight = 0.45;

    /// <summary>How much a familiar artist is worth. Less than genre, on purpose.</summary>
    public const double ArtistWeight = 0.3;

    /// <summary>How far a track's own finish rate can pull it either way.</summary>
    public const double FinishWeight = 0.3;

    /// <summary>
    /// What it costs to have been heard recently. Wider than the whole of the rest of the
    /// scale, which puts everything heard lately below everything that has not been.
    /// </summary>
    public const double RecentPenalty = 1.4;

    /// <summary>
    /// What it costs to be by an artist heard lately. Smaller than the record's own
    /// penalty, and larger than the artist bonus.
    /// </summary>
    public const double SameArtistPenalty = 0.4;

    /// <summary>How much a record's mixability with the one playing is worth.</summary>
    public const double CompatibilityWeight = 0.6;

    /// <summary>
    /// The taste part of the score, before penalties: genre, artist and finish bias.
    /// </summary>
    /// <param name="candidate">The song.</param>
    /// <param name="profile">The listener.</param>
    /// <param name="finish">The song's own finish counts, or null with no history.</param>
    /// <returns>About −0.3 to 1.05.</returns>
    public static double Taste(TasteCandidate candidate, TasteProfile profile, FinishCounts? finish)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        // The best-matching genre rather than the sum of all of them.
        double genre = 0;
        foreach (var tag in candidate.Genres)
        {
            if (profile.Genres.TryGetValue(tag.ToLowerInvariant(), out var value) && value > genre)
            {
                genre = value;
            }
        }

        var artist = profile.Artists.TryGetValue(candidate.Artist.ToLowerInvariant(), out var known) ? known : 0;

        // A track with too little history is neither promoted nor punished.
        var finishBias = (finish?.Rate ?? 0.5) - 0.5;

        return (genre * GenreWeight) + (artist * ArtistWeight) + (finishBias * 2 * FinishWeight);
    }

    /// <summary>
    /// The clients' whole score: taste, the two penalties, and mixability when a set is
    /// being programmed. The order of operations is theirs, so the doubles land on the
    /// same bits.
    /// </summary>
    /// <param name="candidate">The song.</param>
    /// <param name="profile">The listener.</param>
    /// <param name="finish">The song's own finish counts, or null.</param>
    /// <param name="compatibility">The planner's score for following the record playing, or null when a set is not being programmed.</param>
    /// <returns>The score.</returns>
    public static double Score(TasteCandidate candidate, TasteProfile profile, FinishCounts? finish, double? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        var score = Taste(candidate, profile, finish);
        if (profile.Recent.Contains(candidate.Id))
        {
            score -= RecentPenalty;
        }

        if (profile.RecentArtists.Contains(candidate.Artist.ToLowerInvariant()))
        {
            score -= SameArtistPenalty;
        }

        if (compatibility is { } mixable)
        {
            score += Math.Max(0, Math.Min(1, mixable)) * CompatibilityWeight;
        }

        return score;
    }

    /// <summary>
    /// Ties break on id so that the same library and the same history produce the same
    /// queue twice.
    /// </summary>
    /// <typeparam name="T">Whatever is being ranked.</typeparam>
    /// <param name="scored">Items with their scores.</param>
    /// <param name="id">How to read an item's id.</param>
    /// <param name="limit">How many to keep.</param>
    /// <returns>The best, best first.</returns>
    public static List<T> Best<T>(IEnumerable<(T Item, double Score)> scored, Func<T, string> id, int limit)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(id);

        if (limit <= 0)
        {
            return new List<T>();
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => id(s.Item), StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.Item)
            .ToList();
    }
}
