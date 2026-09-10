using Jellyfin.Plugin.AoideSidecar.Sound;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Finding loudnorm's measurement in everything else ffmpeg says.
/// </summary>
public sealed class LoudnessAnalyzerTests
{
    /// <summary>
    /// A real run, verbatim: the filter's block sits in the middle of the log, and the
    /// numbers are quoted strings rather than JSON numbers.
    /// </summary>
    private const string RealLog = """
        [aist#0:0/pcm_s16le @ 0xffff853a0300] Guessed Channel Layout: stereo
        Input #0, wav, from '/tmp/tone.wav':
          Duration: 00:00:05.00, bitrate: 1411 kb/s
        Stream mapping:
          Stream #0:0 (pcm_s16le) -> asplit:default
        Output #0, f32le, to 'pipe:1':
        [Parsed_loudnorm_1 @ 0xffff815b00c0]
        {
        	"input_i" : "-9.70",
        	"input_tp" : "-0.31",
        	"input_lra" : "6.40",
        	"input_thresh" : "-19.75",
        	"output_i" : "-24.03",
        	"output_tp" : "-23.97",
        	"output_lra" : "0.00",
        	"output_thresh" : "-34.03",
        	"normalization_type" : "dynamic",
        	"target_offset" : "0.03"
        }
        [out#0/f32le @ 0xffff8538d280] video:0KiB audio:1723KiB subtitle:0KiB
        size=    1723KiB time=00:00:05.00 bitrate=2822.4kbits/s speed=44.6x
        """;

    [Fact]
    public void The_measurement_is_read_out_of_the_log()
    {
        var loudness = LoudnessAnalyzer.Parse(RealLog);

        Assert.NotNull(loudness);
        Assert.Equal(-9.70, loudness!.LoudnessLufs, 2);
        Assert.Equal(-0.31, loudness.TruePeakDbfs, 2);
    }

    [Fact]
    public void A_log_without_the_filter_yields_nothing()
    {
        Assert.Null(LoudnessAnalyzer.Parse("Input #0, wav, from '/tmp/tone.wav':\nStream mapping:"));
    }

    /// <summary>
    /// loudnorm's own floor. Digital silence prints −70 or −inf, and neither is a
    /// loudness a client could normalise against.
    /// </summary>
    [Theory]
    [InlineData("-70.00")]
    [InlineData("-120.00")]
    [InlineData("-inf")]
    public void Something_too_quiet_to_measure_yields_nothing(string reported)
    {
        Assert.Null(LoudnessAnalyzer.Parse(Block(reported, "-inf")));
    }

    [Fact]
    public void An_unparseable_number_yields_nothing()
    {
        Assert.Null(LoudnessAnalyzer.Parse(Block("nan", "-1.0")));
    }

    /// <summary>
    /// A track clipped by its own master really does peak above zero, and a client needs
    /// that to know it has no headroom left.
    /// </summary>
    [Fact]
    public void A_true_peak_above_full_scale_is_kept()
    {
        var loudness = LoudnessAnalyzer.Parse(Block("-6.20", "1.40"));

        Assert.NotNull(loudness);
        Assert.Equal(1.40, loudness!.TruePeakDbfs, 2);
    }

    /// <summary>
    /// Two files in one command would print two blocks. The last one is the one that
    /// belongs to the run that just finished.
    /// </summary>
    [Fact]
    public void The_last_block_wins()
    {
        var loudness = LoudnessAnalyzer.Parse(Block("-20.00", "-3.00") + "\n" + Block("-7.50", "-0.10"));

        Assert.NotNull(loudness);
        Assert.Equal(-7.50, loudness!.LoudnessLufs, 2);
    }

    private static string Block(string integrated, string truePeak) =>
        $$"""
        [Parsed_loudnorm_1 @ 0x0]
        {
        	"input_i" : "{{integrated}}",
        	"input_tp" : "{{truePeak}}",
        	"input_lra" : "0.00",
        	"normalization_type" : "dynamic"
        }
        """;
}
