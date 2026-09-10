using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The key estimate, over chord progressions whose key is not in question.
/// </summary>
public sealed class ChromaScanTests
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    private readonly ITestOutputHelper _output;

    public ChromaScanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// C major and A minor are the hard pair: the same seven notes, told apart only by
    /// which of them the music leans on. If the estimate can separate these two it is
    /// doing the thing it claims to.
    /// </summary>
    [Theory]
    [InlineData("C major: C F G C", "8B", new[] { 0, 5, 7, 0 }, false)]
    [InlineData("A minor: Am Dm E Am", "8A", new[] { 9, 2, 4, 9 }, true)]
    [InlineData("G major: G C D G", "9B", new[] { 7, 0, 2, 7 }, false)]
    [InlineData("E minor: Em Am B Em", "9A", new[] { 4, 9, 11, 4 }, true)]
    public void A_progression_is_read_as_its_key(string name, string expected, int[] roots, bool minor)
    {
        var key = Measure(Progression(roots, minor));

        Assert.NotNull(key);
        _output.WriteLine($"{name} -> {key!.Camelot} (confidence {key.Confidence:F2})");
        Assert.Equal(expected, key.Camelot);
    }

    [Fact]
    public void Silence_has_no_key()
    {
        Assert.Null(Measure(new float[SampleRate * 30 * Channels]));
    }

    /// <summary>
    /// Noise has no key, and the estimate should not pretend otherwise — it may name one,
    /// but it must not be sure of it.
    /// </summary>
    [Fact]
    public void Noise_is_not_a_confident_key()
    {
        var random = new Random(5);
        var samples = new float[SampleRate * 30 * Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1) * 0.3f;
        }

        var key = Measure(samples);
        _output.WriteLine($"noise -> {key?.Camelot} (confidence {key?.Confidence:F2})");
        Assert.True(key is null || key.Confidence < 0.5, $"noise came back as a confident {key?.Camelot}");
    }

    private static MusicalKey? Measure(float[] samples)
    {
        var scan = new ChromaScan(Channels, SampleRate);
        scan.Feed(samples);
        return scan.Result();
    }

    /// <summary>
    /// Four bars a chord, each a triad with a couple of harmonics, tonic first and last so
    /// the progression leans where a progression in that key leans.
    /// </summary>
    private static float[] Progression(int[] roots, bool minor, int repeats = 8)
    {
        const double secondsPerChord = 1.5;
        var chordFrames = (int)(SampleRate * secondsPerChord);
        var frames = chordFrames * roots.Length * repeats;
        var samples = new float[frames * Channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var chord = (frame / chordFrames) % roots.Length;
            var root = roots[chord];

            // Root, third, fifth. Minor chords on the tonic and subdominant, major on the
            // dominant, which is what makes a minor key sound like one.
            var third = minor && chord != 2 ? 3 : 4;
            double value = 0;
            foreach (var semitone in new[] { 0, third, 7 })
            {
                // 261.63 Hz is middle C, so pitch class 0 really is C.
                var hz = 261.626 * Math.Pow(2, (root + semitone) / 12.0);
                value += 0.22 * Math.Sin(2 * Math.PI * hz * frame / SampleRate);
                value += 0.07 * Math.Sin(2 * Math.PI * hz * 2 * frame / SampleRate);
            }

            var sample = (float)Math.Clamp(value, -1, 1);
            samples[(frame * Channels) + 0] = sample;
            samples[(frame * Channels) + 1] = sample;
        }

        return samples;
    }
}
