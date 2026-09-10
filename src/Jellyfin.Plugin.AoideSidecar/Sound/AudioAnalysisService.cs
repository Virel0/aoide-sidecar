using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.AoideSidecar.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// What the service knows about one requested file's sound bounds right now.
/// </summary>
/// <param name="Row">The cached row, when current.</param>
/// <param name="Pending">True when a measurement has been queued and the row is not yet usable.</param>
public sealed record SoundBoundsLookup(SoundBoundsRow? Row, bool Pending);

/// <summary>
/// What the service knows about one requested file's loudness and tempo right now.
/// </summary>
/// <param name="Row">The cached row, when current.</param>
/// <param name="Pending">True when a measurement has been queued and the row is not yet usable.</param>
public sealed record AudioAnalysisLookup(AudioAnalysisRow? Row, bool Pending);

/// <summary>
/// What the service knows about one requested file's beat grid right now.
/// </summary>
/// <param name="Row">The cached row, when current.</param>
/// <param name="Pending">True when a measurement has been queued and the row is not yet usable.</param>
public sealed record BeatGridLookup(BeatGridRow? Row, bool Pending);

/// <summary>
/// Serves measurements from the caches and queues the files it does not have.
/// </summary>
/// <remarks>
/// <para>
/// Lazy on purpose. A library can hold tens of thousands of tracks and a decode costs
/// real CPU, so nothing is measured until someone asks — and then the answer is
/// "pending" this time and cached from the next request on. One worker drains the
/// queue; the concurrency is configurable but defaults to a single decode at a time so
/// a first sync of a big playlist does not turn the server into a transcoder farm.
/// </para>
/// <para>
/// One queue serves both endpoints. A file asked about for its sound bounds is decoded
/// once and its loudness and tempo fall out of the same pass, so whichever endpoint is
/// asked first pays for the other. That is the whole reason these live together.
/// </para>
/// <para>
/// The caches are keyed by the file's modification time. A replaced file re-measures; a
/// file whose measurement failed is not retried until it changes, so one broken file
/// cannot cost a decode on every request.
/// </para>
/// </remarks>
public sealed class AudioAnalysisService : IDisposable
{
    private const string ServerSource = "server";
    private const string ClientSource = "client";

    private readonly SoundBoundsRepository _bounds;
    private readonly AudioAnalysisRepository _analysis;
    private readonly BeatGridRepository _grids;
    private readonly IAudioMeasurer _measurer;
    private readonly ILogger<AudioAnalysisService> _logger;
    private readonly int _concurrency;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _startLock = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioAnalysisService"/> class.
    /// </summary>
    /// <param name="bounds">The sound-bounds cache.</param>
    /// <param name="analysis">The loudness and tempo cache.</param>
    /// <param name="grids">The beat-grid cache.</param>
    /// <param name="measurer">What actually decodes a file.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="concurrency">How many files may decode at once.</param>
    public AudioAnalysisService(
        SoundBoundsRepository bounds,
        AudioAnalysisRepository analysis,
        BeatGridRepository grids,
        IAudioMeasurer measurer,
        ILogger<AudioAnalysisService> logger,
        int concurrency = 1)
    {
        _bounds = bounds;
        _analysis = analysis;
        _grids = grids;
        _measurer = measurer;
        _logger = logger;
        _concurrency = Math.Max(1, concurrency);
    }

    /// <summary>
    /// Gets how many files are queued or decoding right now.
    /// </summary>
    public int InFlight => _inFlight.Count;

    /// <summary>
    /// Looks up sound bounds for several files, queuing any that need measuring.
    /// </summary>
    /// <param name="files">Id and current path of each file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lookup per id.</returns>
    public async Task<Dictionary<string, SoundBoundsLookup>> LookupAsync(
        IReadOnlyList<(string Id, string Path)> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        var cached = await _bounds.GetManyAsync(files.Select(f => f.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);

        return Resolve(
            files,
            id => cached.TryGetValue(id, out var row) ? row : null,
            row => row.MtimeTicks,
            (row, pending) => new SoundBoundsLookup(row, pending));
    }

    /// <summary>
    /// Looks up loudness and tempo for several files, queuing any that need measuring.
    /// </summary>
    /// <param name="files">Id and current path of each file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lookup per id.</returns>
    public async Task<Dictionary<string, AudioAnalysisLookup>> LookupAnalysisAsync(
        IReadOnlyList<(string Id, string Path)> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        var cached = await _analysis.GetManyAsync(files.Select(f => f.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);

        return Resolve(
            files,
            id => cached.TryGetValue(id, out var row) ? row : null,
            row => row.MtimeTicks,
            (row, pending) => new AudioAnalysisLookup(row, pending));
    }

    /// <summary>
    /// Looks up beat grids for several files, queuing any that need measuring.
    /// </summary>
    /// <param name="files">Id and current path of each file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lookup per id.</returns>
    public async Task<Dictionary<string, BeatGridLookup>> LookupGridAsync(
        IReadOnlyList<(string Id, string Path)> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        var cached = await _grids.GetManyAsync(files.Select(f => f.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);

        return Resolve(
            files,
            id => cached.TryGetValue(id, out var row) ? row : null,
            row => row.MtimeTicks,
            (row, pending) => new BeatGridLookup(row, pending));
    }

    /// <summary>
    /// Queues a file for measurement unless it is already queued.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <param name="path">Its file.</param>
    /// <param name="mtimeTicks">The file's modification time now.</param>
    /// <returns>True if newly queued.</returns>
    public bool Enqueue(string id, string path, long mtimeTicks)
    {
        if (!_inFlight.TryAdd(id, 0))
        {
            return false;
        }

        EnsureWorker();
        _queue.Writer.TryWrite(new Job(id, path, mtimeTicks));
        return true;
    }

    /// <summary>
    /// Stores sound bounds a client measured itself, so they can be served without decoding.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <param name="path">Its file, for the modification time the row is keyed on.</param>
    /// <param name="bounds">The client's result, or null for nothing to trim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if stored; false when there is no file to key on.</returns>
    public async Task<bool> StoreClientMeasurementAsync(
        string id,
        string path,
        SoundBounds? bounds,
        CancellationToken cancellationToken)
    {
        var mtime = Mtime(path);
        if (mtime is null)
        {
            return false;
        }

        await _bounds.UpsertAsync(
            new SoundBoundsRow(id, mtime.Value, bounds, ClientSource, null, Now()),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stores loudness and tempo a client measured itself.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <param name="path">Its file, for the modification time the row is keyed on.</param>
    /// <param name="loudness">The client's loudness, or null.</param>
    /// <param name="tempo">The client's tempo, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if stored; false when there is no file to key on.</returns>
    public async Task<bool> StoreClientAnalysisAsync(
        string id,
        string path,
        Loudness? loudness,
        Tempo? tempo,
        CancellationToken cancellationToken)
    {
        var mtime = Mtime(path);
        if (mtime is null)
        {
            return false;
        }

        await _analysis.UpsertAsync(
            new AudioAnalysisRow(id, mtime.Value, loudness, tempo, ClientSource, null, Now()),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Waits until the queue is empty. For tests and the sweep's progress.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!_inFlight.IsEmpty)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        _stopping.Dispose();
    }

    private static long? Mtime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Decides, for each file, whether its cached row is current, missing, or stale, and
    /// queues a decode for the ones that are not current.
    /// </summary>
    private Dictionary<string, TLookup> Resolve<TRow, TLookup>(
        IReadOnlyList<(string Id, string Path)> files,
        Func<string, TRow?> cached,
        Func<TRow, long> mtimeOf,
        Func<TRow?, bool, TLookup> lookup)
        where TRow : class
    {
        var result = new Dictionary<string, TLookup>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, path) in files)
        {
            var mtime = Mtime(path);
            if (mtime is null)
            {
                // No file to measure. Not pending — it would never resolve.
                result[id] = lookup(null, false);
                continue;
            }

            if (cached(id) is { } row && mtimeOf(row) == mtime)
            {
                result[id] = lookup(row, false);
                continue;
            }

            Enqueue(id, path, mtime.Value);
            result[id] = lookup(null, true);
        }

        return result;
    }

    private void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }

        lock (_startLock)
        {
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        using var gate = new SemaphoreSlim(_concurrency);
        var running = new List<Task>();

        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await MeasureAsync(job).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task MeasureAsync(Job job)
    {
        try
        {
            AudioMeasurement? measurement = null;
            string? error = null;
            try
            {
                measurement = await _measurer.MeasureAsync(job.Path, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "Could not measure {Path}", job.Path);
            }

            // Both caches are written from the one decode, whichever endpoint asked for
            // it. A row a client had posted for itself is replaced by the server's own
            // measurement, which is the same algorithm over the same file.
            //
            // Keyed on the mtime seen when the job was queued: if the file changed while
            // decoding, the next lookup sees a mismatch and measures again.
            await _bounds.UpsertAsync(
                new SoundBoundsRow(job.Id, job.MtimeTicks, measurement?.Bounds, ServerSource, error, Now()),
                CancellationToken.None).ConfigureAwait(false);

            await _analysis.UpsertAsync(
                new AudioAnalysisRow(
                    job.Id,
                    job.MtimeTicks,
                    measurement?.Loudness,
                    measurement?.Tempo,
                    ServerSource,
                    error,
                    Now()),
                CancellationToken.None).ConfigureAwait(false);

            await _grids.UpsertAsync(
                new BeatGridRow(
                    job.Id,
                    job.MtimeTicks,
                    measurement?.Grid,
                    measurement?.Key,
                    ServerSource,
                    error,
                    Now()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store measurements for {Id}", job.Id);
        }
        finally
        {
            _inFlight.TryRemove(job.Id, out _);
        }
    }

    private sealed record Job(string Id, string Path, long MtimeTicks);
}
