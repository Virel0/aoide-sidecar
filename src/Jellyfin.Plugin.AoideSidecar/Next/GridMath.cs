using Jellyfin.Plugin.AoideSidecar.Sound;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>
/// The arithmetic the clients do over a grid and an arrangement, ported so the planner
/// here reads them the same way. Each method mirrors one on the clients' <c>BeatGrid</c>
/// or <c>Arrangement</c>, by name.
/// </summary>
internal static class GridMath
{
    /// <summary>One beat's length in a segment.</summary>
    public static double BeatMs(this BeatSegment segment) => 60_000 / segment.Bpm;

    /// <summary>
    /// The segment holding a moment, or the nearest one if the moment falls outside every
    /// segment.
    /// </summary>
    public static BeatSegment? Segment(this BeatGrid grid, double ms) =>
        grid.Segments.FirstOrDefault(s => ms >= s.StartMs && ms < s.EndMs)
        ?? grid.Segments.LastOrDefault(s => s.StartMs <= ms)
        ?? grid.Segments.FirstOrDefault();

    /// <summary>The length of one bar at a moment, when the meter is known.</summary>
    public static double? BarMs(this BeatGrid grid, double ms)
    {
        if (grid.BeatsPerBar is not { } beatsPerBar || grid.Segment(ms) is not { } segment)
        {
            return null;
        }

        return segment.BeatMs() * beatsPerBar;
    }

    /// <summary>
    /// The downbeat at or before a moment, when the meter is known. Every segment's beat
    /// zero is a downbeat, so this is arithmetic within one segment.
    /// </summary>
    public static double? DownbeatAtOrBefore(this BeatGrid grid, double ms)
    {
        if (grid.BeatsPerBar is not { } beatsPerBar || grid.Segment(ms) is not { } segment)
        {
            return null;
        }

        var barMs = segment.BeatMs() * beatsPerBar;
        var bars = Math.Floor((ms - segment.AnchorMs) / barMs);
        return segment.AnchorMs + (bars * barMs);
    }

    /// <summary>
    /// How far off its own grid a track may sit and still be worth locking to, for a blend
    /// of this many seconds.
    /// </summary>
    public static double MaximumResidualMs(double blendSeconds)
    {
        if (blendSeconds <= 0)
        {
            return 0;
        }

        return Math.Min(40, 20 * (30 / blendSeconds));
    }

    /// <summary>Whether a blend of this length may be locked to this grid at this moment.</summary>
    public static bool CanLock(this BeatGrid grid, double ms, double blendSeconds)
    {
        if (grid.BeatsPerBar is null || grid.Segment(ms) is not { } segment)
        {
            return false;
        }

        if (segment.ResidualMs > MaximumResidualMs(blendSeconds))
        {
            return false;
        }

        // The blend must finish inside the segment it started in: crossing a boundary is
        // crossing a tempo change.
        return ms + (blendSeconds * 1000) <= segment.EndMs;
    }

    /// <summary>The section holding a moment.</summary>
    public static Section? SectionAt(this Arrangement arrangement, double ms) =>
        arrangement.Sections.FirstOrDefault(s => ms >= s.StartMs && ms < s.EndMs)
        ?? arrangement.Sections.LastOrDefault(s => s.StartMs <= ms);

    /// <summary>
    /// Whether anybody might be singing anywhere in this stretch. Unknown is yes.
    /// </summary>
    public static bool MaySing(this Arrangement arrangement, double from, double to)
    {
        if (arrangement.Vocals is null)
        {
            return true;
        }

        return arrangement.Vocals.Any(v => v.StartMs < to && v.EndMs > from);
    }

    /// <summary>The phrase boundary at or before a moment, when the track counts phrases.</summary>
    public static double? PhraseStartAtOrBefore(this Arrangement arrangement, double ms, double barMs)
    {
        if (arrangement.PhraseBars is not { } phraseBars
            || arrangement.PhraseAnchorMs is not { } anchor
            || barMs <= 0)
        {
            return null;
        }

        var phraseMs = barMs * phraseBars;
        var phrases = Math.Floor((ms - anchor) / phraseMs);
        return anchor + (phrases * phraseMs);
    }
}
