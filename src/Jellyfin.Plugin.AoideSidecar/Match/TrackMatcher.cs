using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AoideSidecar.Match;

/// <summary>
/// A track as described by an import — a Spotify export, a pasted list, anything.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Artists">Every credited artist. Empty when the source did not say.</param>
/// <param name="Album">The album, when the source carried one.</param>
/// <param name="DurationMs">Length in milliseconds, when known.</param>
/// <param name="Isrc">The ISRC, when the source carried one.</param>
public sealed record ImportedTrack(
    string? Title,
    IReadOnlyList<string> Artists,
    string? Album,
    long? DurationMs,
    string? Isrc);

/// <summary>
/// A track the library holds, reduced to what matching needs.
/// </summary>
/// <param name="Id">The Jellyfin item id.</param>
/// <param name="Title">The title.</param>
/// <param name="Artists">Track artists and album artists together.</param>
/// <param name="Album">The album.</param>
/// <param name="DurationMs">Length in milliseconds, when known.</param>
/// <param name="Isrc">The ISRC, when the tags carried one.</param>
public sealed record LibraryTrack(
    string Id,
    string? Title,
    IReadOnlyList<string> Artists,
    string? Album,
    long? DurationMs,
    string? Isrc);

/// <summary>
/// The best library track for an import row, with the scores that chose it.
/// </summary>
/// <param name="Track">The matched track.</param>
/// <param name="Confidence">The ranking score: 0.5·title + 0.35·artist + 0.15·duration.</param>
/// <param name="TitleScore">Title word-set similarity.</param>
/// <param name="ArtistScore">Best artist word-set similarity over every pair.</param>
/// <param name="DurationScore">1 within 5 s, 0.8 within 15 s, 0.9 when either side is unknown.</param>
public sealed record TrackMatch(
    LibraryTrack Track,
    double Confidence,
    double TitleScore,
    double ArtistScore,
    double DurationScore);

/// <summary>
/// Turns free-text metadata into comparable word sets.
/// </summary>
/// <remarks>
/// These rules are shared with the clients, which fall back to matching locally when
/// the server is unreachable. Any divergence here means the phone and the server
/// disagree about which tracks are missing, so the rules are deliberately ported
/// verbatim rather than improved: lower-case, fold diacritics, "&amp;" to "and", drop
/// bracketed groups, drop " - " suffixes that contain a noise word, then keep only
/// letters, digits and spaces.
/// </remarks>
public static partial class TrackNormalizer
{
    /// <summary>
    /// Words that mark a " - " suffix as decoration rather than part of the title.
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "feat", "featuring", "ft",
        "remaster", "remastered",
        "remix", "remixed", "mix",
        "live",
        "version",
        "edit", "edited",
        "deluxe",
        "mono", "stereo",
        "acoustic", "instrumental", "demo",
        "radio", "single", "bonus",
        "anniversary", "explicit", "clean",
        "extended", "reissue",
    };

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketedGroups();

    /// <summary>
    /// Normalises a title or artist name.
    /// </summary>
    /// <param name="raw">The text as the source gave it.</param>
    /// <returns>Lower-case letters, digits and single spaces; empty for nothing usable.</returns>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = FoldDiacritics(raw.ToLowerInvariant()).Replace("&", " and ", StringComparison.Ordinal);
        text = BracketedGroups().Replace(text, " ");

        // " - Remastered 2011", " - Live at Wembley": decoration Spotify appends to the
        // title. " - Part 2" is not, because none of its words are noise, so it stays.
        var segments = text.Split(" - ", StringSplitOptions.None);
        if (segments.Length > 1)
        {
            var kept = new List<string>(segments.Length) { segments[0] };
            for (var i = 1; i < segments.Length; i++)
            {
                if (!Words(segments[i]).Overlaps(NoiseWords))
                {
                    kept.Add(segments[i]);
                }
            }

            text = string.Join(' ', kept);
        }

        return string.Join(' ', Words(text));
    }

    /// <summary>
    /// The distinct words of a normalised string, in no particular order.
    /// </summary>
    /// <param name="raw">Text to split; normalised first if it has not been.</param>
    /// <returns>The word set.</returns>
    public static HashSet<string> WordSet(string? raw) => Words(Normalize(raw));

    // Keeps letters and digits from any script, not just ASCII: a Cyrillic or Japanese
    // title is still a title, and folding only strips combining marks, not scripts.
    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string FoldDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>
/// Finds the library track an import row most plausibly refers to.
/// </summary>
/// <remarks>
/// <para>
/// Scores are shared with the clients, which fall back to the same rules locally. Per
/// field, two word sets score 1 when equal, 0.9 when one contains the other, and their
/// Jaccard index otherwise. Artist takes the best score over every pair of credited
/// names, because "Artist A, Artist B" and "Artist B feat. Artist A" are the same
/// record. Duration is 1 within five seconds, 0.8 within fifteen, and rejects beyond
/// that; 0.9 when either side does not know.
/// </para>
/// <para>
/// A row is accepted when title ≥ 0.75, artist ≥ 0.7 and duration did not reject. With
/// no artist to compare on one side or the other, only an exact title will do — a bare
/// title is too little to match fuzzily. Candidates are ranked by
/// 0.5·title + 0.35·artist + 0.15·duration.
/// </para>
/// </remarks>
public sealed class TrackMatcher
{
    private const double TitleFloor = 0.75;
    private const double ArtistFloor = 0.7;
    private const double UnknownScore = 0.9;

    private readonly IReadOnlyList<IndexedTrack> _tracks;
    private readonly Dictionary<string, List<int>> _byTitleWord = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byIsrc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackMatcher"/> class over a library.
    /// </summary>
    /// <remarks>
    /// Builds an index from title words to tracks, so a row is scored only against
    /// tracks sharing at least one title word. That loses nothing: subset and Jaccard
    /// scores are both zero without a shared word, so anything that could pass the
    /// title floor is in the candidate set.
    /// </remarks>
    /// <param name="library">Every track the caller may see.</param>
    public TrackMatcher(IReadOnlyList<LibraryTrack> library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var indexed = new List<IndexedTrack>(library.Count);
        for (var i = 0; i < library.Count; i++)
        {
            var track = library[i];
            var entry = new IndexedTrack(
                track,
                TrackNormalizer.WordSet(track.Title),
                track.Artists.Select(TrackNormalizer.WordSet).Where(w => w.Count > 0).ToList());
            indexed.Add(entry);

            foreach (var word in entry.TitleWords)
            {
                if (!_byTitleWord.TryGetValue(word, out var list))
                {
                    list = new List<int>();
                    _byTitleWord[word] = list;
                }

                list.Add(i);
            }

            if (!string.IsNullOrWhiteSpace(track.Isrc))
            {
                _byIsrc.TryAdd(track.Isrc.Trim(), i);
            }
        }

        _tracks = indexed;
    }

    /// <summary>
    /// Gets the number of library tracks indexed.
    /// </summary>
    public int LibrarySize => _tracks.Count;

    /// <summary>
    /// Finds the best match for one imported row.
    /// </summary>
    /// <param name="import">The row.</param>
    /// <returns>The match, or null when nothing in the library qualifies.</returns>
    public TrackMatch? Match(ImportedTrack import)
    {
        ArgumentNullException.ThrowIfNull(import);

        // An ISRC identifies a recording exactly. Rare in Jellyfin tags, decisive when
        // present on both sides.
        if (!string.IsNullOrWhiteSpace(import.Isrc) && _byIsrc.TryGetValue(import.Isrc.Trim(), out var exact))
        {
            var hit = _tracks[exact];
            return new TrackMatch(hit.Track, 1, 1, 1, 1);
        }

        var titleWords = TrackNormalizer.WordSet(import.Title);
        if (titleWords.Count == 0)
        {
            return null;
        }

        var artistWords = import.Artists
            .Select(TrackNormalizer.WordSet)
            .Where(w => w.Count > 0)
            .ToList();

        var candidates = new HashSet<int>();
        foreach (var word in titleWords)
        {
            if (_byTitleWord.TryGetValue(word, out var list))
            {
                candidates.UnionWith(list);
            }
        }

        TrackMatch? best = null;
        foreach (var index in candidates)
        {
            var candidate = Score(_tracks[index], titleWords, artistWords, import.DurationMs);
            if (candidate is not null && (best is null || candidate.Confidence > best.Confidence))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static TrackMatch? Score(
        IndexedTrack track,
        HashSet<string> titleWords,
        List<HashSet<string>> artistWords,
        long? durationMs)
    {
        var duration = DurationScore(durationMs, track.Track.DurationMs);
        if (duration <= 0)
        {
            return null;
        }

        var title = SetScore(titleWords, track.TitleWords);

        var comparable = artistWords.Count > 0 && track.ArtistWords.Count > 0;
        if (!comparable)
        {
            // Nothing to tell two same-titled songs apart, so only an exact title is
            // trusted, and the artist component contributes nothing to the rank.
            return title >= 1
                ? new TrackMatch(track.Track, Rank(title, 0, duration), title, 0, duration)
                : null;
        }

        var artist = 0.0;
        foreach (var mine in artistWords)
        {
            foreach (var theirs in track.ArtistWords)
            {
                artist = Math.Max(artist, SetScore(mine, theirs));
            }
        }

        return title >= TitleFloor && artist >= ArtistFloor
            ? new TrackMatch(track.Track, Rank(title, artist, duration), title, artist, duration)
            : null;
    }

    private static double Rank(double title, double artist, double duration) =>
        (0.5 * title) + (0.35 * artist) + (0.15 * duration);

    private static double SetScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        if (a.SetEquals(b))
        {
            return 1;
        }

        if (a.IsSubsetOf(b) || b.IsSubsetOf(a))
        {
            return 0.9;
        }

        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }

    private static double DurationScore(long? a, long? b)
    {
        if (a is null || b is null)
        {
            return UnknownScore;
        }

        var difference = Math.Abs(a.Value - b.Value);
        if (difference <= 5_000)
        {
            return 1;
        }

        return difference <= 15_000 ? 0.8 : 0;
    }

    private sealed record IndexedTrack(
        LibraryTrack Track,
        HashSet<string> TitleWords,
        List<HashSet<string>> ArtistWords);
}
