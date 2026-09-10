namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// An in-place radix-2 FFT of one fixed size, with its twiddle factors precomputed.
/// </summary>
/// <remarks>
/// Written out rather than taken from a package for the same reason the tempo estimator
/// is: the plugin ships as a single managed DLL and every dependency it does not have is
/// one a server owner does not have to install. A hundred transforms a second of a
/// thousand points each is nothing next to the decode feeding them.
/// </remarks>
internal sealed class Fft
{
    private readonly int _size;
    private readonly int[] _reversed;
    private readonly double[] _cos;
    private readonly double[] _sin;

    /// <summary>
    /// Initializes a new instance of the <see cref="Fft"/> class.
    /// </summary>
    /// <param name="size">Transform length. Must be a power of two.</param>
    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size must be a power of two.");
        }

        _size = size;
        _reversed = new int[size];
        _cos = new double[size / 2];
        _sin = new double[size / 2];

        var bits = System.Numerics.BitOperations.Log2((uint)size);
        for (var i = 0; i < size; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
            {
                reversed = (reversed << 1) | ((i >> bit) & 1);
            }

            _reversed[i] = reversed;
        }

        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / size);
            _sin[i] = Math.Sin(2 * Math.PI * i / size);
        }
    }

    /// <summary>
    /// Transforms one buffer in place.
    /// </summary>
    /// <param name="real">Real parts, length equal to the transform size.</param>
    /// <param name="imaginary">Imaginary parts, same length. Zeroed for real input.</param>
    public void Transform(double[] real, double[] imaginary)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imaginary);
        if (real.Length != _size || imaginary.Length != _size)
        {
            throw new ArgumentException($"Both buffers must be {_size} long.", nameof(real));
        }

        for (var i = 0; i < _size; i++)
        {
            var j = _reversed[i];
            if (j <= i)
            {
                continue;
            }

            (real[i], real[j]) = (real[j], real[i]);
            (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
        }

        for (var length = 2; length <= _size; length <<= 1)
        {
            var half = length / 2;
            var step = _size / length;

            for (var start = 0; start < _size; start += length)
            {
                for (var offset = 0; offset < half; offset++)
                {
                    var twiddle = offset * step;
                    var wr = _cos[twiddle];
                    var wi = -_sin[twiddle];

                    var lo = start + offset;
                    var hi = lo + half;

                    var vr = (real[hi] * wr) - (imaginary[hi] * wi);
                    var vi = (real[hi] * wi) + (imaginary[hi] * wr);

                    real[hi] = real[lo] - vr;
                    imaginary[hi] = imaginary[lo] - vi;
                    real[lo] += vr;
                    imaginary[lo] += vi;
                }
            }
        }
    }
}
