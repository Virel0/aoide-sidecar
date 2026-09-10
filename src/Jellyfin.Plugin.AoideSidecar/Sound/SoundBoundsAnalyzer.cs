namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Where the sound in a file actually starts and stops, in milliseconds.
/// </summary>
/// <param name="SoundStartMs">Where to begin. Zero when the file starts with sound.</param>
/// <param name="SoundEndMs">Where to stop. The file's length when it ends with sound.</param>
public sealed record SoundBounds(long SoundStartMs, long SoundEndMs);

/// <summary>
/// The phone's silence measurement, ported line for line.
/// </summary>
/// <remarks>
/// <para>
/// Both sides must agree — a track trimmed one way on the phone and another way on the
/// desktop would start and end in different places depending on where it was played.
/// So this is the reference algorithm from <c>PlaybackKit/SilenceBounds.swift</c> over
/// decoded float PCM, not an approximation of it: a frame is sound when any channel's
/// absolute value exceeds a fixed threshold, the first and last such frames become
/// milliseconds by schoolbook rounding, a lead and a tail margin are applied, and a trim
/// too small to be worth a seek is reported as nothing at all.
/// </para>
/// <para>
/// ffmpeg's <c>silencedetect</c> was the suggested tool and is deliberately not used. It
/// detects runs of silence with its own minimum-duration window and reports its own
/// crossings — a second algorithm approximating the first. Decoding to PCM and running
/// the phone's scan leaves only the decoder to differ, and for lossless audio it does not.
/// </para>
/// </remarks>
public static class SoundBoundsAnalyzer
{
    /// <summary>
    /// Quieter than this is silence: −55 dBFS, under a fade's tail but above the noise
    /// floor of a vinyl rip.
    /// </summary>
    public const float Threshold = 0.00178f;

    /// <summary>Kept before the first sound, so a note's attack is never clipped.</summary>
    public const int LeadMarginMs = 60;

    /// <summary>Kept after the last sound, so a reverb tail is not cut mid-decay.</summary>
    public const int TailMarginMs = 200;

    /// <summary>Trimming less than this at both ends is not worth the seek.</summary>
    public const int MinimumTrimMs = 300;

    /// <summary>
    /// Scans interleaved 32-bit float PCM and returns the sounding span, or null when
    /// there is nothing worth trimming: a file that starts and ends with sound, or one
    /// with no sound at all.
    /// </summary>
    /// <param name="pcm">Interleaved little-endian float samples, as ffmpeg emits with <c>-f f32le</c>.</param>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    /// <returns>The bounds, or null.</returns>
    public static SoundBounds? Analyze(Stream pcm, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        var scan = new SoundBoundsScan(channels, sampleRate);
        PcmPump.Run(pcm, channels, new IPcmConsumer[] { scan });
        return scan.Result();
    }

    // Swift's Duration.milliseconds(Int64((frames / rate * 1000).rounded())): .rounded()
    // is schoolbook rounding, halves away from zero, not the banker's rounding .NET
    // defaults to. A half-millisecond frame position must land on the same side.
    internal static long ToMilliseconds(long frames, int sampleRate) =>
        (long)Math.Round(frames / (double)sampleRate * 1000, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The scan itself, as a consumer, so the same decode can also be measured for loudness
/// and tempo. <see cref="SoundBoundsAnalyzer.Analyze"/> is this class over one stream.
/// </summary>
internal sealed class SoundBoundsScan : IPcmConsumer
{
    private readonly int _channels;
    private readonly int _sampleRate;

    private long _total;
    private long? _firstLoud;
    private long? _lastLoud;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundBoundsScan"/> class.
    /// </summary>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="sampleRate">Frames per second.</param>
    public SoundBoundsScan(int channels, int sampleRate)
    {
        _channels = channels;
        _sampleRate = sampleRate;
    }

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var loud = false;
            var at = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                if (Math.Abs(samples[at + channel]) > SoundBoundsAnalyzer.Threshold)
                {
                    loud = true;
                    break;
                }
            }

            if (loud)
            {
                var index = _total + frame;
                _firstLoud ??= index;
                _lastLoud = index;
            }
        }

        _total += frames;
    }

    /// <summary>
    /// The span, once the whole file has been fed.
    /// </summary>
    /// <returns>The bounds, or null when there is nothing worth trimming.</returns>
    public SoundBounds? Result()
    {
        if (_total == 0 || _firstLoud is null || _lastLoud is null)
        {
            return null;
        }

        var length = SoundBoundsAnalyzer.ToMilliseconds(_total, _sampleRate);
        var start = Math.Max(0, SoundBoundsAnalyzer.ToMilliseconds(_firstLoud.Value, _sampleRate) - SoundBoundsAnalyzer.LeadMarginMs);
        var end = Math.Min(length, SoundBoundsAnalyzer.ToMilliseconds(_lastLoud.Value, _sampleRate) + SoundBoundsAnalyzer.TailMarginMs);

        if (start < SoundBoundsAnalyzer.MinimumTrimMs && length - end < SoundBoundsAnalyzer.MinimumTrimMs)
        {
            return null;
        }

        return new SoundBounds(start, end);
    }
}
