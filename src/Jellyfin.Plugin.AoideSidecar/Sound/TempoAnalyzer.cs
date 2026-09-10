namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// A track's tempo, and how much the estimate is worth believing.
/// </summary>
/// <param name="Bpm">Beats per minute.</param>
/// <param name="Confidence">Zero to one. Below <see cref="TempoAnalyzer.MinimumConfidence"/> the estimate is not reported.</param>
public sealed record Tempo(double Bpm, double Confidence);

/// <summary>
/// Estimates tempo from an onset-strength envelope by autocorrelation.
/// </summary>
/// <remarks>
/// <para>
/// The spec suggested <c>aubio tempo</c> or Essentia. Neither is available: Jellyfin
/// ships ffmpeg and nothing else, the sidecar is a single managed DLL installed by
/// Jellyfin's own plugin installer, and requiring an admin to install a native library
/// before a plugin works is a support burden out of all proportion to two numbers used
/// for ordering. So this is the classic envelope-autocorrelation estimator, written out:
/// energy per 10 ms hop, log-compressed, differenced and half-wave rectified into an
/// onset strength, its local mean removed, then autocorrelated over the lags that
/// correspond to plausible tempi.
/// </para>
/// <para>
/// Autocorrelation cannot tell a tempo from half or double it — the envelope really does
/// repeat at both — so candidate lags are weighted by a preference for tempi near
/// <see cref="CenterBpm"/>, which is what resolves the octave in practice. It is the
/// weakest part of the estimate and the reason the confidence exists.
/// </para>
/// <para>
/// Confidence is how far the winning lag stands above a typical lag: a track with a
/// steady beat produces one tall peak against a flat floor, while ambient, spoken word
/// and rubato classical produce no peak worth the name. Reporting nothing there is the
/// point — a made-up 128 BPM would be ordered against as if it were true.
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
    /// How much the energy must actually rise, in log units, before anything is treated
    /// as an onset at all.
    /// </summary>
    private const double MinimumOnsetStrength = 0.05;

    /// <summary>Shorter than this and there is not enough envelope to autocorrelate.</summary>
    private const double MinimumSeconds = 10;

    /// <summary>Window over which the envelope's local mean is removed, in seconds.</summary>
    private const double LocalMeanSeconds = 1.5;

    /// <summary>
    /// Estimates the tempo of an onset-strength envelope.
    /// </summary>
    /// <param name="envelope">Log-compressed energy, one value per hop.</param>
    /// <param name="envelopeRate">Envelope values per second.</param>
    /// <returns>The estimate, or null when the envelope carries no usable periodicity.</returns>
    public static Tempo? Estimate(IReadOnlyList<float> envelope, double envelopeRate)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(envelopeRate, 0);

        var n = envelope.Count;
        if (n < MinimumSeconds * envelopeRate)
        {
            return null;
        }

        var onsets = OnsetStrength(envelope, (int)Math.Round(LocalMeanSeconds * envelopeRate));

        double variance = 0;
        foreach (var value in onsets)
        {
            variance += value * value;
        }

        variance /= onsets.Length;
        if (Math.Sqrt(variance) < MinimumOnsetStrength)
        {
            // Silence, or a held tone: nothing ever starts, so nothing has a tempo. The
            // floor is absolute rather than relative because the envelope is already log
            // compressed — a rise this small is a rise of a few per cent, whatever the
            // track's level, and a sustained sound will still produce one from the way
            // its cycles fall across the analysis hops.
            return null;
        }

        var minLag = (int)Math.Floor(60 * envelopeRate / MaxBpm);
        var maxLag = (int)Math.Ceiling(60 * envelopeRate / MinBpm);
        if (maxLag >= onsets.Length / 2)
        {
            maxLag = (onsets.Length / 2) - 1;
        }

        if (minLag < 1 || maxLag <= minLag)
        {
            return null;
        }

        var span = maxLag - minLag + 1;
        var correlation = new double[span];
        var weighted = new double[span];

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < onsets.Length; i++)
            {
                sum += onsets[i] * onsets[i + lag];
            }

            var r = sum / (onsets.Length - lag) / variance;
            correlation[lag - minLag] = r;
            weighted[lag - minLag] = r * OctavePreference(60 * envelopeRate / lag);
        }

        var best = 0;
        for (var i = 1; i < span; i++)
        {
            if (weighted[i] > weighted[best])
            {
                best = i;
            }
        }

        best = Halve(correlation, best, minLag);

        var prominence = correlation[best] - Median(correlation);
        var confidence = Math.Clamp(prominence / FullConfidenceProminence, 0, 1);
        var lagAtPeak = minLag + best + Interpolate(weighted, best);

        return new Tempo(60 * envelopeRate / lagAtPeak, confidence);
    }

    /// <summary>
    /// Walks a peak down to the shortest lag that explains the envelope just as well.
    /// </summary>
    /// <remarks>
    /// An envelope that repeats every beat also repeats every two beats, and with equal
    /// strength, so the preference for tempi near <see cref="CenterBpm"/> can settle a
    /// tie either way — a steady 170 BPM and its 85 BPM half are almost exactly equally
    /// preferred. Where the half-lag correlates nearly as well as the winner, the
    /// half-lag is the period and the winner was its second harmonic. Where it does not,
    /// as in a track that genuinely has a beat only every 0.7 seconds, nothing moves.
    /// </remarks>
    private static int Halve(double[] correlation, int best, int minLag)
    {
        while (true)
        {
            var half = ((minLag + best) / 2) - minLag;
            if (half < 0 || correlation[half] < HalfLagShare * correlation[best])
            {
                return best;
            }

            best = half;
        }
    }

    /// <summary>
    /// Turns an energy envelope into a zero-mean onset strength: what got louder, with
    /// the slow drift of the track's own dynamics taken back out.
    /// </summary>
    private static double[] OnsetStrength(IReadOnlyList<float> envelope, int window)
    {
        var rise = new double[envelope.Count - 1];
        for (var i = 1; i < envelope.Count; i++)
        {
            rise[i - 1] = Math.Max(0, envelope[i] - envelope[i - 1]);
        }

        if (window < 2)
        {
            return rise;
        }

        // Local mean by prefix sums, so the cost does not depend on the window.
        var prefix = new double[rise.Length + 1];
        for (var i = 0; i < rise.Length; i++)
        {
            prefix[i + 1] = prefix[i] + rise[i];
        }

        var half = window / 2;
        var onsets = new double[rise.Length];
        for (var i = 0; i < rise.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(rise.Length, i + half + 1);
            onsets[i] = rise[i] - ((prefix[to] - prefix[from]) / (to - from));
        }

        return onsets;
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

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}

/// <summary>
/// Builds the onset envelope from decoded samples, so tempo comes out of the same pass
/// as everything else.
/// </summary>
/// <remarks>
/// Energy is measured over a Hann-windowed span of twice the hop, advancing one hop at a
/// time. The window is not cosmetic. A rectangular window over a sustained note measures
/// whatever fraction of a cycle happens to fall inside it, so its energy wobbles in step
/// with the note's frequency against the hop rate — and that wobble is perfectly
/// periodic, which is exactly what the autocorrelation downstream is looking for. A
/// 220 Hz drone read that way reports a confident tempo it does not have. Tapering the
/// window makes the energy estimate all but independent of where the cycles land, and the
/// artefact disappears into the noise.
/// </remarks>
internal sealed class TempoScan : IPcmConsumer
{
    /// <summary>Envelope values per second. One per 10 ms of audio.</summary>
    private const double TargetEnvelopeRate = 100;

    /// <summary>
    /// How hard quiet passages are pulled up before differencing. Log compression is what
    /// stops a loud chorus from drowning out the beat of a quiet verse.
    /// </summary>
    private const double CompressionGain = 1000;

    private readonly int _channels;
    private readonly int _hop;
    private readonly float[] _window;
    private readonly float[] _ring;
    private readonly double _windowPower;
    private readonly List<float> _envelope = new();

    private int _writeAt;
    private int _sinceHop;
    private long _seen;

    /// <summary>
    /// Initializes a new instance of the <see cref="TempoScan"/> class.
    /// </summary>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    public TempoScan(int channels, int sampleRate)
    {
        _channels = channels;
        _hop = Math.Max(1, (int)Math.Round(sampleRate / TargetEnvelopeRate));
        EnvelopeRate = sampleRate / (double)_hop;

        var length = _hop * 2;
        _ring = new float[length];
        _window = new float[length];
        for (var i = 0; i < length; i++)
        {
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (length - 1))));
            _windowPower += _window[i] * _window[i];
        }
    }

    /// <summary>Gets the envelope values per second actually used.</summary>
    public double EnvelopeRate { get; }

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
            _writeAt = (_writeAt + 1) % _ring.Length;
            _seen++;

            if (++_sinceHop < _hop)
            {
                continue;
            }

            _sinceHop = 0;
            if (_seen >= _ring.Length)
            {
                _envelope.Add((float)Math.Log(1 + (CompressionGain * WindowedPower())));
            }
        }
    }

    /// <summary>
    /// The estimate, once the whole file has been fed.
    /// </summary>
    /// <returns>The tempo, or null when nothing periodic was found.</returns>
    public Tempo? Result() => TempoAnalyzer.Estimate(_envelope, EnvelopeRate);

    /// <summary>
    /// Mean square of the last window's worth of samples, tapered by the window.
    /// </summary>
    private double WindowedPower()
    {
        double sum = 0;
        var read = _writeAt;
        for (var i = 0; i < _ring.Length; i++)
        {
            var value = _ring[read] * _window[i];
            sum += value * value;
            read = read + 1 == _ring.Length ? 0 : read + 1;
        }

        return sum / _windowPower;
    }
}
