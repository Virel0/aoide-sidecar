namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// A track's tempo, how much the estimate is worth believing, and whether it holds.
/// </summary>
/// <param name="Bpm">Beats per minute.</param>
/// <param name="Confidence">Zero to one. Below <see cref="TempoAnalyzer.MinimumConfidence"/> the estimate is not reported.</param>
/// <param name="Stability">
/// How much of the track keeps that tempo, zero to one, or null when the track is too
/// short to tell. One means every window agreed and a fixed grid would fit the whole
/// thing; a low value means the tempo moves, whatever the headline number says.
/// </param>
public sealed record Tempo(double Bpm, double Confidence, double? Stability = null);

/// <summary>
/// Estimates tempo from an onset-strength signal by autocorrelation.
/// </summary>
/// <remarks>
/// <para>
/// The spec suggested <c>aubio tempo</c> or Essentia. Neither is available: Jellyfin
/// ships ffmpeg and nothing else, the sidecar is a single managed DLL installed by
/// Jellyfin's own plugin installer, and requiring an admin to install a native library
/// before a plugin works is a support burden out of all proportion to two numbers used
/// for ordering. So this is the classic autocorrelation estimator, written out: the onset
/// strength has its local mean removed and is correlated against itself over the lags
/// that correspond to plausible tempi. <see cref="TempoScan"/> makes the onset strength.
/// </para>
/// <para>
/// Autocorrelation cannot tell a tempo from half or double it — the onsets really do
/// repeat at both — so candidate lags are weighted by a preference for tempi near
/// <see cref="CenterBpm"/> and the winner is walked down to the shortest period that
/// explains the signal as well. It is the weakest part of the estimate and the reason the
/// confidence exists.
/// </para>
/// <para>
/// Confidence is how far the winning lag stands above a typical lag: a track with a
/// steady beat produces one tall peak against a flat floor, while ambient, spoken word
/// and rubato classical produce no peak worth the name. Reporting nothing there is the
/// point — a made-up 128 BPM would be ordered against as if it were true. Stability is a
/// separate question, asked separately: a confident tempo measured over a whole track can
/// still be an average of a performance that sped up.
/// </para>
/// </remarks>
public static class TempoAnalyzer
{
    /// <summary>Slowest tempo considered.</summary>
    public const double MinBpm = 60;

    /// <summary>Fastest tempo considered.</summary>
    public const double MaxBpm = 200;

    /// <summary>The estimate is not reported below this confidence.</summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>Where the octave preference is centred.</summary>
    private const double CenterBpm = 120;

    /// <summary>Width of that preference, in octaves.</summary>
    private const double OctaveWidth = 0.9;

    /// <summary>A peak this far above a typical lag is full confidence.</summary>
    private const double FullConfidenceProminence = 0.30;

    /// <summary>
    /// How much of the winning lag's correlation the half-lag must reach before the
    /// half-lag is taken to be the real period.
    /// </summary>
    private const double HalfLagShare = 0.9;

    /// <summary>
    /// How strong the onsets must be, in log units per band, before anything is treated
    /// as an onset at all.
    /// </summary>
    private const double MinimumOnsetStrength = 0.02;

    /// <summary>Shorter than this and there is not enough signal to autocorrelate.</summary>
    private const double MinimumSeconds = 10;

    /// <summary>Window over which the onset signal's local mean is removed, in seconds.</summary>
    private const double LocalMeanSeconds = 1.5;

    /// <summary>
    /// Extra lags correlated on either side of the reported range. They are never chosen,
    /// but a peak at the very edge needs a neighbour on both sides to interpolate against,
    /// and the half-lag of a fast tempo can fall just outside.
    /// </summary>
    private const int LagPadding = 4;

    /// <summary>Length of each window the tempo is re-measured over for stability.</summary>
    private const double StabilityWindowSeconds = 20;

    /// <summary>
    /// How far a window's tempo may sit from the whole track's and still agree. Half a per
    /// cent, which is about what twenty seconds of audio can resolve — and still loose: a
    /// grid built on a tempo half a per cent out drifts most of a second across a track.
    /// </summary>
    private const double StabilityTolerance = 0.005;

    /// <summary>
    /// Estimates the tempo of an onset-strength signal.
    /// </summary>
    /// <param name="onsets">Onset strength, one value per hop.</param>
    /// <param name="rate">Onset values per second.</param>
    /// <returns>The estimate, or null when the signal carries no usable periodicity.</returns>
    public static Tempo? Estimate(IReadOnlyList<float> onsets, double rate)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rate, 0);

        var whole = Estimate(onsets, 0, onsets.Count, rate);
        return whole is null ? null : whole with { Stability = Stability(onsets, rate, whole.Bpm) };
    }

    /// <summary>
    /// Estimates the tempo of one stretch of the onset signal.
    /// </summary>
    private static Tempo? Estimate(IReadOnlyList<float> onsets, int from, int count, double rate)
    {
        if (count < MinimumSeconds * rate)
        {
            return null;
        }

        var centred = LocalMeanRemoved(onsets, from, count, (int)Math.Round(LocalMeanSeconds * rate));

        // Asked of the onsets as detected, not of the smoothed copy below: the question is
        // whether anything in the file actually starts, and smoothing spreads a spike out
        // and lowers it without making it any less of an onset.
        double raw = 0;
        foreach (var value in centred)
        {
            raw += value * value;
        }

        if (Math.Sqrt(raw / centred.Length) < MinimumOnsetStrength)
        {
            // Silence, or something sustained: nothing ever starts, so nothing has a tempo.
            return null;
        }

        var strength = Smoothed(centred);

        double variance = 0;
        foreach (var value in strength)
        {
            variance += value * value;
        }

        variance /= strength.Length;
        if (variance <= 1e-12)
        {
            return null;
        }

        var fastest = (int)Math.Floor(60 * rate / MaxBpm);
        var slowest = (int)Math.Ceiling(60 * rate / MinBpm);
        var first = Math.Max(1, fastest - LagPadding);
        var last = Math.Min((strength.Length / 2) - 1, slowest + LagPadding);
        if (first < 1 || last <= first || fastest >= last)
        {
            return null;
        }

        var correlation = new double[last - first + 1];
        var weighted = new double[correlation.Length];

        for (var lag = first; lag <= last; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < strength.Length; i++)
            {
                sum += strength[i] * strength[i + lag];
            }

            var r = sum / (strength.Length - lag) / variance;
            correlation[lag - first] = r;
            weighted[lag - first] = r * OctavePreference(60 * rate / lag);
        }

        // The padding is correlated but never chosen: a tempo outside the range is not
        // one this reports.
        var searchFrom = fastest - first;
        var searchTo = Math.Min(correlation.Length - 1, slowest - first);
        var best = searchFrom;
        for (var i = searchFrom + 1; i <= searchTo; i++)
        {
            if (weighted[i] > weighted[best])
            {
                best = i;
            }
        }

        // The prior's job was to pick which peak; where that peak actually sits is the
        // correlation's business alone.
        var lagAtPeak = Halve(correlation, first + best + Interpolate(correlation, best), first, searchFrom);

        var prominence = ValueAt(correlation, lagAtPeak - first) - Median(correlation, searchFrom, searchTo);
        var confidence = Math.Clamp(prominence / FullConfidenceProminence, 0, 1);

        return new Tempo(Math.Clamp(60 * rate / lagAtPeak, MinBpm, MaxBpm), confidence);
    }

    /// <summary>
    /// How much of the track holds the tempo the whole track averaged out to.
    /// </summary>
    /// <remarks>
    /// The question a beat grid actually turns on. A confident tempo over a whole track
    /// says the onsets are periodic on average; it does not say a fixed grid would fit,
    /// and for anything played by people it usually would not. Measuring the tempo again
    /// over overlapping windows and counting how many land on the same answer separates a
    /// sequenced track from a performance that drifted. Half and double count as agreeing:
    /// they are the same grid, read at a different resolution.
    /// </remarks>
    private static double? Stability(IReadOnlyList<float> onsets, double rate, double reference)
    {
        var window = (int)Math.Round(StabilityWindowSeconds * rate);
        var step = window / 2;
        if (step < 1 || onsets.Count < window * 2)
        {
            // Fewer than three windows is not evidence either way.
            return null;
        }

        var windows = 0;
        var agreed = 0;
        for (var from = 0; from + window <= onsets.Count; from += step)
        {
            windows++;
            var here = Estimate(onsets, from, window, rate);
            if (here is not null && here.Confidence >= MinimumConfidence && Agrees(here.Bpm, reference))
            {
                agreed++;
            }
        }

        return windows == 0 ? null : agreed / (double)windows;
    }

    /// <summary>
    /// Whether two tempi describe the same grid, allowing for one being read at half or
    /// double the other.
    /// </summary>
    private static bool Agrees(double bpm, double reference)
    {
        var ratio = bpm / reference;
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            return false;
        }

        while (ratio < Math.Sqrt(0.5))
        {
            ratio *= 2;
        }

        while (ratio >= Math.Sqrt(2))
        {
            ratio /= 2;
        }

        return Math.Abs(Math.Log2(ratio)) <= Math.Log2(1 + StabilityTolerance);
    }

    /// <summary>
    /// Walks a peak down to the shortest lag that explains the signal just as well.
    /// </summary>
    /// <remarks>
    /// Onsets that repeat every beat also repeat every two beats, and with equal strength,
    /// so the preference for tempi near <see cref="CenterBpm"/> can settle a tie either
    /// way — a steady 170 BPM and its 85 BPM half are almost exactly equally preferred.
    /// Where the half-lag correlates nearly as well as the winner, the half-lag is the
    /// period and the winner was its second harmonic. Where it does not, as in a track
    /// that genuinely has a beat only every 0.7 seconds, nothing moves.
    /// </remarks>
    private static double Halve(double[] correlation, double lag, int first, int floor)
    {
        while (true)
        {
            // Both lags are read off the curve at their true fractional position rather
            // than at the nearest whole hop. Without that the comparison is unfair and
            // 185 BPM reads as 92: its period of 32.4 hops is sampled almost half a hop
            // off its own peak, while the 64.9 of the half tempo lands nearly on one, so
            // the slower reading looks stronger than it is.
            // Never below the fastest tempo reported. The padded lags below it exist so
            // the parabola has a sample either side, not so a faster answer can be chosen.
            var half = lag / 2;
            if (half - first < floor)
            {
                return lag;
            }

            if (ValueAt(correlation, half - first) < HalfLagShare * ValueAt(correlation, lag - first))
            {
                return lag;
            }

            lag = half;
        }
    }

    /// <summary>
    /// The curve's value at a fractional index, by the parabola through its three nearest
    /// whole samples.
    /// </summary>
    private static double ValueAt(double[] values, double index)
    {
        var whole = (int)Math.Round(index);
        if (whole <= 0 || whole >= values.Length - 1)
        {
            return values[Math.Clamp(whole, 0, values.Length - 1)];
        }

        var offset = index - whole;
        var curvature = (values[whole - 1] - (2 * values[whole]) + values[whole + 1]) / 2;
        var slope = (values[whole + 1] - values[whole - 1]) / 2;
        return (curvature * offset * offset) + (slope * offset) + values[whole];
    }

    /// <summary>
    /// Centres a whole onset signal by taking out its local mean. The beat tracker wants
    /// the same treatment the autocorrelation gets: what stands out, not how loud the
    /// passage was.
    /// </summary>
    /// <param name="onsets">Onset strength, one value per hop.</param>
    /// <param name="rate">Onset values per second.</param>
    /// <returns>The centred signal.</returns>
    internal static double[] Centred(IReadOnlyList<float> onsets, double rate)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        return LocalMeanRemoved(onsets, 0, onsets.Count, (int)Math.Round(LocalMeanSeconds * rate));
    }

    /// <summary>
    /// Centres the onset signal by taking out its local mean, so the autocorrelation sees
    /// what stands out rather than how loud the passage was.
    /// </summary>
    private static double[] LocalMeanRemoved(IReadOnlyList<float> onsets, int from, int count, int window)
    {
        var strength = new double[count];
        if (window < 2)
        {
            for (var i = 0; i < count; i++)
            {
                strength[i] = onsets[from + i];
            }

            return strength;
        }

        // Local mean by prefix sums, so the cost does not depend on the window.
        var prefix = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            prefix[i + 1] = prefix[i] + onsets[from + i];
        }

        var half = window / 2;
        for (var i = 0; i < count; i++)
        {
            var low = Math.Max(0, i - half);
            var high = Math.Min(count, i + half + 1);
            strength[i] = onsets[from + i] - ((prefix[high] - prefix[low]) / (high - low));
        }

        return strength;
    }

    /// <summary>
    /// Spreads each onset over its neighbours with a narrow Gaussian.
    /// </summary>
    /// <remarks>
    /// An onset is about one hop wide, and a beat period is almost never a whole number of
    /// hops, so a correlation sampled at whole lags can miss a peak by half a hop and read
    /// it far too low. That is not a rounding error — it decides which tempo wins. At 185
    /// BPM the period is 32.4 hops and correlates at 0.74, while the half tempo's 64.9
    /// lands within a seventh of a hop and correlates at 0.96, so the wrong one looks
    /// better by a mile. Widening the onsets widens the correlation peaks with them, and
    /// half a hop of misalignment stops mattering.
    /// </remarks>
    private static double[] Smoothed(double[] strength)
    {
        double[] kernel = { 0.0545, 0.2442, 0.4026, 0.2442, 0.0545 };
        var radius = kernel.Length / 2;
        var smoothed = new double[strength.Length];

        for (var i = 0; i < strength.Length; i++)
        {
            double sum = 0;
            double weight = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var at = i + k;
                if (at < 0 || at >= strength.Length)
                {
                    continue;
                }

                sum += strength[at] * kernel[k + radius];
                weight += kernel[k + radius];
            }

            smoothed[i] = sum / weight;
        }

        return smoothed;
    }

    /// <summary>
    /// How much a tempo is preferred over the same tempo halved or doubled.
    /// </summary>
    private static double OctavePreference(double bpm)
    {
        var octaves = Math.Log2(bpm / CenterBpm) / OctaveWidth;
        return Math.Exp(-0.5 * octaves * octaves);
    }

    /// <summary>
    /// Sub-lag position of a peak, by fitting a parabola through it and its neighbours.
    /// </summary>
    private static double Interpolate(double[] values, int peak)
    {
        if (peak <= 0 || peak >= values.Length - 1)
        {
            return 0;
        }

        var denominator = values[peak - 1] - (2 * values[peak]) + values[peak + 1];
        if (Math.Abs(denominator) < 1e-12)
        {
            return 0;
        }

        return Math.Clamp(0.5 * (values[peak - 1] - values[peak + 1]) / denominator, -1, 1);
    }

    private static double Median(double[] values, int from, int to)
    {
        var sorted = values[from..(to + 1)];
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}

/// <summary>
/// Builds the onset-strength signal from decoded samples, so tempo comes out of the same
/// pass as everything else.
/// </summary>
/// <remarks>
/// <para>
/// Onset strength is spectral flux: the spectrum of each 23 ms window is folded into
/// log-spaced bands, each band's log energy is compared with the same band a hop earlier,
/// and the rises are added up. What that buys over watching the total energy is
/// everything that starts without making the track louder — a hi-hat over a sustained
/// pad, a snare under a bass note, a piano chord inside a held string line. Broadband
/// energy barely moves for any of those, and they are exactly the events that carry the
/// beat.
/// </para>
/// <para>
/// Only rises count. A note ending is not an onset, and counting it would put a second
/// peak between every pair of real ones.
/// </para>
/// <para>
/// The window is Hann-tapered, which is not cosmetic. A rectangular window over a
/// sustained note measures whatever fraction of a cycle falls inside it, so its energy
/// wobbles in step with the note's frequency against the hop rate — and that wobble is
/// perfectly periodic, which is exactly what the autocorrelation downstream is looking
/// for. A 220 Hz drone reported a confident tempo it did not have until the window was
/// tapered.
/// </para>
/// </remarks>
internal sealed class TempoScan : IPcmConsumer
{
    /// <summary>Onset values per second. One per 10 ms of audio.</summary>
    private const double TargetRate = 100;

    /// <summary>
    /// How hard quiet bands are pulled up before differencing. Log compression is what
    /// stops a loud chorus from drowning out the beat of a quiet verse.
    /// </summary>
    private const double CompressionGain = 1e6;

    /// <summary>Below this there is more rumble than rhythm.</summary>
    private const double LowestBandHz = 30;

    /// <summary>Above this there is nothing a beat is carried by.</summary>
    private const double HighestBandHz = 16000;

    /// <summary>Bands below this are where a downbeat is found.</summary>
    private const double LowBandTopHz = 250;

    /// <summary>How many bands to aim for, before bins force them apart.</summary>
    private const int TargetBands = 24;

    private readonly int _channels;
    private readonly int _hop;
    private readonly int _size;
    private readonly Fft _fft;
    private readonly double[] _taper;
    private readonly double _taperPower;
    private readonly float[] _ring;
    private readonly double[] _real;
    private readonly double[] _imaginary;
    private readonly int[] _edges;
    private readonly int _lowBands;
    private readonly double[] _previous;
    private readonly List<float> _onsets = new();
    private readonly List<float> _low = new();

    private bool _havePrevious;
    private int _writeAt;
    private int _sinceHop;
    private long _seen;
    private double _firstOnsetFrame = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="TempoScan"/> class.
    /// </summary>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    public TempoScan(int channels, int sampleRate)
    {
        _channels = channels;
        _hop = Math.Max(1, (int)Math.Round(sampleRate / TargetRate));
        Rate = sampleRate / (double)_hop;

        // At least twice the hop, so consecutive windows overlap rather than leaving gaps,
        // and a power of two for the transform. 1024 at any ordinary sample rate.
        _size = 1024;
        while (_size < _hop * 2)
        {
            _size <<= 1;
        }

        _fft = new Fft(_size);
        _ring = new float[_size];
        _real = new double[_size];
        _imaginary = new double[_size];
        _taper = new double[_size];
        for (var i = 0; i < _size; i++)
        {
            _taper[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (_size - 1)));
            _taperPower += _taper[i] * _taper[i];
        }

        _edges = Bands(sampleRate, _size);
        _previous = new double[_edges.Length - 1];

        // The bottom of the spectrum, kept separately: a downbeat is a kick, and a kick is
        // far more reliably the loudest thing under 250 Hz than the loudest thing overall.
        _lowBands = 0;
        while (_lowBands + 1 < _previous.Length && _edges[_lowBands + 1] * (double)sampleRate / _size <= LowBandTopHz)
        {
            _lowBands++;
        }

        _lowBands = Math.Max(1, _lowBands);
        SampleRate = sampleRate;
    }

    /// <summary>Gets the onset values per second actually used.</summary>
    public double Rate { get; }

    /// <summary>Gets the file's sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the onset strength, one value per hop.</summary>
    public IReadOnlyList<float> Onsets => _onsets;

    /// <summary>Gets the onset strength of the low bands alone, for finding downbeats.</summary>
    public IReadOnlyList<float> LowOnsets => _low;

    /// <summary>
    /// Gets where the first onset value sits in the track, in milliseconds.
    /// </summary>
    /// <remarks>
    /// An onset is attributed to the midpoint of the two windows whose difference produced
    /// it. Some systematic offset of a few milliseconds is unavoidable — the flux from a
    /// transient rises as it enters the window rather than as it passes the centre — but it
    /// is the same offset for every track, so two tracks lined up against each other are
    /// unaffected. It shifts an anchor, never a residual.
    /// </remarks>
    public double FirstOnsetMs => _firstOnsetFrame < 0 ? 0 : _firstOnsetFrame * 1000 / SampleRate;

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * _channels;
            double mono = 0;
            for (var channel = 0; channel < _channels; channel++)
            {
                mono += samples[at + channel];
            }

            _ring[_writeAt] = (float)(mono / _channels);
            _writeAt = _writeAt + 1 == _ring.Length ? 0 : _writeAt + 1;
            _seen++;

            if (++_sinceHop < _hop)
            {
                continue;
            }

            _sinceHop = 0;
            if (_seen >= _size)
            {
                Flux();
            }
        }
    }

    /// <summary>
    /// The estimate, once the whole file has been fed.
    /// </summary>
    /// <returns>The tempo, or null when nothing periodic was found.</returns>
    public Tempo? Result() => TempoAnalyzer.Estimate(_onsets, Rate);

    /// <summary>
    /// Log-spaced band edges as bin indices, forced at least one bin apart so no two bands
    /// are the same band. Low frequencies run out of bins first, so the count that comes
    /// back is usually a little under the target.
    /// </summary>
    private static int[] Bands(int sampleRate, int size)
    {
        var top = Math.Min(HighestBandHz, sampleRate * 0.45);
        var bins = size / 2;
        var edges = new List<int>();

        for (var i = 0; i <= TargetBands; i++)
        {
            var hz = LowestBandHz * Math.Pow(top / LowestBandHz, i / (double)TargetBands);
            var bin = Math.Clamp((int)Math.Round(hz * size / sampleRate), 1, bins);
            if (edges.Count == 0 || bin > edges[^1])
            {
                edges.Add(bin);
            }
        }

        // Two edges make one band; anything less and there is nothing to compare.
        return edges.Count >= 2 ? edges.ToArray() : new[] { 1, bins };
    }

    /// <summary>
    /// Adds up how much each band rose since the previous hop.
    /// </summary>
    private void Flux()
    {
        var read = _writeAt;
        for (var i = 0; i < _size; i++)
        {
            _real[i] = _ring[read] * _taper[i];
            _imaginary[i] = 0;
            read = read + 1 == _ring.Length ? 0 : read + 1;
        }

        _fft.Transform(_real, _imaginary);

        double risen = 0;
        double low = 0;
        for (var band = 0; band < _previous.Length; band++)
        {
            double power = 0;
            for (var bin = _edges[band]; bin < _edges[band + 1]; bin++)
            {
                power += (_real[bin] * _real[bin]) + (_imaginary[bin] * _imaginary[bin]);
            }

            // Mean spectral density in the band, so a wide band is not louder for being
            // wide, then compressed so a quiet band still gets a vote.
            var density = power / (_size * _taperPower * (_edges[band + 1] - _edges[band]));
            var energy = Math.Log(1 + (CompressionGain * density));

            var rise = Math.Max(0, energy - _previous[band]);
            risen += rise;
            if (band < _lowBands)
            {
                low += rise;
            }

            _previous[band] = energy;
        }

        if (_havePrevious)
        {
            if (_firstOnsetFrame < 0)
            {
                _firstOnsetFrame = _seen - (_size / 2.0) - (_hop / 2.0);
            }

            _onsets.Add((float)(risen / _previous.Length));
            _low.Add((float)(low / _lowBands));
        }

        _havePrevious = true;
    }
}
