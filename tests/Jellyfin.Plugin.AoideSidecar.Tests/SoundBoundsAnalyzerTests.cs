using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// The phone's fixtures from <c>SilenceBoundsTests.swift</c>, generated the same way:
/// leading silence, a 440 Hz tone, trailing silence, 44.1 kHz stereo.
/// </summary>
public class SoundBoundsAnalyzerTests
{
    private const int Rate = 44_100;

    private static MemoryStream File(double leading, double sound, double trailing, float amplitude = 0.5f, int rate = Rate)
    {
        var total = (int)((leading + sound + trailing) * rate);
        var from = (int)(leading * rate);
        var to = (int)((leading + sound) * rate);
        var stream = new MemoryStream(total * 2 * sizeof(float));
        var writer = new BinaryWriter(stream);
        for (var i = 0; i < total; i++)
        {
            var v = i >= from && i < to ? amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / rate) : 0f;
            writer.Write(v);
            writer.Write(v);
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static bool Close(long a, long b, long withinMs = 60) => Math.Abs(a - b) <= withinMs;

    [Fact]
    public void Finds_the_sound_between_leading_and_trailing_silence_with_margins()
    {
        var found = SoundBoundsAnalyzer.Analyze(File(2, 3, 2.5), 2, Rate);

        Assert.NotNull(found);
        Assert.True(Close(found!.SoundStartMs, 2000 - SoundBoundsAnalyzer.LeadMarginMs));
        Assert.True(Close(found.SoundEndMs, 5000 + SoundBoundsAnalyzer.TailMarginMs));

        // The phone's test allows 60 ms of slack for its decoder; this scan is deterministic
        // over the same samples, so the spec's own "≈ 1.94 s and ≈ 5.2 s" is exact here.
        Assert.Equal(1940, found.SoundStartMs);
        Assert.Equal(5200, found.SoundEndMs);
    }

    [Fact]
    public void A_file_that_starts_and_ends_with_sound_is_left_alone()
    {
        Assert.Null(SoundBoundsAnalyzer.Analyze(File(0, 3, 0), 2, Rate));
    }

    [Fact]
    public void A_trim_too_small_to_matter_is_left_alone()
    {
        Assert.Null(SoundBoundsAnalyzer.Analyze(File(0.1, 3, 0.1), 2, Rate));
    }

    [Fact]
    public void Silence_at_one_end_only_is_trimmed_at_that_end()
    {
        var found = SoundBoundsAnalyzer.Analyze(File(0, 2, 3), 2, Rate);

        Assert.NotNull(found);
        Assert.Equal(0, found!.SoundStartMs);
        Assert.True(Close(found.SoundEndMs, 2000 + SoundBoundsAnalyzer.TailMarginMs));
    }

    [Fact]
    public void A_file_with_no_sound_at_all_is_nothing_to_trim()
    {
        Assert.Null(SoundBoundsAnalyzer.Analyze(File(2, 0, 0), 2, Rate));
    }

    [Fact]
    public void A_quiet_ending_above_the_threshold_is_kept()
    {
        // −40 dBFS: quiet, but music. Must not be treated as silence.
        Assert.Null(SoundBoundsAnalyzer.Analyze(File(0, 3, 0, amplitude: 0.01f), 2, Rate));
    }

    // ---- beyond the phone's fixtures: the edges its slack would hide -----------------

    [Fact]
    public void A_sample_exactly_at_the_threshold_is_silence()
    {
        // Strictly greater than, as in the reference. Four seconds of the threshold value
        // itself must read as an entirely silent file.
        var total = 4 * Rate;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        for (var i = 0; i < total; i++)
        {
            writer.Write(SoundBoundsAnalyzer.Threshold);
            writer.Write(-SoundBoundsAnalyzer.Threshold);
        }

        writer.Flush();
        stream.Position = 0;

        Assert.Null(SoundBoundsAnalyzer.Analyze(stream, 2, Rate));
    }

    [Fact]
    public void One_loud_sample_bounds_a_span_around_itself()
    {
        var total = 4 * Rate;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        for (var i = 0; i < total; i++)
        {
            var v = i == Rate ? 1f : 0f; // exactly 1.000 s in
            writer.Write(v);
            writer.Write(0f);
        }

        writer.Flush();
        stream.Position = 0;

        var found = SoundBoundsAnalyzer.Analyze(stream, 2, Rate);

        Assert.Equal(new SoundBounds(1000 - 60, 1000 + 200), found);
    }

    [Fact]
    public void Any_channel_counts()
    {
        // Sound on the right channel only is still sound.
        var total = 4 * Rate;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        for (var i = 0; i < total; i++)
        {
            writer.Write(0f);
            writer.Write(i == Rate ? 0.5f : 0f);
        }

        writer.Flush();
        stream.Position = 0;

        Assert.NotNull(SoundBoundsAnalyzer.Analyze(stream, 2, Rate));
    }

    [Fact]
    public void Half_milliseconds_round_away_from_zero_as_on_the_phone()
    {
        // At 8 kHz, frame 4 is 0.5 ms. Swift's .rounded() makes that 1; .NET's default
        // banker's rounding would make it 0, and the end would land a millisecond early.
        const int rate = 8_000;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        for (var i = 0; i < 5_000; i++)
        {
            writer.Write(i == 4 ? 0.5f : 0f);
        }

        writer.Flush();
        stream.Position = 0;

        var found = SoundBoundsAnalyzer.Analyze(stream, 1, rate);

        Assert.NotNull(found);
        Assert.Equal(0, found!.SoundStartMs);
        Assert.Equal(1 + SoundBoundsAnalyzer.TailMarginMs, found.SoundEndMs);
    }

    [Fact]
    public void An_empty_stream_is_nothing()
    {
        Assert.Null(SoundBoundsAnalyzer.Analyze(new MemoryStream(), 2, Rate));
    }

    [Fact]
    public void Frames_split_across_reads_are_not_torn()
    {
        // A stream that hands back awkward byte counts must still see every sample whole.
        var source = File(2, 3, 2.5);
        var found = SoundBoundsAnalyzer.Analyze(new DribbleStream(source, 7), 2, Rate);

        Assert.Equal(new SoundBounds(1940, 5200), found);
    }

    private sealed class DribbleStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _max;

        public DribbleStream(Stream inner, int max)
        {
            _inner = inner;
            _max = max;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _max));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
