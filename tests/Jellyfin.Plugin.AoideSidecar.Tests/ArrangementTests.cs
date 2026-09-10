using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Structure detection, over synthesised music built out of sections at known bar lines.
/// </summary>
public sealed class ArrangementTests
{
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const double Bpm = 128;
    private static readonly double BarMs = 4 * 60000 / Bpm;

    private readonly ITestOutputHelper _output;

    public ArrangementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// A record built as sixteen bars of each thing in turn. The boundaries are where the
    /// music changes, and that is what the checkerboard is looking for.
    /// </summary>
    [Fact]
    public void The_parts_of_a_track_are_found_where_they_actually_change()
    {
        var arrangement = Measure(Arranged());

        Assert.NotNull(arrangement);
        foreach (var s in arrangement!.Sections)
        {
            _output.WriteLine($"{s.StartMs,9:F0}..{s.EndMs,-9:F0} bar {s.StartMs / BarMs,5:F1} {s.Kind,-10} energy={s.Energy:F2}");
        }

        Assert.InRange(arrangement.Sections.Count, 5, 8);

        // Contiguous, and covering the record end to end.
        Assert.Equal(0, arrangement.Sections[0].StartMs);
        for (var i = 1; i < arrangement.Sections.Count; i++)
        {
            Assert.Equal(arrangement.Sections[i - 1].EndMs, arrangement.Sections[i].StartMs);
        }

        // The real changes are at bars 16, 32, 64, 80 and 96. Every boundary the analyser
        // found should be one of them rather than somewhere arbitrary.
        double[] truth = { 16, 32, 64, 80, 96 };
        foreach (var section in arrangement.Sections.Skip(1))
        {
            var bar = section.StartMs / BarMs;
            var nearest = truth.Min(t => Math.Abs(t - bar));
            Assert.True(nearest <= 1, $"a section began at bar {bar:F1}, {nearest:F1} bars from any real change");
        }
    }

    /// <summary>
    /// Boundaries are published against the grid, not against the beats they were spotted
    /// at. The two are a few milliseconds apart when the tracker ran true and a beat or
    /// three apart when it did not, and a client told "this section starts on a downbeat"
    /// has only the grid to check that against.
    /// </summary>
    [Fact]
    public void Section_boundaries_land_on_bar_lines_of_the_published_grid()
    {
        var samples = Arranged();
        var grid = Grid(samples);
        var arrangement = Measure(samples);

        Assert.NotNull(grid);
        Assert.NotNull(arrangement);

        var segment = grid!.Segments[0];
        var barMs = grid.BeatsPerBar!.Value * 60000 / segment.Bpm;

        foreach (var section in arrangement!.Sections.Skip(1))
        {
            var bars = (section.StartMs - segment.AnchorMs) / barMs;
            Assert.True(
                Math.Abs(bars - Math.Round(bars)) < 0.01,
                $"a section began {bars:F3} bars from the anchor, which is not a bar line");
        }
    }

    /// <summary>
    /// A build sits between something quieter and something louder; a breakdown sits
    /// between two louder things. That difference in position is what separates them —
    /// without it every quiet stretch before a drop read as a build, including breakdowns,
    /// and a breakdown is where a client would most like to bring a record in.
    /// </summary>
    [Fact]
    public void A_section_that_climbs_into_a_drop_is_a_build()
    {
        var arrangement = Measure(Arranged());

        Assert.NotNull(arrangement);
        var build = arrangement!.Sections.FirstOrDefault(s => s.Kind == SectionKind.Build);
        Assert.NotNull(build);
        Assert.InRange(build!.StartMs / BarMs, 15, 17);

        var breakdown = arrangement.Sections.First(s => s.Kind == SectionKind.Breakdown);
        Assert.InRange(breakdown.StartMs / BarMs, 63, 65);
    }

    /// <summary>
    /// Energy is normalised inside the track, so the loudest part of a quiet record reads
    /// as its loudest part. What matters is the ordering, not the absolute figures.
    /// </summary>
    [Fact]
    public void The_loud_parts_read_as_louder_than_the_quiet_ones()
    {
        var arrangement = Measure(Arranged());

        Assert.NotNull(arrangement);
        var first = arrangement!.Sections[0];
        var loudest = arrangement.Sections.MaxBy(s => s.Energy)!;

        Assert.True(first.Energy < 0.5, $"the intro read at energy {first.Energy:F2}");
        Assert.True(loudest.Energy > 0.8, $"the loudest section read at only {loudest.Energy:F2}");
        Assert.True(loudest.StartMs > first.EndMs, "the loudest section should not be the intro");
    }

    /// <summary>
    /// The names that can be given from the signal alone. A quiet opening is an intro and
    /// the loudest, heaviest thing is a drop; anything the evidence does not settle stays
    /// unknown, which is a real answer rather than a failure.
    /// </summary>
    [Fact]
    public void Sections_are_named_only_where_the_signal_says_so()
    {
        var arrangement = Measure(Arranged());

        Assert.NotNull(arrangement);
        Assert.Equal(SectionKind.Intro, arrangement!.Sections[0].Kind);
        Assert.Contains(arrangement.Sections, s => s.Kind == SectionKind.Drop);

        var allowed = new[]
        {
            SectionKind.Intro, SectionKind.Build, SectionKind.Drop,
            SectionKind.Breakdown, SectionKind.Outro, SectionKind.Unknown
        };
        Assert.All(arrangement.Sections, s => Assert.Contains(s.Kind, allowed));
    }

    /// <summary>
    /// A mix is counted in phrases. This record changes every sixteen bars, so eight or
    /// sixteen are both defensible readings of it — what must not happen is a confident
    /// answer that is neither.
    /// </summary>
    [Fact]
    public void The_phrase_length_is_read_off_where_the_changes_fall()
    {
        var arrangement = Measure(Arranged());

        Assert.NotNull(arrangement);
        _output.WriteLine($"phraseBars={arrangement!.PhraseBars} anchor={arrangement.PhraseAnchorMs}");

        if (arrangement.PhraseBars is null)
        {
            Assert.Null(arrangement.PhraseAnchorMs);
            return;
        }

        Assert.Contains(arrangement.PhraseBars.Value, new[] { 8, 16, 32 });
        Assert.NotNull(arrangement.PhraseAnchorMs);
    }

    /// <summary>
    /// A voice in the middle of the image, against a pad spread wide. This is the mechanism
    /// the vocal guess rests on, tested where it is unambiguous.
    /// </summary>
    [Fact]
    public void A_centred_voice_over_a_wide_pad_is_found()
    {
        var arrangement = Measure(WithVoice(fromMs: 40_000, toMs: 80_000, stereo: true));

        Assert.NotNull(arrangement);
        Assert.NotNull(arrangement!.Vocals);
        foreach (var v in arrangement.Vocals!)
        {
            _output.WriteLine($"vocal {v.StartMs:F0}..{v.EndMs:F0}ms");
        }

        Assert.NotEmpty(arrangement.Vocals);
        var overlap = arrangement.Vocals.Sum(v => Math.Max(0, Math.Min(v.EndMs, 80_000) - Math.Max(v.StartMs, 40_000)));
        Assert.True(overlap >= 20_000, $"only {overlap:F0} ms of the sung passage was found");
    }

    /// <summary>
    /// The distinction the whole thing turns on. A mono file has no stereo image to read,
    /// so nothing can be said — and "nothing can be said" must not arrive looking like
    /// "there is no singing", or a client would mix a vocal over a vocal on exactly the
    /// files it knows least about.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_read_says_so_rather_than_saying_no_vocals()
    {
        var arrangement = Measure(WithVoice(fromMs: 40_000, toMs: 80_000, stereo: false));

        Assert.NotNull(arrangement);
        Assert.Null(arrangement!.Vocals);
    }

    [Fact]
    public void Something_with_no_beat_has_no_arrangement()
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

    private static BeatGrid? Grid(float[] samples)
    {
        var tempo = new TempoScan(Channels, SampleRate);
        PcmPump.Run(new MemoryStream(ToBytes(samples)), Channels, new IPcmConsumer[] { tempo });
        return BeatGridAnalyzer.Analyze(
            tempo.Onsets, tempo.LowOnsets, tempo.Rate, tempo.FirstOnsetMs,
            samples.Length / (double)Channels / SampleRate * 1000, tempo.Result());
    }

    private static Arrangement? Measure(float[] samples)
    {
        var tempo = new TempoScan(Channels, SampleRate);
        var voice = new VocalScan(Channels, SampleRate);
        PcmPump.Run(new MemoryStream(ToBytes(samples)), Channels, new IPcmConsumer[] { tempo, voice });

        var durationMs = samples.Length / (double)Channels / SampleRate * 1000;
        var grid = BeatGridAnalyzer.Analyze(
            tempo.Onsets, tempo.LowOnsets, tempo.Rate, tempo.FirstOnsetMs, durationMs, tempo.Result(), out var beats);

        return ArrangementAnalyzer.Analyze(
            tempo.Timbre, tempo.TimbreRate, tempo.Onsets, tempo.Rate, tempo.FirstOnsetMs,
            beats, grid, durationMs, voice.Spans());
    }

    private static byte[] ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Sixteen bars of intro, sixteen of build, thirty-two of drop, sixteen of breakdown,
    /// sixteen of drop and sixteen of outro.
    /// </summary>
    private static float[] Arranged()
    {
        var plan = new (int Bars, double Kick, double Bass, double Hat, double Pad)[]
        {
            (16, 0.00, 0.00, 0.06, 0.10),
            (16, 0.25, 0.10, 0.14, 0.16),
            (32, 0.85, 0.40, 0.20, 0.14),
            (16, 0.00, 0.02, 0.05, 0.22),
            (16, 0.85, 0.40, 0.20, 0.14),
            (16, 0.05, 0.02, 0.05, 0.10),
        };

        var beatFrames = 60.0 / Bpm * SampleRate;
        var totalBars = plan.Sum(p => p.Bars);
        var frames = (int)(totalBars * 4 * beatFrames);
        var samples = new float[frames * Channels];
        var random = new Random(21);

        for (var frame = 0; frame < frames; frame++)
        {
            var beat = frame / beatFrames;
            var bar = (int)(beat / 4);

            var at = 0;
            var part = plan[0];
            foreach (var candidate in plan)
            {
                if (bar < at + candidate.Bars)
                {
                    part = candidate;
                    break;
                }

                at += candidate.Bars;
            }

            var index = (int)beat;
            var since = (beat - index) * 60.0 / Bpm;
            var value = part.Pad * Math.Sin(2 * Math.PI * 330 * frame / SampleRate);

            if (index % 4 == 0)
            {
                value += part.Kick * Math.Exp(-since / 0.06) * Math.Sin(2 * Math.PI * 55 * since);
            }
            else
            {
                value += part.Hat * Math.Exp(-since / 0.01) * ((random.NextDouble() * 2) - 1);
            }

            value += part.Bass * Math.Sin(2 * Math.PI * 82 * frame / SampleRate);

            var sample = (float)Math.Clamp(value, -1, 1);
            samples[(frame * Channels) + 0] = sample;
            samples[(frame * Channels) + 1] = sample;
        }

        return samples;
    }

    /// <summary>
    /// A steady groove with a wide pad throughout, and for one stretch a centred voice —
    /// a tone that slides and vibratos the way singing does and a sustained instrument
    /// does not.
    /// </summary>
    private static float[] WithVoice(double fromMs, double toMs, bool stereo)
    {
        const int seconds = 150;
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];
        var beatFrames = 60.0 / Bpm * SampleRate;
        var random = new Random(23);

        for (var frame = 0; frame < frames; frame++)
        {
            var ms = frame * 1000.0 / SampleRate;
            var beat = frame / beatFrames;
            var index = (int)beat;
            var since = (beat - index) * 60.0 / Bpm;

            double drums = index % 4 == 0
                ? 0.7 * Math.Exp(-since / 0.06) * Math.Sin(2 * Math.PI * 55 * since)
                : 0.10 * Math.Exp(-since / 0.01) * ((random.NextDouble() * 2) - 1);

            // A pad, hard left and hard right at different pitches: plenty of level in the
            // vocal band, none of it in the centre.
            var padLeft = 0.20 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate);
            var padRight = 0.20 * Math.Sin(2 * Math.PI * 660 * frame / SampleRate);

            double voice = 0;
            if (ms >= fromMs && ms < toMs)
            {
                // Syllables with gaps between them, the way singing goes — not one
                // unbroken tone, which is a thing no voice does and which a beat tracker
                // will happily mistake for a pulse of its own.
                var seconds_ = frame / (double)SampleRate;
                var into = (ms - fromMs) % 800;
                if (into < 560)
                {
                    var note = 260 * Math.Pow(2, Math.Floor((ms - fromMs) / 800) % 5 / 12.0);
                    var vibrato = 1 + (0.02 * Math.Sin(2 * Math.PI * 5.5 * seconds_));
                    var shape = Math.Min(1, into / 60) * Math.Min(1, (560 - into) / 80);
                    voice = shape * 0.30 * Math.Sin(2 * Math.PI * note * vibrato * seconds_);
                    voice += shape * 0.12 * Math.Sin(2 * Math.PI * note * 2 * vibrato * seconds_);
                }
            }

            var left = (float)Math.Clamp(drums + padLeft + voice, -1, 1);
            var right = (float)Math.Clamp(drums + (stereo ? padRight : padLeft) + voice, -1, 1);

            samples[(frame * Channels) + 0] = left;
            samples[(frame * Channels) + 1] = stereo ? right : left;
        }

        return samples;
    }
}
