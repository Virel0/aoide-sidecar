using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>A track as Discover sees it: what taste reads, and what the caps count.</summary>
/// <param name="Track">Id, album artist (the taste rule) and genres.</param>
/// <param name="TrackArtist">The track's own first artist, for the per-artist cap.</param>
/// <param name="Album">The album's id, or null when the track has none.</param>
internal sealed record DiscoverTrack(LibraryTrack Track, string TrackArtist, string? Album);

/// <summary>One record proposed, and its taste.</summary>
internal sealed record DiscoverCandidate(string Id, double Score, double Taste);

/// <summary>The week's list, and what it stood on.</summary>
internal sealed record DiscoverResult(string Week, IReadOnlyList<DiscoverCandidate> Candidates, int ProfileEvents, long ProfileSince);

/// <summary>
/// The records this listener owns and has not heard through, ranked by their taste, the
/// same all week.
/// </summary>
/// <remarks>
/// <para>
/// The ranking is <see cref="NextRanker"/>'s taste term pointed at a different pool, with
/// no seed and so nothing to be kin to, similar to or mix from.
/// </para>
/// <para>
/// The same all week because it reads the week as it began: plays from before Monday
/// 00:00 UTC, both for the pool and for taste. A record finished on Tuesday stays on the
/// list until the week turns, as it would on a printed one. Flags are read as they are
/// now — a record marked not interested leaves at once.
/// </para>
/// </remarks>
internal static class DiscoverRanker
{
    /// <summary>A record started this many times and never finished has been answered.</summary>
    public const int AnsweredStarts = 3;

    /// <summary>At most this many records by one artist.</summary>
    public const int PerArtist = 2;

    /// <summary>At most this many from one album.</summary>
    public const int PerAlbum = 3;

    /// <summary>
    /// Ranks the pool for the week containing <paramref name="nowMs"/>.
    /// </summary>
    /// <param name="userId">The listener, normalised; part of the week's shuffle.</param>
    /// <param name="library">Every track the listener can see.</param>
    /// <param name="history">Every listen the server holds for this listener.</param>
    /// <param name="notInterested">Tracks flagged not interested.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="nowMs">The present.</param>
    /// <returns>The list, best first.</returns>
    public static DiscoverResult Rank(
        string userId,
        IReadOnlyList<DiscoverTrack> library,
        IReadOnlyList<PlayEvent> history,
        IReadOnlySet<string> notInterested,
        int limit,
        long nowMs)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(notInterested);

        var (week, weekStart) = Week(nowMs);
        var before = history.Where(e => e.StartedAt < weekStart).ToList();

        var byId = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in library)
        {
            byId.TryAdd(entry.Track.Id, entry.Track);
        }

        var since = weekStart - NextRanker.WindowMs;
        var (genres, artists, events) = NextRanker.Listened(before, byId, since);
        var profile = new TasteProfile(genres, artists, new HashSet<string>(), new HashSet<string>());

        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var play in before)
        {
            if (play.Completed)
            {
                finished.Add(play.JellyfinId);
            }

            starts[play.JellyfinId] = starts.GetValueOrDefault(play.JellyfinId) + 1;
        }

        var scored = new List<(DiscoverTrack Entry, double Score, string Shuffle)>();
        foreach (var entry in library)
        {
            var id = entry.Track.Id;
            if (notInterested.Contains(id) || finished.Contains(id) || starts.GetValueOrDefault(id) >= AnsweredStarts)
            {
                continue;
            }

            // No finish bias: nothing in the pool has three decided listens, so the rate
            // is always unknown and the bias always 0.
            var candidate = new TasteCandidate(id, entry.Track.Artist, entry.Track.Genres);
            var taste = TasteRanking.Taste(candidate, profile, null);
            scored.Add((entry, taste, Shuffle(week, userId, id)));
        }

        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        var perAlbum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var chosen = new List<DiscoverCandidate>();
        foreach (var (entry, score, _) in scored
                     .OrderByDescending(s => s.Score)
                     .ThenBy(s => s.Shuffle, StringComparer.Ordinal)
                     .ThenBy(s => s.Entry.Track.Id, StringComparer.Ordinal))
        {
            if (chosen.Count >= limit)
            {
                break;
            }

            var artist = entry.TrackArtist.ToLowerInvariant();
            if (perArtist.GetValueOrDefault(artist) >= PerArtist
                || (entry.Album is { } full && perAlbum.GetValueOrDefault(full) >= PerAlbum))
            {
                continue;
            }

            perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            if (entry.Album is { } album)
            {
                perAlbum[album] = perAlbum.GetValueOrDefault(album) + 1;
            }

            chosen.Add(new DiscoverCandidate(entry.Track.Id, score, Math.Clamp(score, 0, 1)));
        }

        return new DiscoverResult(week, chosen, events, since);
    }

    /// <summary>
    /// The ISO week containing the moment, in UTC, as <c>2026-W43</c>, and when it began.
    /// </summary>
    /// <param name="nowMs">Milliseconds since epoch.</param>
    /// <returns>The week's name and its Monday 00:00 UTC, in milliseconds.</returns>
    public static (string Name, long StartMs) Week(long nowMs)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        var year = ISOWeek.GetYear(now);
        var week = ISOWeek.GetWeekOfYear(now);
        var monday = DateTime.SpecifyKind(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday), DateTimeKind.Utc);
        var name = string.Create(CultureInfo.InvariantCulture, $"{year:D4}-W{week:D2}");
        return (name, new DateTimeOffset(monday).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// The week's shuffle position for a track: the lowercase hex SHA-256 of
    /// <c>week:user:track</c>, compared as a string. Different every week, the same on
    /// every device, and nothing to port but a hash.
    /// </summary>
    /// <param name="week">The week's name.</param>
    /// <param name="userId">The listener.</param>
    /// <param name="trackId">The track.</param>
    /// <returns>Sixty-four hex digits.</returns>
    public static string Shuffle(string week, string userId, string trackId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{week}:{userId}:{trackId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
