using Jellyfin.Plugin.AoideSidecar.Sound;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>A track in the library, described by what the ranking needs.</summary>
/// <param name="Id">The Jellyfin id, normalised.</param>
/// <param name="Artist">Album artist, or first artist, or "Unknown Artist".</param>
/// <param name="Genres">Its genre tags.</param>
internal sealed record LibraryTrack(string Id, string Artist, IReadOnlyList<string> Genres);

/// <summary>One listen, as the clients recorded it.</summary>
/// <param name="JellyfinId">The track.</param>
/// <param name="StartedAt">Milliseconds since epoch.</param>
/// <param name="EndedAt">When it ended, or null while still open.</param>
/// <param name="Completed">Whether it reached the end.</param>
/// <param name="Skipped">Whether it was skipped away from.</param>
internal sealed record PlayEvent(string JellyfinId, long StartedAt, long? EndedAt, bool Completed, bool Skipped);

/// <summary>Everything measured about a track that the ranking reads.</summary>
/// <param name="Tempo">From audio analysis, or null.</param>
/// <param name="Grid">The beat grid, or null.</param>
/// <param name="Key">The Camelot key, or null.</param>
/// <param name="Arrangement">The arrangement, or null.</param>
internal sealed record Measured(Tempo? Tempo, BeatGrid? Grid, string? Key, Arrangement? Arrangement)
{
    /// <summary>Nothing measured.</summary>
    public static readonly Measured Nothing = new(null, null, null, null);
}

/// <summary>What the client asked.</summary>
/// <param name="Seed">The record playing.</param>
/// <param name="Queue">What is queued after it, in order.</param>
/// <param name="Recent">What this device heard lately, newest first.</param>
/// <param name="AutoDj">Whether a set is being programmed.</param>
/// <param name="Limit">How many to return.</param>
internal sealed record NextRequest(
    string Seed,
    IReadOnlyList<string> Queue,
    IReadOnlyList<string> Recent,
    bool AutoDj,
    int Limit);

/// <summary>The five factors behind one candidate's score.</summary>
internal sealed record NextFactors(double Taste, double Freshness, double Similarity, double? Mixability, double Arc);

/// <summary>One candidate, scored.</summary>
internal sealed record NextCandidate(string Id, double Score, NextFactors Factors);

/// <summary>The answer, and how much history it stood on.</summary>
internal sealed record NextResult(IReadOnlyList<NextCandidate> Candidates, int ProfileEvents, long ProfileSince);

/// <summary>
/// Chooses what plays next, from the whole library, by arithmetic everyone can read.
/// </summary>
/// <remarks>
/// <para>
/// Not a recommender. No embeddings, no learned model, no "explore" term. Five factors,
/// each explained in one sentence, summed with weights the clients already use; every
/// position it produces can be argued with line by line, and a bad pick can be diagnosed
/// from the five numbers rather than from a screen recording.
/// </para>
/// <para>
/// Deterministic: the same request against the same tables returns the same list. Ties
/// break on id. Variety comes from the freshness penalties, which is the right place for
/// it, because it is explainable. Nothing is remembered between calls.
/// </para>
/// </remarks>
internal static class NextRanker
{
    /// <summary>How far back taste is read.</summary>
    public const long WindowMs = 90L * 24 * 60 * 60 * 1000;

    /// <summary>The latest this many finished plays are what taste is built from.</summary>
    public const int Depth = 200;

    /// <summary>The last this many distinct tracks started count as heard lately.</summary>
    public const int RecentDepth = 40;

    /// <summary>The artists of the last this many queued records count as heard lately.</summary>
    public const int QueueArtistDepth = 3;

    /// <summary>Weight of similarity to the seed.</summary>
    public const double SimilarityWeight = 0.4;

    /// <summary>Weight of the arc.</summary>
    public const double ArcWeight = 0.2;

    /// <summary>What a pair the planner refuses is still worth: it can be crossfaded.</summary>
    public const double CrossfadeOnly = 0.15;

    /// <summary>Below this, a measured tempo does not describe its track well enough to compare.</summary>
    public const double MinimumStability = 0.5;

    /// <summary>
    /// Ranks the library for what should follow the seed.
    /// </summary>
    /// <param name="request">What the client asked.</param>
    /// <param name="library">Every track the listener can see, the seed included.</param>
    /// <param name="history">Every listen the server holds for this listener.</param>
    /// <param name="notInterested">Tracks flagged not interested.</param>
    /// <param name="measured">What is measured about each track, by id.</param>
    /// <param name="nowMs">The present, for the window.</param>
    /// <returns>The best, best first, with their factors.</returns>
    public static NextResult Rank(
        NextRequest request,
        IReadOnlyList<LibraryTrack> library,
        IReadOnlyList<PlayEvent> history,
        IReadOnlySet<string> notInterested,
        IReadOnlyDictionary<string, Measured> measured,
        long nowMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(notInterested);
        ArgumentNullException.ThrowIfNull(measured);

        var byId = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in library)
        {
            byId.TryAdd(track.Id, track);
        }

        var since = nowMs - WindowMs;
        var (profile, events) = Profile(history, byId, request, since);
        var finish = Finish(history);
        var seed = byId.GetValueOrDefault(request.Seed);
        var seedMeasured = measured.GetValueOrDefault(request.Seed) ?? Measured.Nothing;
        var target = ArcTarget(request, measured);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { request.Seed };
        excluded.UnionWith(request.Queue);
        excluded.UnionWith(request.Recent);

        var scored = new List<(NextCandidate Candidate, double Score)>();
        foreach (var track in library)
        {
            if (excluded.Contains(track.Id) || notInterested.Contains(track.Id))
            {
                continue;
            }

            var candidate = new TasteCandidate(track.Id, track.Artist, track.Genres);
            var taste = TasteRanking.Taste(candidate, profile, finish.GetValueOrDefault(track.Id));

            var heardLately = profile.Recent.Contains(track.Id);
            var artistHeardLately = profile.RecentArtists.Contains(track.Artist.ToLowerInvariant());

            var candidateMeasured = measured.GetValueOrDefault(track.Id) ?? Measured.Nothing;
            var similarity = Similarity(seed, seedMeasured, track, candidateMeasured);
            var mixability = request.AutoDj ? Mixability(seedMeasured, candidateMeasured) : null;
            var arc = Arc(target, candidateMeasured);

            var score = taste
                        + (similarity * SimilarityWeight)
                        + (mixability is { } mix ? mix * TasteRanking.CompatibilityWeight : 0)
                        + (arc * ArcWeight)
                        - (heardLately ? TasteRanking.RecentPenalty : 0)
                        - (artistHeardLately ? TasteRanking.SameArtistPenalty : 0);

            var factors = new NextFactors(
                Math.Clamp(taste, 0, 1),
                heardLately ? 0 : artistHeardLately ? 0.5 : 1,
                similarity,
                mixability,
                arc);

            scored.Add((new NextCandidate(track.Id, score, factors), score));
        }

        var best = TasteRanking.Best(scored, c => c.Id, request.Limit);
        return new NextResult(best, events, since);
    }

    /// <summary>
    /// Whether a measured tempo describes its track well enough to compare. Null is not
    /// held against a track: it means a server that never measured it.
    /// </summary>
    public static bool Holds(double? stability) => stability is null || stability >= MinimumStability;

    /// <summary>
    /// The length-weighted mean energy of a track, or null with no arrangement.
    /// </summary>
    public static double? MeanEnergy(Arrangement? arrangement)
    {
        if (arrangement is null || arrangement.Sections.Count == 0)
        {
            return null;
        }

        double weighted = 0;
        double length = 0;
        foreach (var section in arrangement.Sections)
        {
            var span = Math.Max(0, section.EndMs - section.StartMs);
            weighted += section.Energy * span;
            length += span;
        }

        return length > 0 ? weighted / length : arrangement.Sections.Average(s => s.Energy);
    }

    /// <summary>
    /// The listener, from finished plays: the latest two hundred inside ninety days.
    /// A skip is not a fact about taste; a finish is.
    /// </summary>
    private static (TasteProfile Profile, int Events) Profile(
        IReadOnlyList<PlayEvent> history,
        IReadOnlyDictionary<string, LibraryTrack> byId,
        NextRequest request,
        long since)
    {
        var finished = history
            .Where(e => e.Completed && e.StartedAt >= since)
            .OrderByDescending(e => e.StartedAt)
            .Take(Depth)
            .ToList();

        var genres = new Dictionary<string, double>(StringComparer.Ordinal);
        var artists = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in finished)
        {
            if (!byId.TryGetValue(play.JellyfinId, out var track))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(track.Artist))
            {
                var artist = track.Artist.ToLowerInvariant();
                artists[artist] = artists.GetValueOrDefault(artist) + 1;
            }

            foreach (var genre in track.Genres)
            {
                if (string.IsNullOrEmpty(genre))
                {
                    continue;
                }

                var name = genre.ToLowerInvariant();
                genres[name] = genres.GetValueOrDefault(name) + 1;
            }
        }

        // Everything lately, finished or not: a song skipped an hour ago is still a song
        // you have just heard. The request's own list is the truth about the last hour,
        // because sync lags by minutes and a set is judged in seconds.
        var recent = new HashSet<string>(request.Recent, StringComparer.OrdinalIgnoreCase);
        recent.UnionWith(history
            .GroupBy(e => e.JellyfinId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Latest: g.Max(e => e.StartedAt)))
            .OrderByDescending(g => g.Latest)
            .Take(RecentDepth)
            .Select(g => g.Id));

        var recentArtists = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in recent.Concat(request.Queue.TakeLast(QueueArtistDepth)))
        {
            if (byId.TryGetValue(id, out var track) && !string.IsNullOrEmpty(track.Artist))
            {
                recentArtists.Add(track.Artist.ToLowerInvariant());
            }
        }

        return (new TasteProfile(Normalised(genres), Normalised(artists), recent, recentArtists), finished.Count);
    }

    /// <summary>
    /// Normalised against the strongest rather than against the total: what is wanted is
    /// "how much of a favourite is this", and a listener with two genres and one with
    /// twenty should both have a 1.
    /// </summary>
    private static Dictionary<string, double> Normalised(Dictionary<string, double> counts)
    {
        if (counts.Count == 0)
        {
            return counts;
        }

        var top = counts.Values.Max();
        if (top <= 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        return counts.ToDictionary(c => c.Key, c => c.Value / top, StringComparer.Ordinal);
    }

    /// <summary>
    /// Finish counts per track: listens that reached a verdict, and how many reached the
    /// end. An event still open counts for nothing.
    /// </summary>
    private static Dictionary<string, FinishCounts> Finish(IReadOnlyList<PlayEvent> history)
    {
        var counts = new Dictionary<string, (int Starts, int Completed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var play in history)
        {
            if (play.EndedAt is null || !(play.Completed || play.Skipped))
            {
                continue;
            }

            var current = counts.GetValueOrDefault(play.JellyfinId);
            counts[play.JellyfinId] = (current.Starts + 1, current.Completed + (play.Completed ? 1 : 0));
        }

        return counts.ToDictionary(c => c.Key, c => new FinishCounts(c.Value.Starts, c.Value.Completed), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How alike the candidate is to the seed, from the measurements only the server has.
    /// Four parts, mean of those that can be answered; a part with no data on either side
    /// is left out of the mean rather than scored 0.5.
    /// </summary>
    private static double Similarity(LibraryTrack? seed, Measured seedMeasured, LibraryTrack candidate, Measured candidateMeasured)
    {
        var parts = new List<double>(4);

        if (seed is { Genres.Count: > 0 } && candidate.Genres.Count > 0)
        {
            var shared = seed.Genres.Any(g => candidate.Genres.Contains(g, StringComparer.OrdinalIgnoreCase));
            parts.Add(shared ? 1 : 0);
        }

        if (seedMeasured.Tempo is { } seedTempo && candidateMeasured.Tempo is { } candidateTempo
            && Holds(seedTempo.Stability) && Holds(candidateTempo.Stability)
            && seedTempo.Bpm > 0 && candidateTempo.Bpm > 0)
        {
            var ratio = candidateTempo.Bpm / seedTempo.Bpm;
            while (ratio > 1.4)
            {
                ratio /= 2;
            }

            while (ratio < 0.7)
            {
                ratio *= 2;
            }

            parts.Add(MixScore.TempoFactor(ratio));
        }

        parts.Add(MixScore.KeyFactor(seedMeasured.Key, candidateMeasured.Key));

        if (MeanEnergy(seedMeasured.Arrangement) is { } seedEnergy && MeanEnergy(candidateMeasured.Arrangement) is { } candidateEnergy)
        {
            parts.Add(1 - Math.Abs(seedEnergy - candidateEnergy));
        }

        return parts.Average();
    }

    /// <summary>
    /// Exactly the planner's score for the pair seed → candidate, or the crossfade figure
    /// when the planner refuses. Absent when either record is unread, so an unread record
    /// neither gains nor loses on this factor.
    /// </summary>
    private static double? Mixability(Measured seed, Measured candidate)
    {
        if (seed.Grid is null || seed.Arrangement is null || candidate.Grid is null || candidate.Arrangement is null)
        {
            return null;
        }

        var plan = DJPlanner.Plan(
            new MixRecord(seed.Grid, seed.Key, seed.Arrangement),
            new MixRecord(candidate.Grid, candidate.Key, candidate.Arrangement));

        return plan?.Score.Total ?? CrossfadeOnly;
    }

    /// <summary>
    /// What energy the set wants next, from the last four records it has been through, or
    /// null when nothing is known about them.
    /// </summary>
    /// <remarks>
    /// The four are the last four of what has played and will play before the candidate:
    /// the request's recent list oldest first, then the seed, then the queue. With an empty
    /// queue that is the seed and the three before it, which is how the ask put it; with a
    /// queue, the candidate follows the end of the queue and the arc is read from there.
    /// The reference the target is set against is the last of the four that has an energy.
    /// Rose three times in a row wants a breather; fell twice in a row wants to come back;
    /// otherwise hold.
    /// </remarks>
    private static double? ArcTarget(NextRequest request, IReadOnlyDictionary<string, Measured> measured)
    {
        var sequence = request.Recent.Reverse().Append(request.Seed).Concat(request.Queue).TakeLast(4);
        var energies = sequence
            .Select(id => MeanEnergy((measured.GetValueOrDefault(id) ?? Measured.Nothing).Arrangement))
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();

        if (energies.Count == 0)
        {
            return null;
        }

        var reference = energies[^1];
        var steps = new List<double>();
        for (var i = 1; i < energies.Count; i++)
        {
            steps.Add(energies[i] - energies[i - 1]);
        }

        if (steps.Count >= 3 && steps.TakeLast(3).All(s => s > 0))
        {
            return reference - 0.25;
        }

        if (steps.Count >= 2 && steps.TakeLast(2).All(s => s < 0))
        {
            return reference + 0.2;
        }

        return reference;
    }

    /// <summary>How near the candidate lands to what the set wants. 0.5 with nothing known.</summary>
    private static double Arc(double? target, Measured candidate)
    {
        if (target is not { } wanted || MeanEnergy(candidate.Arrangement) is not { } energy)
        {
            return 0.5;
        }

        return Math.Clamp(1 - Math.Abs(energy - wanted), 0, 1);
    }
}
