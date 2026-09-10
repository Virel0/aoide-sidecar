using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The transform, against the definition it implements.
/// </summary>
public sealed class FftTests
{
    [Fact]
    public void A_sinusoid_lands_in_its_own_bin()
    {
        const int size = 1024;
        const int bin = 37;
        var (real, imaginary) = Buffers(size, n => Math.Cos(2 * Math.PI * bin * n / size));

        new Fft(size).Transform(real, imaginary);

        var loudest = 0;
        for (var k = 1; k < size / 2; k++)
        {
            if (Magnitude(real, imaginary, k) > Magnitude(real, imaginary, loudest))
            {
                loudest = k;
            }
        }

        Assert.Equal(bin, loudest);
    }

    [Fact]
    public void An_impulse_is_flat()
    {
        const int size = 256;
        var (real, imaginary) = Buffers(size, n => n == 0 ? 1 : 0);

        new Fft(size).Transform(real, imaginary);

        for (var k = 0; k < size; k++)
        {
            Assert.Equal(1, Magnitude(real, imaginary, k), 9);
        }
    }

    /// <summary>
    /// The one that would catch a wrong twiddle sign or a botched bit reversal: the fast
    /// transform against the slow one it is supposed to equal.
    /// </summary>
    [Fact]
    public void It_agrees_with_the_definition()
    {
        const int size = 64;
        var random = new Random(3);
        var samples = new double[size];
        for (var n = 0; n < size; n++)
        {
            samples[n] = (random.NextDouble() * 2) - 1;
        }

        var (real, imaginary) = Buffers(size, n => samples[n]);
        new Fft(size).Transform(real, imaginary);

        for (var k = 0; k < size; k++)
        {
            double expectedReal = 0;
            double expectedImaginary = 0;
            for (var n = 0; n < size; n++)
            {
                var angle = -2 * Math.PI * k * n / size;
                expectedReal += samples[n] * Math.Cos(angle);
                expectedImaginary += samples[n] * Math.Sin(angle);
            }

            Assert.Equal(expectedReal, real[k], 9);
            Assert.Equal(expectedImaginary, imaginary[k], 9);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(1000)]
    public void A_size_that_is_not_a_power_of_two_is_refused(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(size));
    }

    private static (double[] Real, double[] Imaginary) Buffers(int size, Func<int, double> sample)
    {
        var real = new double[size];
        var imaginary = new double[size];
        for (var n = 0; n < size; n++)
        {
            real[n] = sample(n);
        }

        return (real, imaginary);
    }

    private static double Magnitude(double[] real, double[] imaginary, int k) =>
        Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
}
