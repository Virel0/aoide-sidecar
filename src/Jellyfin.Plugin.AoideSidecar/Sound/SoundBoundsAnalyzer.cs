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

        var frameBytes = channels * sizeof(float);

        // A chunk of whole frames. Bytes that arrive short of a frame boundary are held
        // over to the next read rather than split across a sample.
        var buffer = new byte[frameBytes * 16_384];
        var held = 0;

        long total = 0;
        long? firstLoud = null;
        long? lastLoud = null;

        while (true)
        {
            var read = pcm.Read(buffer, held, buffer.Length - held);
            if (read <= 0)
            {
                break;
            }

            var available = held + read;
            var frames = available / frameBytes;
            var span = buffer.AsSpan(0, frames * frameBytes);

            for (var frame = 0; frame < frames; frame++)
            {
                var loud = false;
                var at = frame * frameBytes;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = BitConverter.ToSingle(span.Slice(at + (channel * sizeof(float)), sizeof(float)));
                    if (Math.Abs(sample) > Threshold)
                    {
                        loud = true;
                        break;
                    }
                }

                if (loud)
                {
                    var index = total + frame;
                    firstLoud ??= index;
                    lastLoud = index;
                }
            }

            total += frames;

            held = available - (frames * frameBytes);
            if (held > 0)
            {
                Buffer.BlockCopy(buffer, frames * frameBytes, buffer, 0, held);
            }
        }

        if (total == 0 || firstLoud is null || lastLoud is null)
        {
            return null;
        }

        var length = ToMilliseconds(total, sampleRate);
        var start = Math.Max(0, ToMilliseconds(firstLoud.Value, sampleRate) - LeadMarginMs);
        var end = Math.Min(length, ToMilliseconds(lastLoud.Value, sampleRate) + TailMarginMs);

        if (start < MinimumTrimMs && length - end < MinimumTrimMs)
        {
            return null;
        }

        return new SoundBounds(start, end);
    }

    // Swift's Duration.milliseconds(Int64((frames / rate * 1000).rounded())): .rounded()
    // is schoolbook rounding, halves away from zero, not the banker's rounding .NET
    // defaults to. A half-millisecond frame position must land on the same side.
    private static long ToMilliseconds(long frames, int sampleRate) =>
        (long)Math.Round(frames / (double)sampleRate * 1000, MidpointRounding.AwayFromZero);
}
