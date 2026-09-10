namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Finds where the beats actually fall, rather than only how far apart they are.
/// </summary>
/// <remarks>
/// <para>
/// The tempo estimate says a beat every 468.75 ms; it does not say whether the first one
/// is at 0.2 s or at 0.6 s, and no amount of extra precision on the number will ever say.
/// Positions are a different measurement and this is it: the standard dynamic-programming
/// tracker, which picks the sequence of onsets maximising their combined strength less a
/// penalty for every interval that departs from the expected period.
/// </para>
/// <para>
/// The penalty is what makes it a beat tracker rather than an onset picker. Each candidate
/// interval costs <c>(ln(interval / period))²</c>, so a spacing at the expected period is
/// free, one at half or double it is expensive, and a syncopated onset only wins a place
/// if it is strong enough to pay. How hard that penalty bites is the one real tuning knob:
/// too tight and the tracker imposes a constant period on a performance that never had
/// one, hiding exactly the drift the grid is supposed to expose; too loose and it wanders
/// off the beat and onto whatever is loudest.
/// </para>
/// </remarks>
internal static class BeatTracker
{
    /// <summary>
    /// How hard a departure from the expected period is penalised.
    /// </summary>
    /// <remarks>
    /// Chosen so a track that really does change tempo is followed rather than flattened.
    /// The segment fit downstream reports what the tracker found; if this were rigid, every
    /// track would report a tidy grid and a small residual, including the ones nothing can
    /// be mixed with.
    /// </remarks>
    private const double Tightness = 90;

    /// <summary>
    /// Tracks the beats through an onset-strength signal.
    /// </summary>
    /// <param name="onsets">Onset strength, one value per hop, local mean already removed.</param>
    /// <param name="period">Expected beats' spacing, in hops.</param>
    /// <returns>Indices into <paramref name="onsets"/>, ascending. Empty when nothing could be tracked.</returns>
    public static IReadOnlyList<int> Track(IReadOnlyList<double> onsets, double period)
    {
        ArgumentNullException.ThrowIfNull(onsets);

        var count = onsets.Count;
        if (count < 4 || period < 2 || period * 4 > count)
        {
            return Array.Empty<int>();
        }

        var strength = Normalised(onsets);

        // The window of previous beats a beat may follow: from half the period back to
        // twice it. Anything outside that is a different tempo, not a wobble.
        var earliest = Math.Max(1, (int)Math.Floor(period / 2));
        var latest = Math.Max(earliest + 1, (int)Math.Ceiling(period * 2));

        var score = new double[count];
        var previous = new int[count];

        for (var t = 0; t < count; t++)
        {
            var best = double.NegativeInfinity;
            var from = -1;

            for (var back = earliest; back <= latest; back++)
            {
                var candidate = t - back;
                if (candidate < 0)
                {
                    break;
                }

                var departure = Math.Log(back / period);
                var here = score[candidate] - (Tightness * departure * departure);
                if (here > best)
                {
                    best = here;
                    from = candidate;
                }
            }

            if (from < 0)
            {
                // Too early in the track to have a predecessor: a possible first beat.
                score[t] = strength[t];
                previous[t] = -1;
            }
            else
            {
                score[t] = strength[t] + best;
                previous[t] = from;
            }
        }

        // The chain has to end somewhere near the end of the track — the score only ever
        // grows, so an early maximum would mean the tracker gave up.
        var last = -1;
        var highest = double.NegativeInfinity;
        for (var t = count - latest; t < count; t++)
        {
            if (t >= 0 && score[t] > highest)
            {
                highest = score[t];
                last = t;
            }
        }

        if (last < 0)
        {
            return Array.Empty<int>();
        }

        var beats = new List<int>();
        for (var t = last; t >= 0; t = previous[t])
        {
            beats.Add(t);
        }

        beats.Reverse();
        return beats;
    }

    /// <summary>
    /// Puts the onset strength on a scale the penalty can be balanced against.
    /// </summary>
    private static double[] Normalised(IReadOnlyList<double> onsets)
    {
        double mean = 0;
        foreach (var value in onsets)
        {
            mean += value;
        }

        mean /= onsets.Count;

        double variance = 0;
        foreach (var value in onsets)
        {
            var difference = value - mean;
            variance += difference * difference;
        }

        var deviation = Math.Sqrt(variance / onsets.Count);
        var scale = deviation > 1e-12 ? 1 / deviation : 0;

        var normalised = new double[onsets.Count];
        for (var i = 0; i < onsets.Count; i++)
        {
            normalised[i] = (onsets[i] - mean) * scale;
        }

        return normalised;
    }
}
