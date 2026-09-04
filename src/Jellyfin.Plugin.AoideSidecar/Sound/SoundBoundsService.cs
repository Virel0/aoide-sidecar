using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.AoideSidecar.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// What the service knows about one requested file right now.
/// </summary>
/// <param name="Row">The cached row, when current.</param>
/// <param name="Pending">True when a measurement has been queued and the row is not yet usable.</param>
public sealed record SoundBoundsLookup(SoundBoundsRow? Row, bool Pending);

/// <summary>
/// Serves measurements from the cache and queues the ones it does not have.
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
/// The cache is keyed by the file's modification time. A replaced file re-measures; a
/// file whose measurement failed is not retried until it changes, so one broken file
/// cannot cost a decode on every request.
/// </para>
/// </remarks>
public sealed class SoundBoundsService : IDisposable
{
    private const string ServerSource = "server";

    private readonly SoundBoundsRepository _repository;
    private readonly ISoundBoundsMeasurer _measurer;
    private readonly ILogger<SoundBoundsService> _logger;
    private readonly int _concurrency;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _startLock = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundBoundsService"/> class.
    /// </summary>
    /// <param name="repository">The cache.</param>
    /// <param name="measurer">What actually decodes a file.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="concurrency">How many files may decode at once.</param>
    public SoundBoundsService(
        SoundBoundsRepository repository,
        ISoundBoundsMeasurer measurer,
        ILogger<SoundBoundsService> logger,
        int concurrency = 1)
    {
        _repository = repository;
        _measurer = measurer;
        _logger = logger;
        _concurrency = Math.Max(1, concurrency);
    }

    /// <summary>
    /// Gets how many files are queued or decoding right now.
    /// </summary>
    public int InFlight => _inFlight.Count;

    /// <summary>
    /// Looks up several files, queuing any that need measuring.
    /// </summary>
    /// <param name="files">Id and current path of each file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lookup per id.</returns>
    public async Task<Dictionary<string, SoundBoundsLookup>> LookupAsync(
        IReadOnlyList<(string Id, string Path)> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        var cached = await _repository.GetManyAsync(files.Select(f => f.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var result = new Dictionary<string, SoundBoundsLookup>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, path) in files)
        {
            var mtime = Mtime(path);
            if (mtime is null)
            {
                // No file to measure. Not pending — it would never resolve.
                result[id] = new SoundBoundsLookup(null, false);
                continue;
            }

            if (cached.TryGetValue(id, out var row) && row.MtimeTicks == mtime)
            {
                result[id] = new SoundBoundsLookup(row, false);
                continue;
            }

            Enqueue(id, path, mtime.Value);
            result[id] = new SoundBoundsLookup(null, true);
        }

        return result;
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
    /// Stores a measurement a client made itself, so it can be served without decoding.
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

        await _repository.UpsertAsync(
            new SoundBoundsRow(id, mtime.Value, bounds, "client", null, Now()),
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
            SoundBounds? bounds = null;
            string? error = null;
            try
            {
                bounds = await _measurer.MeasureAsync(job.Path, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "Could not measure sound bounds for {Path}", job.Path);
            }

            // Keyed on the mtime seen when the job was queued: if the file changed while
            // decoding, the next lookup sees a mismatch and measures again.
            await _repository.UpsertAsync(
                new SoundBoundsRow(job.Id, job.MtimeTicks, bounds, ServerSource, error, Now()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store sound bounds for {Id}", job.Id);
        }
        finally
        {
            _inFlight.TryRemove(job.Id, out _);
        }
    }

    private sealed record Job(string Id, string Path, long MtimeTicks);
}
