using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Measures a file. Abstracted so the queue can be tested without launching ffmpeg.
/// </summary>
public interface IAudioMeasurer
{
    /// <summary>
    /// Decodes and scans one file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Everything the decode produced; any part of it may be null.</returns>
    Task<AudioMeasurement> MeasureAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Decodes with the ffmpeg Jellyfin ships and takes every measurement from that one pass.
/// </summary>
/// <remarks>
/// <para>
/// The binaries come from <see cref="IMediaEncoder"/> rather than <c>PATH</c>: in the
/// official container ffmpeg lives under <c>/usr/lib/jellyfin-ffmpeg</c> and is not on
/// the path at all. Raw output is interleaved float at the file's native layout —
/// no resampling, which would move a threshold crossing by a few samples and make the
/// server disagree with the phone about where a note starts.
/// </para>
/// <para>
/// One invocation, two outputs. The decoded audio is split in the filter graph: one
/// branch is written to the pipe as raw PCM, for the sound-bounds scan and the tempo
/// envelope, and the other goes through <c>loudnorm</c> to a null muxer purely so the
/// filter prints its measurement. The null muxer writes no bytes, so the PCM on the pipe
/// is untouched. Doing it this way rather than as two ffmpeg runs halves the decoding,
/// which is the only expensive part.
/// </para>
/// </remarks>
public sealed class FfmpegAudioMeasurer : IAudioMeasurer
{
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<FfmpegAudioMeasurer> _logger;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegAudioMeasurer"/> class.
    /// </summary>
    /// <param name="encoder">Jellyfin's media encoder, for the ffmpeg and ffprobe paths.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeout">Longest a single decode may run.</param>
    public FfmpegAudioMeasurer(IMediaEncoder encoder, ILogger<FfmpegAudioMeasurer> logger, TimeSpan timeout)
    {
        _encoder = encoder;
        _logger = logger;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<AudioMeasurement> MeasureAsync(string path, CancellationToken cancellationToken)
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

        // -v info because loudnorm prints its result through the log at that level;
        // -nostats because otherwise ffmpeg's progress line can land in the middle of it.
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-nostats", "-v", "info", "-nostdin", "-i", path, "-vn",
                     "-filter_complex", "[0:a:0]asplit=2[raw][loud];[loud]loudnorm=print_format=json[measured]",
                     "-map", "[raw]", "-f", "f32le", "pipe:1",
                     "-map", "[measured]", "-f", "null", "-",
                 })
        {
            ffmpeg.StartInfo.ArgumentList.Add(arg);
        }

        ffmpeg.Start();
        var stderr = ffmpeg.StandardError.ReadToEndAsync(deadline.Token);

        var bounds = new SoundBoundsScan(channels, sampleRate);
        var tempo = new TempoScan(channels, sampleRate);
        var chroma = new ChromaScan(channels, sampleRate);
        var voice = new VocalScan(channels, sampleRate);
        long frames;

        try
        {
            // The scans read the pipe as ffmpeg fills it, so a long file never sits in
            // memory whole.
            frames = await Task.Run(
                () => PcmPump.Run(ffmpeg.StandardOutput.BaseStream, channels, new IPcmConsumer[] { bounds, tempo, chroma, voice }),
                deadline.Token).ConfigureAwait(false);
            await ffmpeg.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(ffmpeg);
            throw;
        }

        var log = await stderr.ConfigureAwait(false);
        if (ffmpeg.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited {ffmpeg.ExitCode}: {log.Trim()}");
        }

        // loudnorm's gating needs a few seconds of audio before its answer means anything,
        // and it will still print one for a two-second interlude.
        var loudness = frames >= LoudnessAnalyzer.MinimumSeconds * sampleRate
            ? LoudnessAnalyzer.Parse(log)
            : null;

        var beat = tempo.Result();
        var durationMs = frames * 1000.0 / sampleRate;
        var grid = BeatGridAnalyzer.Analyze(
            tempo.Onsets,
            tempo.LowOnsets,
            tempo.Rate,
            tempo.FirstOnsetMs,
            durationMs,
            beat,
            out var tracked);

        var arrangement = ArrangementAnalyzer.Analyze(
            tempo.Timbre,
            tempo.TimbreRate,
            tempo.Onsets,
            tempo.Rate,
            tempo.FirstOnsetMs,
            tracked,
            grid,
            durationMs,
            voice.Spans());

        return new AudioMeasurement(bounds.Result(), loudness, beat, grid, chroma.Result(), arrangement);
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
