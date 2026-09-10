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

    /// <summary>
    /// The whole range, including the tempi that used to break it. 185 read as 92 until
    /// the onsets were widened, and 198 was pinned to the top of the search range until it
    /// was padded.
    /// </summary>
    [Theory]
    [InlineData(62)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(155)]
    [InlineData(170)]
    [InlineData(185)]
    [InlineData(198)]
    public void A_steady_beat_is_found_to_within_a_fifth_of_a_beat_per_minute(double bpm)
    {
        var tempo = Measure(Beats(bpm, seconds: 60));

        Assert.NotNull(tempo);
        Assert.True(
            Math.Abs(tempo!.Bpm - bpm) <= 0.2,
            $"expected about {bpm} BPM, measured {tempo.Bpm:F2}");
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
        Assert.InRange(tempo!.Bpm, 119.5, 120.5);
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
    /// A beat that carries on under a chorus ten times as loud as the verse: log
    /// compression is what stops the loud half from being the only half that counts.
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
    /// The case multi-band onsets exist for. A loud sustained bass note carries almost all
    /// the energy in the file, and the beat is a quiet tick in the top octaves. Watching
    /// the total energy, nothing happens; watching each band, the beat is obvious.
    /// </summary>
    [Fact]
    public void A_beat_carried_only_by_quiet_high_frequencies_is_still_found()
    {
        const double bpm = 128;
        var frames = SampleRate * 40;
        var samples = new float[frames * Channels];
        var period = 60.0 / bpm * SampleRate;
        var random = new Random(11);

        for (var frame = 0; frame < frames; frame++)
        {
            // A bass note that never stops, at ten times the level of the beat.
            var bass = 0.5 * Math.Sin(2 * Math.PI * 55 * frame / SampleRate);

            // A tick, high and quiet, on every beat.
            var since = frame % period;
            var tick = Math.Exp(-since / (SampleRate * 0.008)) * 0.05 * ((random.NextDouble() * 2) - 1);

            var value = (float)(bass + tick);
            samples[(frame * Channels) + 0] = value;
            samples[(frame * Channels) + 1] = value;
        }

        var tempo = Measure(samples);

        Assert.NotNull(tempo);
        Assert.InRange(tempo!.Bpm, 127.5, 128.5);
        Assert.True(tempo.Confidence >= TempoAnalyzer.MinimumConfidence, $"got {tempo.Confidence:F2}");
    }

    /// <summary>
    /// A sequenced track keeps one tempo from end to end, and a fixed grid would fit it.
    /// </summary>
    [Fact]
    public void A_track_that_never_changes_tempo_is_completely_stable()
    {
        var tempo = Measure(Beats(128, seconds: 180));

        Assert.NotNull(tempo);
        Assert.Equal(1.0, tempo!.Stability);
    }

    /// <summary>
    /// The question a beat grid actually turns on. This track averages out to a confident
    /// tempo it never actually plays for long, which is what a performance does.
    /// </summary>
    /// <summary>
    /// The more it moves, the less stable it reads — including a four per cent drift that
    /// still reports a confident tempo, which is exactly the track a grid would ruin.
    /// </summary>
    [Theory]
    [InlineData(120, 125, 0.6)]
    [InlineData(110, 130, 0.2)]
    [InlineData(105, 145, 0.1)]
    public void A_track_that_drifts_is_not_stable_however_confident_the_tempo_is(
        double fromBpm, double toBpm, double ceiling)
    {
        var tempo = Measure(Ramp(fromBpm, toBpm, seconds: 180));

        Assert.NotNull(tempo);
        Assert.NotNull(tempo!.Stability);
        Assert.True(
            tempo.Stability < ceiling,
            $"a track that ramped {fromBpm} to {toBpm} reported stability {tempo.Stability:F2}");
    }

    /// <summary>
    /// Under three windows there is nothing to compare, and guessing would be worse than
    /// admitting it.
    /// </summary>
    [Fact]
    public void A_track_too_short_to_compare_windows_reports_no_stability()
    {
        var tempo = Measure(Beats(128, seconds: 30));

        Assert.NotNull(tempo);
        Assert.Null(tempo!.Stability);
    }

    /// <summary>
    /// Beats whose spacing slides from one tempo to another, as a band does.
    /// </summary>
    private static float[] Ramp(double fromBpm, double toBpm, int seconds)
    {
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];
        var random = new Random(7);
        double phase = 0;
        double sinceBeat = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            var bpm = fromBpm + ((toBpm - fromBpm) * frame / (double)frames);
            phase += bpm / 60.0 / SampleRate;
            if (phase >= 1)
            {
                phase -= 1;
                sinceBeat = 0;
            }

            var envelope = Math.Exp(-sinceBeat / (SampleRate * 0.05));
            var value = (float)(0.5 * envelope * ((0.7 * Math.Sin(2 * Math.PI * 70 * frame / SampleRate))
                                                  + (0.3 * ((random.NextDouble() * 2) - 1))));
            samples[(frame * Channels) + 0] = value;
            samples[(frame * Channels) + 1] = value;
            sinceBeat++;
        }

        return samples;
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
