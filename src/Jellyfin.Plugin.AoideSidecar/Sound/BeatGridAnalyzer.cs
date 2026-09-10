namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Turns tracked beats into the thing a mixed transition can actually use: a fitted grid,
/// a meter, and the two points between which a blend may run.
/// </summary>
/// <remarks>
/// A fit rather than a list of beat times. The residual is the reason — it is the only
/// number that says whether a track can be locked to at all, and a list of timestamps does
/// not carry it. Sixteen bars at 128 BPM is thirty seconds; holding a twentieth of a beat
/// across that needs a grid good to about 20 ms end to end, and a track whose own onsets
/// sit 60 ms off its own fitted grid is not one anything should try to mix.
/// </remarks>
internal static class BeatGridAnalyzer
{
    /// <summary>A fit better than this is not worth splitting further.</summary>
    private const double GoodResidualMs = 12;

    /// <summary>A split must improve the residual by this much to be worth making.</summary>
    private const double WorthSplitting = 0.75;

    /// <summary>How many times a segment may be split in two.</summary>
    private const int MaxSplitDepth = 3;

    /// <summary>No segment shorter than this, in beats — a fit needs something to fit to.</summary>
    private const int MinimumSegmentBeats = 32;

    /// <summary>Beyond this the fit is describing something that is not a steady beat.</summary>
    private const double UselessResidualMs = 120;

    /// <summary>Meters tried, in the order they are preferred.</summary>
    private static readonly int[] Meters = { 4, 3 };

    /// <summary>How much louder a downbeat must be than an average beat to count as found.</summary>
    private const double MinimumDownbeatContrast = 0.12;

    /// <summary>How much better three must look than four before a waltz is believed.</summary>
    private const double WaltzMargin = 1.3;

    /// <summary>A beat carrying less than this share of the median is not part of the groove.</summary>
    private const double GrooveShare = 0.5;

    /// <summary>
    /// Below this share of the median, a beat at either end of the track is not a beat at
    /// all — the tracker was chaining through something with nothing in it.
    /// </summary>
    private const double SilentShare = 0.25;

    /// <summary>
    /// Builds the grid.
    /// </summary>
    /// <param name="onsets">Onset strength per hop, as the tempo estimate sees it.</param>
    /// <param name="lowOnsets">Onset strength from the low bands only, where a downbeat lives.</param>
    /// <param name="rate">Onset values per second.</param>
    /// <param name="firstOnsetMs">Position of the first onset value in the track.</param>
    /// <param name="durationMs">The track's length.</param>
    /// <param name="tempo">The tempo estimate, for the period the tracker expects.</param>
    /// <returns>The grid, or null when there is no grid worth having.</returns>
    public static BeatGrid? Analyze(
        IReadOnlyList<float> onsets,
        IReadOnlyList<float> lowOnsets,
        double rate,
        double firstOnsetMs,
        double durationMs,
        Tempo? tempo) => Analyze(onsets, lowOnsets, rate, firstOnsetMs, durationMs, tempo, out _);

    /// <summary>
    /// Builds the grid, and hands back the beats it was built from.
    /// </summary>
    /// <param name="onsets">Onset strength per hop, as the tempo estimate sees it.</param>
    /// <param name="lowOnsets">Onset strength from the low bands only, where a downbeat lives.</param>
    /// <param name="rate">Onset values per second.</param>
    /// <param name="firstOnsetMs">Position of the first onset value in the track.</param>
    /// <param name="durationMs">The track's length.</param>
    /// <param name="tempo">The tempo estimate, for the period the tracker expects.</param>
    /// <param name="beats">The tracked beats, for anything that wants to measure per beat.</param>
    /// <returns>The grid, or null when there is no grid worth having.</returns>
    public static BeatGrid? Analyze(
        IReadOnlyList<float> onsets,
        IReadOnlyList<float> lowOnsets,
        double rate,
        double firstOnsetMs,
        double durationMs,
        Tempo? tempo,
        out TrackedBeats? beats)
    {
        beats = null;
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(lowOnsets);

        if (tempo is null || tempo.Confidence < TempoAnalyzer.MinimumConfidence)
        {
            // No usable period to track against, and a tracker given a wrong period finds
            // a confident grid for the wrong thing.
            return null;
        }

        var period = 60 * rate / tempo.Bpm;
        var tracked = BeatTracker.Track(TempoAnalyzer.Centred(onsets, rate), period);
        if (tracked.Count < MinimumSegmentBeats)
        {
            return null;
        }

        var msPerHop = 1000 / rate;
        var times = new double[tracked.Count];
        for (var i = 0; i < tracked.Count; i++)
        {
            times[i] = firstOnsetMs + (tracked[i] * msPerHop);
        }

        // Beat numbers, not positions in the list: a beat the tracker could not find leaves
        // a hole, and a fit that ignored it would report a tempo slower than the track's.
        var expected = 60000 / tempo.Bpm;
        var numbers = new int[times.Length];
        for (var i = 1; i < times.Length; i++)
        {
            numbers[i] = numbers[i - 1] + Math.Max(1, (int)Math.Round((times[i] - times[i - 1]) / expected));
        }

        // The tracker has to produce an unbroken chain from the first hop to the last, so
        // a track that opens on twenty seconds of pad gets twenty seconds of beats that are
        // not there. Fitting those produces a whole extra segment describing nothing.
        var carries = Sounding(tracked, onsets, out var first, out var last);
        if (last - first + 1 < MinimumSegmentBeats)
        {
            return null;
        }

        var fits = new List<Fit>();
        Split(times, numbers, carries, first, last, 0, fits);
        if (fits.Count == 0)
        {
            return null;
        }

        var meter = Meter(tracked, numbers, lowOnsets);
        beats = new TrackedBeats(times, numbers, first, last, meter?.BeatsPerBar, meter?.Phase ?? 0);
        var segments = Describe(fits, times, numbers, durationMs, meter);
        if (segments.Count == 0)
        {
            return null;
        }

        // A grid nothing could be locked to is still worth returning: the client needs to
        // see the residual to decide, and "no grid at all" would be a different claim.
        if (segments.All(s => s.ResidualMs > UselessResidualMs))
        {
            return null;
        }

        var (mixIn, mixOut) = MixPoints(tracked, times, numbers, onsets, segments, meter);

        return new BeatGrid(
            segments,
            meter?.BeatsPerBar,
            meter is null ? null : 0,
            mixIn,
            mixOut);
    }

    /// <summary>
    /// Marks which beats carry anything, and where the sounding part of the track begins
    /// and ends.
    /// </summary>
    /// <remarks>
    /// The tracker must return an unbroken chain from the first hop to the last, so a
    /// stretch with no percussion in it — a breakdown, an intro of pure pad — still gets
    /// beats, and they are wherever the chain drifted to. Fitting those alongside real ones
    /// invents a tempo change: on a track that is 128 BPM throughout, a beat-free breakdown
    /// produced two extra segments at 130 and 133 BPM with residuals near 60 ms, and every
    /// section boundary snapped to those bar lines instead of the real ones. Beats that
    /// carry nothing are excluded from the arithmetic; the fit spans the gap on the beats
    /// either side, which is exactly right when the tempo did not change.
    /// </remarks>
    private static bool[] Sounding(IReadOnlyList<int> tracked, IReadOnlyList<float> onsets, out int first, out int last)
    {
        var strength = Strength(tracked, onsets);
        var sorted = (double[])strength.Clone();
        Array.Sort(sorted);
        var floor = sorted[sorted.Length / 2] * SilentShare;

        var carries = new bool[strength.Length];
        for (var i = 0; i < strength.Length; i++)
        {
            // Judged over a bar rather than a beat: a real beat can land between two hits.
            carries[i] = Mean(strength, Math.Max(0, i - 2), Math.Min(4, strength.Length - Math.Max(0, i - 2))) >= floor;
        }

        first = 0;
        while (first < strength.Length - 1 && !carries[first])
        {
            first++;
        }

        last = strength.Length - 1;
        while (last > first && !carries[last])
        {
            last--;
        }

        return carries;
    }

    private static double[] Strength(IReadOnlyList<int> tracked, IReadOnlyList<float> onsets)
    {
        var strength = new double[tracked.Count];
        for (var i = 0; i < tracked.Count; i++)
        {
            strength[i] = tracked[i] < onsets.Count ? Math.Max(0, onsets[tracked[i]]) : 0;
        }

        return strength;
    }

    /// <summary>
    /// Splits a run of beats until each part is fitted well, or until splitting stops
    /// helping.
    /// </summary>
    /// <remarks>
    /// A track that changes tempo fitted as one grid gives a residual that describes
    /// neither half. Splitting where it helps most, and only where it helps enough, gives
    /// each part an honest one — and the client is told to mix inside a segment, which is
    /// the truthful way to handle a track that speeds up.
    /// </remarks>
    private static void Split(double[] times, int[] numbers, bool[] carries, int from, int to, int depth, List<Fit> fits)
    {
        var whole = LeastSquares(times, numbers, carries, from, to);

        if (depth >= MaxSplitDepth
            || whole.ResidualMs <= GoodResidualMs
            || whole.Beats < MinimumSegmentBeats * 2)
        {
            fits.Add(whole);
            return;
        }

        var bestAt = -1;
        var bestResidual = double.MaxValue;
        for (var at = from + MinimumSegmentBeats; at <= to - MinimumSegmentBeats; at++)
        {
            var left = LeastSquares(times, numbers, carries, from, at);
            var right = LeastSquares(times, numbers, carries, at + 1, to);
            if (left.Beats < MinimumSegmentBeats || right.Beats < MinimumSegmentBeats)
            {
                continue;
            }

            // Weighted by length, so a split is judged on the whole run and not on
            // whichever side happens to be tidier.
            var combined = Math.Sqrt(
                (((left.ResidualMs * left.ResidualMs) * left.Beats) + ((right.ResidualMs * right.ResidualMs) * right.Beats))
                / (left.Beats + right.Beats));

            if (combined < bestResidual)
            {
                bestResidual = combined;
                bestAt = at;
            }
        }

        if (bestAt < 0 || bestResidual > whole.ResidualMs * WorthSplitting)
        {
            fits.Add(whole);
            return;
        }

        Split(times, numbers, carries, from, bestAt, depth + 1, fits);
        Split(times, numbers, carries, bestAt + 1, to, depth + 1, fits);
    }

    /// <summary>
    /// Fits one straight line through beat number against beat time.
    /// </summary>
    /// <remarks>
    /// Sums are taken about the means rather than raw. Beat times run to hundreds of
    /// thousands of milliseconds and the residual being measured is around ten, so the
    /// textbook sum-of-squares form would compute it as the difference of two very large
    /// and nearly equal numbers, and return noise.
    /// </remarks>
    private static Fit LeastSquares(double[] times, int[] numbers, bool[] carries, int from, int to)
    {
        var count = 0;
        double meanNumber = 0;
        double meanTime = 0;
        for (var i = from; i <= to; i++)
        {
            if (!carries[i])
            {
                continue;
            }

            meanNumber += numbers[i];
            meanTime += times[i];
            count++;
        }

        if (count < 2)
        {
            return new Fit(from, to, times[from], 60000 / 120.0, double.MaxValue, 0);
        }

        meanNumber /= count;
        meanTime /= count;

        double covariance = 0;
        double spread = 0;
        for (var i = from; i <= to; i++)
        {
            if (!carries[i])
            {
                continue;
            }

            var dn = numbers[i] - meanNumber;
            covariance += dn * (times[i] - meanTime);
            spread += dn * dn;
        }

        var msPerBeat = spread > 1e-9 ? covariance / spread : 0;
        if (msPerBeat <= 0)
        {
            msPerBeat = 60000 / Math.Max(1, meanNumber);
        }

        var intercept = meanTime - (msPerBeat * meanNumber);

        double squared = 0;
        for (var i = from; i <= to; i++)
        {
            if (!carries[i])
            {
                continue;
            }

            var error = times[i] - intercept - (msPerBeat * numbers[i]);
            squared += error * error;
        }

        return new Fit(from, to, intercept, msPerBeat, Math.Sqrt(squared / count), count);
    }

    /// <summary>
    /// Turns fits into the segments the client sees: contiguous, anchored on a downbeat
    /// where the meter is known, and covering the whole track.
    /// </summary>
    private static List<BeatSegment> Describe(
        List<Fit> fits,
        double[] times,
        int[] numbers,
        double durationMs,
        MeterResult? meter)
    {
        var segments = new List<BeatSegment>(fits.Count);

        for (var i = 0; i < fits.Count; i++)
        {
            var fit = fits[i];

            // Boundaries fall between one fit's last beat and the next fit's first, and the
            // outer edges run to the ends of the track: a client is told to mix inside a
            // segment, so the segments have to account for all of it.
            // The first segment runs from the start of the track and the last to its end,
            // even where the fit was made over less: the grid extrapolates perfectly well
            // across an intro, and a client asking which segment holds a moment needs an
            // answer for every moment.
            var start = i == 0 ? 0 : (times[fit.From] + times[fits[i - 1].To]) / 2;
            var end = i == fits.Count - 1 ? Math.Max(durationMs, times[fit.To]) : (times[fit.To] + times[fits[i + 1].From]) / 2;

            // Beat zero of the fit is put on a downbeat, so bar n of any segment is
            // anchorMs + n * beatsPerBar * 60000 / bpm without the client tracking phase
            // across a tempo change.
            var origin = numbers[fit.From];
            if (meter is not null)
            {
                var phase = ((origin - meter.Phase) % meter.BeatsPerBar + meter.BeatsPerBar) % meter.BeatsPerBar;
                if (phase != 0)
                {
                    origin += meter.BeatsPerBar - phase;
                }
            }

            segments.Add(new BeatSegment(
                Math.Round(start, 1),
                Math.Round(end, 1),
                Math.Round(fit.Intercept + (fit.MsPerBeat * origin), 1),
                Math.Round(60000 / fit.MsPerBeat, 2),
                Math.Round(fit.ResidualMs, 1),
                fit.Beats));
        }

        return segments;
    }

    /// <summary>
    /// Works out how many beats to a bar, and which of them is the first.
    /// </summary>
    /// <remarks>
    /// Beats alone are not enough: landing bar three of one track on bar two of the other
    /// is in time and audibly wrong. Downbeats are found where they live, in the bottom
    /// couple of hundred hertz — a kick lands on one far more reliably than anything is
    /// louder overall. Four is preferred because nearly everything is four; three has to
    /// beat it by a clear margin before a waltz is believed, and when neither pattern
    /// stands out the answer is that the meter is not known.
    /// </remarks>
    private static MeterResult? Meter(IReadOnlyList<int> tracked, int[] numbers, IReadOnlyList<float> lowOnsets)
    {
        if (lowOnsets.Count == 0)
        {
            return null;
        }

        var strength = new double[tracked.Count];
        double total = 0;
        for (var i = 0; i < tracked.Count; i++)
        {
            strength[i] = tracked[i] < lowOnsets.Count ? lowOnsets[tracked[i]] : 0;
            total += strength[i];
        }

        var mean = total / tracked.Count;
        if (mean <= 1e-9)
        {
            return null;
        }

        MeterResult? best = null;
        double bestContrast = 0;

        foreach (var beatsPerBar in Meters)
        {
            var sums = new double[beatsPerBar];
            var counts = new int[beatsPerBar];
            for (var i = 0; i < tracked.Count; i++)
            {
                var phase = ((numbers[i] % beatsPerBar) + beatsPerBar) % beatsPerBar;
                sums[phase] += strength[i];
                counts[phase]++;
            }

            var strongest = 0;
            for (var phase = 1; phase < beatsPerBar; phase++)
            {
                if (counts[phase] > 0 && sums[phase] / counts[phase] > sums[strongest] / Math.Max(1, counts[strongest]))
                {
                    strongest = phase;
                }
            }

            if (counts[strongest] == 0)
            {
                continue;
            }

            var contrast = ((sums[strongest] / counts[strongest]) - mean) / mean;
            var required = best is null ? MinimumDownbeatContrast : bestContrast * WaltzMargin;
            if (contrast >= MinimumDownbeatContrast && contrast >= required)
            {
                best = new MeterResult(beatsPerBar, strongest);
                bestContrast = contrast;
            }
        }

        return best;
    }

    /// <summary>
    /// Finds where a blend may bring the track in and take it out.
    /// </summary>
    /// <remarks>
    /// Measured on the onset strength rather than on loudness, which is what makes an intro
    /// of pure atmosphere come out as not yet playing: a sustained pad can be as loud as
    /// the chorus and still have nothing starting in it. Both points land on downbeats of
    /// the fit, because a transition that begins mid-bar is the problem the meter was
    /// worked out to avoid.
    /// </remarks>
    private static (double? In, double? Out) MixPoints(
        IReadOnlyList<int> tracked,
        double[] times,
        int[] numbers,
        IReadOnlyList<float> onsets,
        List<BeatSegment> segments,
        MeterResult? meter)
    {
        if (meter is null || tracked.Count < MinimumSegmentBeats)
        {
            return (null, null);
        }

        var strength = Strength(tracked, onsets);
        var sorted = (double[])strength.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        if (median <= 1e-9)
        {
            return (null, null);
        }

        var phrase = meter.BeatsPerBar * 4;
        var floor = median * GrooveShare;

        // Every bar of the phrase has to carry its weight, not the phrase on average. A
        // mean lets a strong second half drag a silent first half over the line, which put
        // the start of a blend four seconds inside a twenty-second pad.
        bool Grooving(double[] values, int at, int beatsPerBar)
        {
            for (var bar = 0; bar < 4; bar++)
            {
                if (Mean(values, at + (bar * beatsPerBar), beatsPerBar) < floor)
                {
                    return false;
                }
            }

            return true;
        }

        int? first = null;
        for (var i = 0; i + phrase <= strength.Length; i++)
        {
            if (!IsDownbeat(numbers[i], meter))
            {
                continue;
            }

            if (Grooving(strength, i, meter.BeatsPerBar))
            {
                first = i;
                break;
            }
        }

        int? last = null;
        for (var i = strength.Length - 1; i - phrase >= 0; i--)
        {
            if (!IsDownbeat(numbers[i], meter))
            {
                continue;
            }

            if (Grooving(strength, i - phrase, meter.BeatsPerBar))
            {
                last = i;
                break;
            }
        }

        if (first is null || last is null || last <= first)
        {
            return (null, null);
        }

        return (Snap(times[first.Value], segments), Snap(times[last.Value], segments));
    }

    private static bool IsDownbeat(int number, MeterResult meter) =>
        (((number - meter.Phase) % meter.BeatsPerBar) + meter.BeatsPerBar) % meter.BeatsPerBar == 0;

    private static double Mean(double[] values, int from, int count)
    {
        double sum = 0;
        for (var i = from; i < from + count; i++)
        {
            sum += values[i];
        }

        return sum / count;
    }

    /// <summary>
    /// Moves a time onto the nearest bar line of whichever segment holds it, so a mix point
    /// is always somewhere the published grid actually says a bar begins.
    /// </summary>
    private static double Snap(double ms, List<BeatSegment> segments)
    {
        var segment = segments.FirstOrDefault(s => ms >= s.StartMs && ms <= s.EndMs) ?? segments[0];
        var msPerBeat = 60000 / segment.Bpm;
        var bars = Math.Round((ms - segment.AnchorMs) / msPerBeat);
        return Math.Round(segment.AnchorMs + (bars * msPerBeat), 1);
    }

    private sealed record Fit(int From, int To, double Intercept, double MsPerBeat, double ResidualMs, int Beats);

    private sealed record MeterResult(int BeatsPerBar, int Phase);
}

/// <summary>
/// The beats a grid was fitted to, for anything else that wants to measure per beat rather
/// than per hop.
/// </summary>
/// <param name="TimesMs">Every tracked beat's position.</param>
/// <param name="Numbers">Each beat's number, which skips where the tracker missed one.</param>
/// <param name="First">Index of the first beat that carried anything.</param>
/// <param name="Last">Index of the last.</param>
/// <param name="BeatsPerBar">The meter, or null when it could not be established.</param>
/// <param name="Phase">Which beat number begins a bar.</param>
public sealed record TrackedBeats(
    double[] TimesMs,
    int[] Numbers,
    int First,
    int Last,
    int? BeatsPerBar,
    int Phase);
