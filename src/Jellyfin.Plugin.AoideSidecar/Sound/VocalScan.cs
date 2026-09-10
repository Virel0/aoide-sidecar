namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Guesses where someone is singing, from where the sound sits in the stereo image and
/// how much its pitch moves.
/// </summary>
/// <remarks>
/// <para>
/// The roughest measurement in the plugin, and the spec asked for it that way. Without
/// stems there is no way to isolate a voice, so this leans on two things that are true of
/// most produced records and not of all of them. A lead vocal is mixed to the centre, so
/// it survives in the mid channel and largely cancels in the side; and a voice does not
/// hold a pitch the way an instrument does — it slides into notes and it vibratos, so the
/// spectrum in the vocal band keeps moving when a pad's does not.
/// </para>
/// <para>
/// It is biased towards saying yes. A client uses this to refuse an overlap where both
/// records are singing, so a false positive costs a mix that would have been fine and a
/// false negative puts two lead vocals on top of each other — the mistake the measurement
/// exists to prevent. The errors belong on the cautious side.
/// </para>
/// <para>
/// A mono file has no stereo image to read, and a bass guitar and a snare are centred too.
/// Where the file carries no usable side channel at all the answer is that nothing is
/// known, which is not the same as nothing is sung.
/// </para>
/// </remarks>
internal sealed class VocalScan : IPcmConsumer
{
    private const int Size = 2048;

    /// <summary>The band a lead vocal lives in.</summary>
    private const double LowHz = 200;

    /// <summary>The band a lead vocal lives in.</summary>
    private const double HighHz = 4000;

    /// <summary>Below this share of the mid channel, the file has no stereo image to read.</summary>
    private const double MonoThreshold = 0.005;

    /// <summary>Over how long the frame-by-frame guess is averaged before it is believed.</summary>
    private const double SmoothingSeconds = 2;

    /// <summary>
    /// How far the sung passages must stand above the rest of the track before any claim is
    /// made at all.
    /// </summary>
    private const double MinimumContrast = 0.05;

    /// <summary>Nothing shorter than this is a vocal passage.</summary>
    private const double MinimumSpanMs = 2000;

    /// <summary>A break shorter than this is a breath, not the end of a verse.</summary>
    private const double BridgeMs = 1500;

    private readonly int _channels;
    private readonly int _hop;
    private readonly Fft _fft;
    private readonly double[] _taper;
    private readonly float[] _left;
    private readonly float[] _right;
    private readonly double[] _real;
    private readonly double[] _imaginary;
    private readonly int _from;
    private readonly int _to;
    private readonly List<float> _centred = new();
    private readonly List<float> _moving = new();
    private double[] _previous;

    private int _writeAt;
    private int _sinceHop;
    private long _seen;
    private double _midTotal;
    private double _sideTotal;

    /// <summary>
    /// Initializes a new instance of the <see cref="VocalScan"/> class.
    /// </summary>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    public VocalScan(int channels, int sampleRate)
    {
        _channels = channels;
        _hop = Size / 2;
        _fft = new Fft(Size);
        _left = new float[Size];
        _right = new float[Size];
        _real = new double[Size];
        _imaginary = new double[Size];
        _taper = new double[Size];
        for (var i = 0; i < Size; i++)
        {
            _taper[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (Size - 1)));
        }

        _from = Math.Max(1, (int)(LowHz * Size / sampleRate));
        _to = Math.Min((Size / 2) - 1, (int)(HighHz * Size / sampleRate));
        _previous = new double[Math.Max(1, _to - _from + 1)];
        Rate = sampleRate / (double)_hop;
    }

    /// <summary>Gets the frames per second of the two signals below.</summary>
    public double Rate { get; }

    /// <summary>
    /// Gets how much of the vocal band sits in the centre of the image, per frame.
    /// </summary>
    public IReadOnlyList<float> Centred => _centred;

    /// <summary>
    /// Gets how much the vocal band's spectrum moved since the previous frame, per frame.
    /// </summary>
    public IReadOnlyList<float> Moving => _moving;

    /// <summary>
    /// Gets a value indicating whether the file carries a usable stereo image.
    /// </summary>
    public bool Stereo => _channels > 1 && _midTotal > 0 && _sideTotal / _midTotal > MonoThreshold;

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * _channels;
            var left = samples[at];
            var right = _channels > 1 ? samples[at + 1] : left;

            _left[_writeAt] = left;
            _right[_writeAt] = right;
            _writeAt = _writeAt + 1 == Size ? 0 : _writeAt + 1;
            _seen++;

            var mid = (left + right) / 2;
            var side = (left - right) / 2;
            _midTotal += mid * mid;
            _sideTotal += side * side;

            if (++_sinceHop < _hop)
            {
                continue;
            }

            _sinceHop = 0;
            if (_seen >= Size)
            {
                Measure();
            }
        }
    }

    /// <summary>
    /// The stretches where somebody is probably singing.
    /// </summary>
    /// <returns>
    /// The spans; an empty list where none were found; <b>null where it could not be told</b>,
    /// which a caller must not read as an instrumental.
    /// </returns>
    public IReadOnlyList<VocalSpan>? Spans()
    {
        if (!Stereo || _centred.Count < 40)
        {
            // A mono file, or a stereo one mixed so narrowly it may as well be. Nothing can
            // be said either way, and saying "no vocals" would be a claim, not an absence
            // of one.
            return null;
        }

        var reference = Median(_moving);
        var likelihood = new double[_centred.Count];
        for (var i = 0; i < likelihood.Length; i++)
        {
            var restless = reference > 1e-9 ? Math.Clamp(_moving[i] / reference, 0, 2) / 2 : 0.5;
            likelihood[i] = Math.Max(0, _centred[i]) * (0.5 + (0.5 * restless));
        }

        var smoothed = Smooth(likelihood, (int)Math.Round(SmoothingSeconds * Rate));
        var sorted = (double[])smoothed.Clone();
        Array.Sort(sorted);
        var middle = sorted[sorted.Length / 2];
        var high = sorted[(int)(sorted.Length * 0.9)];

        if (high - middle < MinimumContrast)
        {
            // The whole track reads the same. Either it is sung throughout or not at all,
            // and this cannot tell which.
            return null;
        }

        var threshold = middle + ((high - middle) * 0.4);
        var spans = new List<VocalSpan>();
        int? start = null;

        for (var i = 0; i < smoothed.Length; i++)
        {
            if (smoothed[i] >= threshold)
            {
                start ??= i;
                continue;
            }

            if (start is { } at)
            {
                Close(spans, at, i);
                start = null;
            }
        }

        if (start is { } last)
        {
            Close(spans, last, smoothed.Length);
        }

        return spans;
    }

    /// <summary>
    /// Adds a span, joining it to the previous one across a gap too short to be a real
    /// break in the singing.
    /// </summary>
    private void Close(List<VocalSpan> spans, int from, int to)
    {
        var startMs = from * 1000 / Rate;
        var endMs = to * 1000 / Rate;
        if (endMs - startMs < MinimumSpanMs)
        {
            return;
        }

        if (spans.Count > 0 && startMs - spans[^1].EndMs <= BridgeMs)
        {
            spans[^1] = new VocalSpan(spans[^1].StartMs, Math.Round(endMs, 1));
            return;
        }

        spans.Add(new VocalSpan(Math.Round(startMs, 1), Math.Round(endMs, 1)));
    }

    private static double[] Smooth(double[] values, int window)
    {
        if (window < 2)
        {
            return values;
        }

        var prefix = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        var half = window / 2;
        var smoothed = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var low = Math.Max(0, i - half);
            var high = Math.Min(values.Length, i + half + 1);
            smoothed[i] = (prefix[high] - prefix[low]) / (high - low);
        }

        return smoothed;
    }

    private static double Median(List<float> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private void Measure()
    {
        var mid = Spectrum(centre: true);
        var side = Spectrum(centre: false);

        double midEnergy = 0;
        double sideEnergy = 0;
        double moved = 0;

        for (var i = 0; i < mid.Length; i++)
        {
            midEnergy += mid[i];
            sideEnergy += side[i];
            moved += Math.Abs(mid[i] - _previous[i]);
        }

        // How much of the band is centre rather than spread: one for a voice sitting in the
        // middle of the mix, towards zero for a wide pad or a doubled guitar.
        var total = midEnergy + sideEnergy;
        _centred.Add((float)(total <= 1e-12 ? 0 : (midEnergy - sideEnergy) / total));

        // And how restless it is, scaled by its own level so a loud passage is not
        // automatically a moving one.
        _moving.Add((float)(midEnergy <= 1e-12 ? 0 : moved / midEnergy));

        _previous = mid;
    }

    /// <summary>
    /// Magnitudes across the vocal band of the mid or the side channel.
    /// </summary>
    private double[] Spectrum(bool centre)
    {
        var read = _writeAt;
        for (var i = 0; i < Size; i++)
        {
            var left = _left[read];
            var right = _right[read];
            _real[i] = (centre ? (left + right) / 2 : (left - right) / 2) * _taper[i];
            _imaginary[i] = 0;
            read = read + 1 == Size ? 0 : read + 1;
        }

        _fft.Transform(_real, _imaginary);

        var band = new double[_to - _from + 1];
        for (var bin = _from; bin <= _to; bin++)
        {
            band[bin - _from] = Math.Sqrt((_real[bin] * _real[bin]) + (_imaginary[bin] * _imaginary[bin]));
        }

        return band;
    }
}
