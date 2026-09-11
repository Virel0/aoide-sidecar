using Jellyfin.Plugin.AoideSidecar.Sound;

namespace Jellyfin.Plugin.AoideSidecar.Next;

/// <summary>How the outgoing record leaves.</summary>
internal enum MixStyle
{
    /// <summary>The standard blend, for pairs that agree.</summary>
    Blend,

    /// <summary>The filter fade, for pairs that clash in key or where one is singing.</summary>
    FilterFade,

    /// <summary>No overlap at all.</summary>
    Cut,

    /// <summary>A cut with the last beat left ringing.</summary>
    EchoOut
}

/// <summary>
/// A mix: two records beat-matched, bar-aligned and handed over on a downbeat. A port of
/// the clients' <c>DJTransition</c>.
/// </summary>
internal sealed record DJTransition(
    double OutgoingStartMs,
    double IncomingStartMs,
    int Bars,
    double Seconds,
    double TargetBpm,
    double OutgoingRate,
    double IncomingRate,
    double RestoreSeconds,
    bool IsKeyCompatible,
    MixScore Score,
    MixStyle Style);

/// <summary>
/// A record as the planner sees it: its grid, its key, and what it is made of.
/// </summary>
/// <param name="Grid">The beat grid.</param>
/// <param name="Key">The Camelot key, or null.</param>
/// <param name="Arrangement">The arrangement, or null when the server has not read it.</param>
internal sealed record MixRecord(BeatGrid Grid, string? Key, Arrangement? Arrangement);

/// <summary>
/// Plans a mix between two records, or refuses. The third port of the clients'
/// <c>DJPlanner</c> — Swift and TypeScript exist — and the one that must not drift: the
/// ranking's opinion of a pair and the client's plan for it have to be the same number, or
/// the set will book a mix the ranking did not choose it for. It is held to the clients'
/// forty-pair parity table, verbatim.
/// </summary>
internal static class DJPlanner
{
    /// <summary>The shortest a mix is; anything less is a crossfade.</summary>
    public const int MinimumBars = 8;

    /// <summary>How far a record may be bent to meet another.</summary>
    public const double MaximumStretch = 0.06;

    /// <summary>How long the incoming record takes to find its own tempo again.</summary>
    public const double RestoreSeconds = 8.0;

    /// <summary>The lengths tried, longest first.</summary>
    private static readonly int[] Lengths = { 32, 16, 8 };

    /// <summary>
    /// Plans a mix, or returns null — which means crossfade.
    /// </summary>
    /// <param name="outgoing">The record playing now.</param>
    /// <param name="incoming">The one that follows.</param>
    /// <param name="notBeforeMs">The earliest point in the outgoing track the mix may start.</param>
    /// <param name="outgoingExitMs">Where the set says the outgoing record leaves, or null for its own mix-out point.</param>
    /// <returns>The plan, or null.</returns>
    public static DJTransition? Plan(
        MixRecord outgoing,
        MixRecord incoming,
        double notBeforeMs = 0,
        double? outgoingExitMs = null)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        // Both records have to have been read. Matching tempo is not a substitute for
        // knowing the arrangement.
        if (outgoing.Arrangement is null || incoming.Arrangement is null)
        {
            return null;
        }

        var outMixOut = outgoingExitMs ?? outgoing.Grid.MixOutMs;
        var inMixIn = incoming.Grid.MixInMs;
        if (outMixOut is not { } outExit
            || inMixIn is not { } inEntry
            || outgoing.Grid.Segment(outExit) is not { } outSegment
            || outgoing.Grid.BeatsPerBar is null
            || incoming.Grid.BeatsPerBar is null
            || outgoing.Grid.BarMs(outExit) is not { } outBar
            || incoming.Grid.BarMs(inEntry) is not { } inBar)
        {
            return null;
        }

        // The bend, folded into one octave.
        if (BendableRate(incoming.Grid.Segment(inEntry)?.Bpm ?? 0, outSegment.Bpm) is not { } rate)
        {
            return null;
        }

        // Where the incoming record could come in: its mix-in point always; and the start
        // of every build, intro and breakdown as well.
        var entries = new List<double> { EntryPoint(inEntry, incoming.Grid, incoming.Arrangement, inBar) };
        foreach (var section in incoming.Arrangement.Sections)
        {
            if (section.Kind is SectionKind.Build or SectionKind.Intro or SectionKind.Breakdown)
            {
                entries.Add(EntryPoint(section.StartMs, incoming.Grid, incoming.Arrangement, inBar));
            }
        }

        // Best score wins; on a tie, the earlier entry, which is the thinner one.
        DJTransition? best = null;
        foreach (var entry in entries.Distinct().OrderBy(e => e))
        {
            var candidate = PlanEntry(
                entry, rate, outgoing, incoming, outExit, outBar, outSegment.Bpm, notBeforeMs);
            if (candidate is not null && (best is null || candidate.Score.Total > best.Score.Total))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The rate the incoming deck runs at to sit on the outgoing tempo, or null if that is
    /// further than a record may be bent. Folded into one octave first.
    /// </summary>
    public static double? BendableRate(double bpm, double target)
    {
        if (bpm <= 0 || target <= 0 || !double.IsFinite(bpm) || !double.IsFinite(target))
        {
            return null;
        }

        var ratio = target / bpm;
        while (ratio > 1.5)
        {
            ratio /= 2;
        }

        while (ratio < 1 / 1.5)
        {
            ratio *= 2;
        }

        if (Math.Abs(ratio - 1) > MaximumStretch + 1e-9)
        {
            return null;
        }

        return ratio;
    }

    /// <summary>
    /// A candidate entry, put on the nearest phrase line when one is close and on the bar
    /// line otherwise. A record brought in three bars into a phrase is a record brought in
    /// at the wrong moment however exactly its beats land.
    /// </summary>
    private static double EntryPoint(double ms, BeatGrid grid, Arrangement? arrangement, double barMs) =>
        arrangement?.PhraseLineNear(ms, barMs)
        ?? grid.DownbeatAtOrBefore(ms)
        ?? ms;

    /// <summary>One entry point, scored and fitted.</summary>
    private static DJTransition? PlanEntry(
        double entry,
        double rate,
        MixRecord outgoing,
        MixRecord incoming,
        double outMixOut,
        double outBar,
        double outSegmentBpm,
        double notBeforeMs)
    {
        if (incoming.Grid.Segment(entry) is not { } inSegment)
        {
            return null;
        }

        var exitKind = outgoing.Arrangement?.SectionAt(outMixOut)?.Kind;
        var entryKind = incoming.Arrangement?.SectionAt(entry)?.Kind;

        // A drop onto a drop, or keys known to clash, does not get long together.
        var dropOnDrop = exitKind == SectionKind.Drop && entryKind == SectionKind.Drop;
        var keysClash = outgoing.Key is not null && incoming.Key is not null
                        && !CamelotKey.AreCompatible(outgoing.Key, incoming.Key);
        var capped = dropOnDrop || keysClash;

        foreach (var bars in Lengths)
        {
            var seconds = outBar * bars / 1000;

            // Back from the mix-out point a whole number of bars, then onto the phrase
            // line if one is close, else the bar line. The mix may end a little before the
            // mix-out point as a result — never seven bars before it, which would fade the
            // hook out under the new record.
            var counted = outMixOut - (outBar * bars);
            var startMs = outgoing.Arrangement?.PhraseLineAtOrBefore(counted, outBar)
                          ?? outgoing.Grid.DownbeatAtOrBefore(counted)
                          ?? counted;

            if (startMs < notBeforeMs)
            {
                continue;
            }

            if (!outgoing.Grid.CanLock(startMs, seconds))
            {
                continue;
            }

            if (!incoming.Grid.CanLock(entry, seconds))
            {
                continue;
            }

            // A bent record is eaten faster than the clock.
            if (entry + (seconds * 1000 * rate) > inSegment.EndMs)
            {
                continue;
            }

            // Singing, across exactly the stretch that would overlap. Unknown counts as
            // singing.
            bool? outgoingSings = outgoing.Arrangement?.MaySing(startMs, startMs + (seconds * 1000));
            bool? incomingSings = incoming.Arrangement?.MaySing(entry, entry + (seconds * 1000 * rate));
            if (outgoingSings == true && incomingSings == true)
            {
                continue;
            }

            var score = new MixScore(
                MixScore.TempoFactor(rate),
                MixScore.KeyFactor(outgoing.Key, incoming.Key),
                MixScore.VocalsFactor(outgoingSings, incomingSings),
                MixScore.EnergyFactor(
                    outgoing.Arrangement?.SectionAt(startMs)?.Energy,
                    incoming.Arrangement?.SectionAt(entry)?.Energy),
                MixScore.SectionsFactor(exitKind, entryKind));

            // A length that scores nothing refuses the entry outright rather than trying
            // the shorter ones — the clients do the same, and their table pins it.
            if (score.Bars is not { } earned)
            {
                return null;
            }

            var allowed = capped ? Math.Min(earned, MinimumBars) : earned;
            if (bars > allowed)
            {
                continue;
            }

            // The outgoing side snapped to the grid rather than trusted.
            var start = outgoing.Grid.DownbeatAtOrBefore(startMs) ?? startMs;

            var compatible = CamelotKey.AreCompatible(outgoing.Key, incoming.Key);
            var clashes = outgoing.Key is not null && incoming.Key is not null && !compatible;
            var anyoneSings = outgoingSings == true || incomingSings == true;
            var style = clashes || anyoneSings ? MixStyle.FilterFade : MixStyle.Blend;

            // A filter fade is capped too: the filter takes the melody out of the outgoing
            // record progressively, and thirty-two bars of a voice being slowly filtered
            // under another record is thirty-two bars of clash. Eight is long enough for
            // the sweep to read as a move.
            if (style != MixStyle.Blend && bars > MinimumBars)
            {
                continue;
            }

            return new DJTransition(
                start,
                entry,
                bars,
                seconds,
                outSegmentBpm,
                1,
                rate,
                RestoreSeconds,
                compatible,
                score,
                style);
        }

        return null;
    }
}
