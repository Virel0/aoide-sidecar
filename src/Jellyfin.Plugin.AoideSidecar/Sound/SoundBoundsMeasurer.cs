using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Measures a file. Abstracted so the queue can be tested without launching ffmpeg.
/// </summary>
public interface ISoundBoundsMeasurer
{
    /// <summary>
    /// Decodes and scans one file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bounds, or null when there is nothing to trim.</returns>
    Task<SoundBounds?> MeasureAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Decodes with the ffmpeg Jellyfin ships and runs the phone's scan over the samples.
/// </summary>
/// <remarks>
/// The binaries come from <see cref="IMediaEncoder"/> rather than <c>PATH</c>: in the
/// official container ffmpeg lives under <c>/usr/lib/jellyfin-ffmpeg</c> and is not on
/// the path at all. Output is raw interleaved float at the file's native layout —
/// no resampling, which would move a threshold crossing by a few samples and make the
/// server disagree with the phone about where a note starts.
/// </remarks>
public sealed class FfmpegSoundBoundsMeasurer : ISoundBoundsMeasurer
{
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<FfmpegSoundBoundsMeasurer> _logger;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegSoundBoundsMeasurer"/> class.
    /// </summary>
    /// <param name="encoder">Jellyfin's media encoder, for the ffmpeg and ffprobe paths.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeout">Longest a single decode may run.</param>
    public FfmpegSoundBoundsMeasurer(IMediaEncoder encoder, ILogger<FfmpegSoundBoundsMeasurer> logger, TimeSpan timeout)
    {
        _encoder = encoder;
        _logger = logger;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<SoundBounds?> MeasureAsync(string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        var (channels, sampleRate) = await ProbeAsync(path, deadline.Token).ConfigureAwait(false);

        using var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _encoder.EncoderPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var arg in new[]
                 {
                     "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-vn",
                     "-f", "f32le", "-acodec", "pcm_f32le", "-",
                 })
        {
            ffmpeg.StartInfo.ArgumentList.Add(arg);
        }

        ffmpeg.Start();
        var stderr = ffmpeg.StandardError.ReadToEndAsync(deadline.Token);

        SoundBounds? bounds;
        try
        {
            // The scan reads the pipe as ffmpeg fills it, so a long file never sits in
            // memory whole.
            bounds = await Task.Run(
                () => SoundBoundsAnalyzer.Analyze(ffmpeg.StandardOutput.BaseStream, channels, sampleRate),
                deadline.Token).ConfigureAwait(false);
            await ffmpeg.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(ffmpeg);
            throw;
        }

        if (ffmpeg.ExitCode != 0)
        {
            var error = (await stderr.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException($"ffmpeg exited {ffmpeg.ExitCode}: {error}");
        }

        return bounds;
    }

    private async Task<(int Channels, int SampleRate)> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var ffprobe = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _encoder.ProbePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var arg in new[]
                 {
                     "-v", "error", "-select_streams", "a:0",
                     "-show_entries", "stream=channels,sample_rate", "-of", "json", path,
                 })
        {
            ffprobe.StartInfo.ArgumentList.Add(arg);
        }

        ffprobe.Start();
        var stdoutTask = ffprobe.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = ffprobe.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await ffprobe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(ffprobe);
            throw;
        }

        if (ffprobe.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {ffprobe.ExitCode}: {(await stderrTask.ConfigureAwait(false)).Trim()}");
        }

        using var document = JsonDocument.Parse(await stdoutTask.ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("ffprobe found no audio stream.");
        }

        var stream = streams[0];
        var channels = stream.GetProperty("channels").GetInt32();
        var rate = int.Parse(stream.GetProperty("sample_rate").GetString() ?? "0", CultureInfo.InvariantCulture);
        if (channels < 1 || rate < 1)
        {
            throw new InvalidOperationException($"ffprobe reported channels={channels} sample_rate={rate}.");
        }

        return (channels, rate);
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not kill {Process}", process.StartInfo.FileName);
        }
    }
}
