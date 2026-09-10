using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The grid, over synthesised music whose beats are at known positions to the sample.
/// </summary>
public sealed class BeatGridTests
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    private readonly ITestOutputHelper _output;

    public BeatGridTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void A_sequenced_track_fits_one_grid_tightly()
    {
        var grid = Measure(Groove(128, seconds: 180, firstBeatMs: 500));

        Assert.NotNull(grid);
        var segment = Assert.Single(grid!.Segments);
        Assert.InRange(segment.Bpm, 127.9, 128.1);
        Assert.True(segment.ResidualMs <= 12, $"residual was {segment.ResidualMs} ms");
        Assert.True(segment.Beats > 300, $"only {segment.Beats} beats");
        _output.WriteLine($"one segment: bpm={segment.Bpm} residual={segment.ResidualMs}ms anchor={segment.AnchorMs}ms beats={segment.Beats}");
    }

    /// <summary>
    /// The anchor is the whole point: a tempo says how far apart the beats are, and only
    /// this says where they are. Bar lines land every four beats from it, so the anchor is
    /// right if it sits on the first beat or a whole number of bars after it.
    /// </summary>
    [Fact]
    public void The_anchor_lands_on_a_real_downbeat()
    {
        const double firstBeatMs = 500;
        var grid = Measure(Groove(128, seconds: 180, firstBeatMs: firstBeatMs));

        Assert.NotNull(grid);
        var segment = grid!.Segments[0];
        var barMs = 4 * 60000 / segment.Bpm;
        var offset = (segment.AnchorMs - firstBeatMs) % barMs;
        var error = Math.Min(Math.Abs(offset), Math.Abs(barMs - Math.Abs(offset)));

        _output.WriteLine($"anchor={segment.AnchorMs}ms first beat={firstBeatMs}ms error={error:F1}ms");
        Assert.True(error <= 20, $"the anchor was {error:F1} ms off a real downbeat");
    }

    [Fact]
    public void Four_four_is_recognised_and_so_is_its_downbeat()
    {
        var grid = Measure(Groove(128, seconds: 180, firstBeatMs: 500));

        Assert.NotNull(grid);
        Assert.Equal(4, grid!.BeatsPerBar);
        Assert.Equal(0, grid.DownbeatIndex);
    }

    [Fact]
    public void A_waltz_is_recognised_as_three()
    {
        var grid = Measure(Groove(150, seconds: 180, firstBeatMs: 250, beatsPerBar: 3));

        Assert.NotNull(grid);
        Assert.Equal(3, grid!.BeatsPerBar);
    }

    /// <summary>
    /// A track that genuinely changes tempo comes back as two fits rather than one smeared
    /// across the change, which would describe neither half.
    /// </summary>
    [Fact]
    public void A_tempo_change_comes_back_as_two_segments()
    {
        var grid = Measure(Concatenate(
            Groove(128, seconds: 90, firstBeatMs: 500),
            Groove(140, seconds: 90, firstBeatMs: 0)));

        Assert.NotNull(grid);
        foreach (var s in grid!.Segments)
        {
            _output.WriteLine($"segment {s.StartMs}..{s.EndMs}ms bpm={s.Bpm} residual={s.ResidualMs}ms beats={s.Beats}");
        }

        Assert.Equal(2, grid.Segments.Count);
        Assert.InRange(grid.Segments[0].Bpm, 127.5, 128.5);
        Assert.InRange(grid.Segments[1].Bpm, 139.5, 140.5);
        Assert.All(grid.Segments, s => Assert.True(s.ResidualMs <= 15, $"residual {s.ResidualMs} ms"));
    }

    /// <summary>
    /// Segments cover the track end to end, so a client asking which one holds a moment
    /// always gets an answer.
    /// </summary>
    [Fact]
    public void Segments_are_contiguous_and_cover_the_whole_track()
    {
        var grid = Measure(Concatenate(
            Groove(128, seconds: 90, firstBeatMs: 500),
            Groove(140, seconds: 90, firstBeatMs: 0)));

        Assert.NotNull(grid);
        Assert.Equal(0, grid!.Segments[0].StartMs);
        Assert.True(grid.Segments[^1].EndMs >= 179_000, $"last segment ended at {grid.Segments[^1].EndMs}");
        for (var i = 1; i < grid.Segments.Count; i++)
        {
            Assert.Equal(grid.Segments[i - 1].EndMs, grid.Segments[i].StartMs);
        }
    }

    /// <summary>
    /// An intro of pure atmosphere is loud but nothing in it starts, so a blend must not
    /// begin there. Mix points are the difference between a transition and a mess.
    /// </summary>
    [Fact]
    public void A_blend_does_not_begin_in_an_intro_that_is_only_atmosphere()
    {
        var samples = Concatenate(Pad(seconds: 20), Groove(128, seconds: 150, firstBeatMs: 0));
        var grid = Measure(samples);

        Assert.NotNull(grid);
        Assert.NotNull(grid!.MixInMs);
        Assert.NotNull(grid.MixOutMs);
        _output.WriteLine($"mixIn={grid.MixInMs}ms mixOut={grid.MixOutMs}ms");

        Assert.True(grid.MixInMs >= 18_000, $"a blend would have started at {grid.MixInMs} ms, inside the pad");
        Assert.True(grid.MixInMs <= 30_000, $"a blend would not start until {grid.MixInMs} ms, well after the groove");
        Assert.True(grid.MixOutMs > grid.MixInMs);
    }

    /// <summary>
    /// The tracker has to return an unbroken chain from the first hop to the last, so a
    /// track opening on a pad gets beats found in the pad. Fitting those made a whole extra
    /// segment describing nothing, at a residual of 33 ms; the beats at either end that
    /// carry nothing are dropped before anything is fitted.
    /// </summary>
    [Fact]
    public void Beats_imagined_in_an_intro_do_not_become_a_segment_of_their_own()
    {
        var grid = Measure(Concatenate(Pad(seconds: 20), Groove(128, seconds: 150, firstBeatMs: 0)));

        Assert.NotNull(grid);
        var segment = Assert.Single(grid!.Segments);
        _output.WriteLine($"one segment: {segment.StartMs}..{segment.EndMs}ms bpm={segment.Bpm} residual={segment.ResidualMs}ms");

        Assert.InRange(segment.Bpm, 127.9, 128.1);
        Assert.True(segment.ResidualMs <= 12, $"residual was {segment.ResidualMs} ms");

        // The fit still covers the whole track: a grid extrapolates across an intro
        // perfectly well, and a client asking which segment holds a moment needs an answer.
        Assert.Equal(0, segment.StartMs);
        Assert.True(segment.EndMs >= 169_000);
    }

    /// <summary>
    /// Both mix points sit on bar lines of the published grid — a transition that begins
    /// mid-bar is the problem the meter was worked out to avoid.
    /// </summary>
    [Fact]
    public void Mix_points_land_on_bar_lines()
    {
        var grid = Measure(Groove(128, seconds: 180, firstBeatMs: 500));

        Assert.NotNull(grid);
        Assert.NotNull(grid!.MixInMs);

        foreach (var point in new[] { grid.MixInMs!.Value, grid.MixOutMs!.Value })
        {
            var segment = grid.Segments.First(s => point >= s.StartMs && point <= s.EndMs);
            var beatMs = 60000 / segment.Bpm;
            var beats = (point - segment.AnchorMs) / beatMs;
            Assert.True(Math.Abs(beats - Math.Round(beats)) < 0.02, $"{point} ms is {beats:F2} beats from the anchor");
            Assert.True(Math.Abs(Math.Round(beats) % grid.BeatsPerBar!.Value) < 0.001, $"{point} ms is not a bar line");
        }
    }

    [Fact]
    public void Something_with_no_beat_has_no_grid()
    {
        var samples = new float[SampleRate * 60 * Channels];
        for (var frame = 0; frame < SampleRate * 60; frame++)
        {
            var value = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * frame / SampleRate));
            samples[(frame * Channels) + 0] = value;
            samples[(frame * Channels) + 1] = value;
        }

        Assert.Null(Measure(samples));
    }

    [Fact]
    public void Silence_has_no_grid()
    {
        Assert.Null(Measure(new float[SampleRate * 60 * Channels]));
    }

    private static BeatGrid? Measure(float[] samples)
    {
        var scan = new TempoScan(Channels, SampleRate);
        scan.Feed(samples);
        return BeatGridAnalyzer.Analyze(
            scan.Onsets,
            scan.LowOnsets,
            scan.Rate,
            scan.FirstOnsetMs,
            samples.Length / (double)Channels / SampleRate * 1000,
            scan.Result());
    }

    /// <summary>
    /// A drum pattern: a loud low kick on the downbeat, a quieter tick on the rest, and a
    /// pad underneath so the track is never silent between hits.
    /// </summary>
    private static float[] Groove(double bpm, int seconds, double firstBeatMs, int beatsPerBar = 4)
    {
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];
        var beatFrames = 60.0 / bpm * SampleRate;
        var firstFrame = firstBeatMs / 1000 * SampleRate;
        var random = new Random(13);

        for (var frame = 0; frame < frames; frame++)
        {
            var beat = (frame - firstFrame) / beatFrames;
            var index = (int)Math.Floor(beat);
            var since = (frame - firstFrame - (index * beatFrames)) / SampleRate;

            double value = 0.05 * Math.Sin(2 * Math.PI * 330 * frame / SampleRate);

            if (index >= 0 && since >= 0)
            {
                var downbeat = ((index % beatsPerBar) + beatsPerBar) % beatsPerBar == 0;
                if (downbeat)
                {
                    value += 0.8 * Math.Exp(-since / 0.06) * Math.Sin(2 * Math.PI * 55 * since);
                }
                else
                {
                    value += 0.12 * Math.Exp(-since / 0.01) * ((random.NextDouble() * 2) - 1);
                }
            }

            var sample = (float)Math.Clamp(value, -1, 1);
            samples[(frame * Channels) + 0] = sample;
            samples[(frame * Channels) + 1] = sample;
        }

        return samples;
    }

    /// <summary>A loud sustained pad. Plenty of level, nothing starting.</summary>
    private static float[] Pad(int seconds)
    {
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(0.4 * ((0.6 * Math.Sin(2 * Math.PI * 110 * frame / SampleRate))
                                       + (0.4 * Math.Sin(2 * Math.PI * 165 * frame / SampleRate))));
            samples[(frame * Channels) + 0] = value;
            samples[(frame * Channels) + 1] = value;
        }

        return samples;
    }

    private static float[] Concatenate(params float[][] parts)
    {
        var joined = new float[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(joined, at);
            at += part.Length;
        }

        return joined;
    }
}
