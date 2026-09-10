using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The tempo estimator, over synthesised audio whose tempo is known exactly.
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="TempoScan"/> rather than calling the estimator
/// with a hand-made envelope, so the hop size and the log compression are under test too
/// — those choices decide as much as the autocorrelation does.
/// </remarks>
public sealed class TempoAnalyzerTests
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(170)]
    public void A_steady_beat_is_found_within_a_beat_per_minute_or_two(double bpm)
    {
        var tempo = Measure(Beats(bpm, seconds: 40));

        Assert.NotNull(tempo);
        Assert.True(
            Math.Abs(tempo!.Bpm - bpm) <= 3,
            $"expected about {bpm} BPM, measured {tempo.Bpm:F1}");
        Assert.True(
            tempo.Confidence >= TempoAnalyzer.MinimumConfidence,
            $"expected a confident answer, got {tempo.Confidence:F2}");
    }

    /// <summary>
    /// The case the octave rule exists for: a beat every half second is 120 BPM, not the
    /// 60 BPM that correlates exactly as well.
    /// </summary>
    [Fact]
    public void The_faster_reading_wins_when_both_explain_the_beat_equally()
    {
        var tempo = Measure(Beats(120, seconds: 40));

        Assert.NotNull(tempo);
        Assert.InRange(tempo!.Bpm, 117, 123);
    }

    [Fact]
    public void Noise_has_no_tempo_worth_reporting()
    {
        var random = new Random(1);
        var samples = new float[SampleRate * 40 * Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1) * 0.2f;
        }

        var tempo = Measure(samples);

        Assert.True(
            tempo is null || tempo.Confidence < TempoAnalyzer.MinimumConfidence,
            $"noise should not be confident, got {tempo?.Confidence:F2}");
    }

    /// <summary>
    /// A sustained note has no tempo, and this is the case that gets it wrong: the energy
    /// of a steady tone read on a fixed grid wobbles in step with the tone's own
    /// frequency, and that wobble is as periodic as any drum machine. A 220 Hz drone
    /// really did come back as a confident 120 BPM before the analysis window was tapered.
    /// </summary>
    [Theory]
    [InlineData(55)]
    [InlineData(110)]
    [InlineData(220)]
    [InlineData(440)]
    [InlineData(1000)]
    public void A_held_tone_has_no_tempo_at_all(double frequency)
    {
        var samples = new float[SampleRate * 40 * Channels];
        for (var frame = 0; frame < SampleRate * 40; frame++)
        {
            var value = (float)(0.4 * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));
            samples[(frame * Channels) + 0] = value;
            samples[(frame * Channels) + 1] = value;
        }

        var tempo = Measure(samples);

        Assert.True(
            tempo is null || tempo.Confidence < TempoAnalyzer.MinimumConfidence,
            $"a held {frequency} Hz tone should not be confident, got {tempo?.Confidence:F2} at {tempo?.Bpm:F1} BPM");
    }

    [Fact]
    public void Silence_has_no_tempo_at_all()
    {
        Assert.Null(Measure(new float[SampleRate * 40 * Channels]));
    }

    /// <summary>
    /// Ten seconds is the floor. A jingle is not a track and has no tempo to order by.
    /// </summary>
    [Fact]
    public void Something_too_short_to_autocorrelate_reports_nothing()
    {
        Assert.Null(Measure(Beats(120, seconds: 6)));
    }

    /// <summary>
    /// A beat that carries on under a chorus twice as loud as the verse: log compression
    /// is what stops the loud half from being the only half that counts.
    /// </summary>
    [Fact]
    public void A_beat_survives_a_change_in_level()
    {
        var samples = Beats(128, seconds: 40);
        for (var frame = 0; frame < samples.Length / Channels; frame++)
        {
            if (frame > SampleRate * 20)
            {
                continue;
            }

            samples[(frame * Channels) + 0] *= 0.1f;
            samples[(frame * Channels) + 1] *= 0.1f;
        }

        var tempo = Measure(samples);

        Assert.NotNull(tempo);
        Assert.InRange(tempo!.Bpm, 125, 131);
        Assert.True(tempo.Confidence >= TempoAnalyzer.MinimumConfidence, $"got {tempo.Confidence:F2}");
    }

    /// <summary>
    /// Percussive hits at a fixed interval, decaying, over a quiet floor — a drum machine
    /// with none of the music.
    /// </summary>
    private static float[] Beats(double bpm, int seconds)
    {
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];
        var period = 60.0 / bpm * SampleRate;
        var random = new Random(7);

        for (var frame = 0; frame < frames; frame++)
        {
            var sinceBeat = frame % period;
            var envelope = Math.Exp(-sinceBeat / (SampleRate * 0.04));
            var hit = (float)(envelope * 0.8 * ((random.NextDouble() * 2) - 1));
            var floor = (float)(((random.NextDouble() * 2) - 1) * 0.002);

            samples[(frame * Channels) + 0] = hit + floor;
            samples[(frame * Channels) + 1] = hit + floor;
        }

        return samples;
    }

    private static Tempo? Measure(float[] samples)
    {
        var scan = new TempoScan(Channels, SampleRate);
        scan.Feed(samples);
        return scan.Result();
    }
}
