using System.Globalization;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// A track's key, on the Camelot wheel that harmonic mixing is done by.
/// </summary>
/// <param name="Camelot">Such as <c>8A</c> for A minor or <c>8B</c> for C major.</param>
/// <param name="Confidence">Zero to one: how far ahead of the runner-up the answer finished.</param>
public sealed record MusicalKey(string Camelot, double Confidence);

/// <summary>
/// Estimates the key by matching what pitches the track dwells on against the profiles of
/// the twenty-four keys.
/// </summary>
/// <remarks>
/// <para>
/// A second transform rather than the one the onsets use, because the two want opposite
/// things. Onsets want a short window, to say exactly when something happened; pitch wants
/// a long one, to say exactly what it was. At 23 ms a bin is 43 Hz wide, which cannot tell
/// two adjacent semitones apart anywhere below the top octaves. At 186 ms a bin is 5 Hz
/// and it can. Both read the same decoded samples, so it is still the one pass.
/// </para>
/// <para>
/// The profiles are Krumhansl and Kessler's, and the estimate is what it is: it hears
/// which notes a track leans on and nothing else, so it has no idea about modulation and
/// is at its weakest telling a key from its relative major or minor, which use the same
/// notes. The confidence is the margin over the runner-up, which is exactly where that
/// weakness shows. Nothing refuses a mix over a key.
/// </para>
/// </remarks>
internal sealed class ChromaScan : IPcmConsumer
{
    private const int Size = 8192;

    /// <summary>Below this the bins are too close together to be sure which note is which.</summary>
    private const double LowestHz = 55;

    /// <summary>Above this there is more harmonic than fundamental.</summary>
    private const double HighestHz = 2000;

    /// <summary>Windows quieter than this share of the track's loudest are not listened to.</summary>
    private const double SilenceShare = 0.02;

    private static readonly double[] MajorProfile =
    {
        6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88
    };

    private static readonly double[] MinorProfile =
    {
        6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17
    };

    // Camelot numbers by pitch class, C first. C major is 8B and A minor is 8A; the rest
    // follow the circle of fifths around the wheel.
    private static readonly int[] MajorWheel = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    private static readonly int[] MinorWheel = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };

    private readonly int _channels;
    private readonly int _hop;
    private readonly Fft _fft;
    private readonly double[] _taper;
    private readonly float[] _ring;
    private readonly double[] _real;
    private readonly double[] _imaginary;
    private readonly int[] _chromaOf;
    private readonly double[] _chroma = new double[12];
    private readonly List<double[]> _windows = new();

    private int _writeAt;
    private int _sinceHop;
    private long _seen;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromaScan"/> class.
    /// </summary>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    public ChromaScan(int channels, int sampleRate)
    {
        _channels = channels;
        _hop = Size / 2;
        _fft = new Fft(Size);
        _ring = new float[Size];
        _real = new double[Size];
        _imaginary = new double[Size];
        _taper = new double[Size];
        for (var i = 0; i < Size; i++)
        {
            _taper[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (Size - 1)));
        }

        // Which of the twelve each bin belongs to, worked out once.
        _chromaOf = new int[Size / 2];
        for (var bin = 0; bin < _chromaOf.Length; bin++)
        {
            var hz = bin * (double)sampleRate / Size;
            if (hz < LowestHz || hz > HighestHz)
            {
                _chromaOf[bin] = -1;
                continue;
            }

            // MIDI note 69 is A440; the pitch class is that, modulo the octave.
            var semitones = 69 + (12 * Math.Log2(hz / 440));
            _chromaOf[bin] = (int)(((int)Math.Round(semitones) % 12 + 12) % 12);
        }
    }

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
            if (_seen >= Size)
            {
                Accumulate();
            }
        }
    }

    /// <summary>
    /// The key, once the whole file has been fed.
    /// </summary>
    /// <returns>The key, or null when there was not enough pitched material to judge.</returns>
    public MusicalKey? Result()
    {
        if (_windows.Count < 8)
        {
            return null;
        }

        // The loud windows only. A long quiet passage would otherwise weigh as much as the
        // chorus, and a fade-out as much as the song.
        var loudest = _windows.Max(w => w.Sum());
        if (loudest <= 1e-12)
        {
            return null;
        }

        Array.Clear(_chroma);
        var used = 0;
        foreach (var window in _windows)
        {
            if (window.Sum() < loudest * SilenceShare)
            {
                continue;
            }

            used++;
            for (var i = 0; i < 12; i++)
            {
                _chroma[i] += window[i];
            }
        }

        if (used < 8 || _chroma.Sum() <= 1e-12)
        {
            return null;
        }

        var best = double.NegativeInfinity;
        var runnerUp = double.NegativeInfinity;
        string? camelot = null;

        for (var root = 0; root < 12; root++)
        {
            foreach (var minor in new[] { false, true })
            {
                var score = Correlation(_chroma, minor ? MinorProfile : MajorProfile, root);
                if (score > best)
                {
                    runnerUp = best;
                    best = score;
                    camelot = (minor ? MinorWheel[root] : MajorWheel[root]).ToString(CultureInfo.InvariantCulture)
                              + (minor ? "A" : "B");
                }
                else if (score > runnerUp)
                {
                    runnerUp = score;
                }
            }
        }

        if (camelot is null || !double.IsFinite(best))
        {
            return null;
        }

        // Two things have to hold. The winner must be well clear of the runner-up — a
        // tenth of a correlation is a comfortable win — and it must actually fit: a track
        // with no tonal content at all still has a best key, and on a bare drum loop the
        // margin alone called it at 0.55. Requiring the fit itself to be good is what
        // separates "this is the key" from "this is the least bad of twenty-four".
        var margin = Math.Clamp((best - runnerUp) / 0.1, 0, 1);
        var fit = Math.Clamp(best / 0.6, 0, 1);
        var confidence = margin * fit;
        return new MusicalKey(camelot, Math.Round(confidence, 2));
    }

    /// <summary>
    /// Pearson correlation of the track's pitch distribution against one key's profile.
    /// </summary>
    private static double Correlation(double[] chroma, double[] profile, int root)
    {
        double meanChroma = 0;
        double meanProfile = 0;
        for (var i = 0; i < 12; i++)
        {
            meanChroma += chroma[i];
            meanProfile += profile[i];
        }

        meanChroma /= 12;
        meanProfile /= 12;

        double covariance = 0;
        double spreadChroma = 0;
        double spreadProfile = 0;
        for (var i = 0; i < 12; i++)
        {
            var dc = chroma[i] - meanChroma;
            var dp = profile[((i - root) % 12 + 12) % 12] - meanProfile;
            covariance += dc * dp;
            spreadChroma += dc * dc;
            spreadProfile += dp * dp;
        }

        var denominator = Math.Sqrt(spreadChroma * spreadProfile);
        return denominator <= 1e-12 ? 0 : covariance / denominator;
    }

    private void Accumulate()
    {
        var read = _writeAt;
        for (var i = 0; i < Size; i++)
        {
            _real[i] = _ring[read] * _taper[i];
            _imaginary[i] = 0;
            read = read + 1 == _ring.Length ? 0 : read + 1;
        }

        _fft.Transform(_real, _imaginary);

        var window = new double[12];
        for (var bin = 1; bin < _chromaOf.Length; bin++)
        {
            var of = _chromaOf[bin];
            if (of < 0)
            {
                continue;
            }

            window[of] += Math.Sqrt((_real[bin] * _real[bin]) + (_imaginary[bin] * _imaginary[bin]));
        }

        _windows.Add(window);
    }
}
