using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Something that wants to look at every sample of a decoded file.
/// </summary>
public interface IPcmConsumer
{
    /// <summary>
    /// Takes the next block of interleaved samples. Always a whole number of frames.
    /// </summary>
    /// <param name="samples">Interleaved samples, channel-major within each frame.</param>
    void Feed(ReadOnlySpan<float> samples);
}

/// <summary>
/// Reads a PCM stream once and hands every sample to each consumer in turn.
/// </summary>
/// <remarks>
/// Decoding is by far the most expensive thing the sidecar does, so a file is decoded
/// once and every measurement is taken from that one pass. Consumers are fed
/// synchronously from the reading loop, in order, which keeps ffmpeg's pipe draining at
/// the speed of the slowest consumer and never buffers a whole file in memory.
/// </remarks>
public static class PcmPump
{
    /// <summary>
    /// Drains the stream, feeding each block to every consumer.
    /// </summary>
    /// <param name="pcm">Interleaved little-endian float samples, as ffmpeg emits with <c>-f f32le</c>.</param>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="consumers">Who gets the samples.</param>
    /// <returns>How many whole frames were read.</returns>
    public static long Run(Stream pcm, int channels, IReadOnlyList<IPcmConsumer> consumers)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        var frameBytes = channels * sizeof(float);

        // A chunk of whole frames. Bytes that arrive short of a frame boundary are held
        // over to the next read rather than split across a sample.
        var buffer = new byte[frameBytes * 16_384];
        var held = 0;
        long total = 0;

        while (true)
        {
            var read = pcm.Read(buffer, held, buffer.Length - held);
            if (read <= 0)
            {
                break;
            }

            var available = held + read;
            var frames = available / frameBytes;
            if (frames > 0)
            {
                // f32le is native order on every platform Jellyfin runs on, so the bytes
                // are the floats; no copy and no per-sample conversion.
                var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, frames * frameBytes));
                for (var i = 0; i < consumers.Count; i++)
                {
                    consumers[i].Feed(samples);
                }

                total += frames;
            }

            held = available - (frames * frameBytes);
            if (held > 0)
            {
                Buffer.BlockCopy(buffer, frames * frameBytes, buffer, 0, held);
            }
        }

        return total;
    }
}
