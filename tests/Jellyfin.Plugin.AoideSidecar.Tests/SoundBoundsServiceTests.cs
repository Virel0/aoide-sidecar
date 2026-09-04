using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Lazy measurement, the mtime-keyed cache, and dedupe of in-flight work — with a fake
/// measurer in place of ffmpeg.
/// </summary>
public sealed class SoundBoundsServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SoundBoundsRepository _repository;
    private readonly FakeMeasurer _measurer = new();
    private readonly SoundBoundsService _service;
    private readonly string _file;

    public SoundBoundsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-sound-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _repository = new SoundBoundsRepository(
            new SyncDatabase(Path.Combine(_directory, "s.db"), NullLogger<SyncDatabase>.Instance));
        _service = new SoundBoundsService(_repository, _measurer, NullLogger<SoundBoundsService>.Instance);
        _file = Path.Combine(_directory, "track.flac");
        File.WriteAllText(_file, "not really audio");
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task First_ask_is_pending_and_the_next_is_served_from_cache()
    {
        _measurer.Result = new SoundBounds(1940, 5200);

        var first = await _service.LookupAsync(new[] { ("t1", _file) }, default);
        Assert.True(first["t1"].Pending);

        await _service.DrainAsync(default);

        var second = await _service.LookupAsync(new[] { ("t1", _file) }, default);
        Assert.False(second["t1"].Pending);
        Assert.Equal(new SoundBounds(1940, 5200), second["t1"].Row!.Bounds);
        Assert.Equal(1, _measurer.Calls);
    }

    [Fact]
    public async Task Nothing_to_trim_is_cached_as_a_null_result_not_as_missing()
    {
        _measurer.Result = null;

        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);
        var lookup = (await _service.LookupAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(lookup.Pending);
        Assert.NotNull(lookup.Row);
        Assert.Null(lookup.Row!.Bounds);
        Assert.Null(lookup.Row.Error);
    }

    [Fact]
    public async Task Asking_twice_while_pending_measures_once()
    {
        _measurer.Hold = new TaskCompletionSource();

        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.LookupAsync(new[] { ("t1", _file) }, default);

        _measurer.Hold.SetResult();
        await _service.DrainAsync(default);

        Assert.Equal(1, _measurer.Calls);
    }

    [Fact]
    public async Task A_replaced_file_is_measured_again()
    {
        _measurer.Result = new SoundBounds(100, 200);
        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(5));
        _measurer.Result = new SoundBounds(300, 400);

        var stale = await _service.LookupAsync(new[] { ("t1", _file) }, default);
        Assert.True(stale["t1"].Pending);

        await _service.DrainAsync(default);
        var fresh = (await _service.LookupAsync(new[] { ("t1", _file) }, default))["t1"];
        Assert.Equal(new SoundBounds(300, 400), fresh.Row!.Bounds);
        Assert.Equal(2, _measurer.Calls);
    }

    [Fact]
    public async Task A_failed_measurement_is_remembered_and_not_retried_until_the_file_changes()
    {
        _measurer.Throw = new InvalidOperationException("ffmpeg exited 1: broken");
        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var lookup = (await _service.LookupAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(lookup.Pending);
        Assert.Contains("broken", lookup.Row!.Error, StringComparison.Ordinal);
        Assert.Equal(1, _measurer.Calls);
    }

    [Fact]
    public async Task A_missing_file_is_neither_pending_nor_measured()
    {
        var lookup = (await _service.LookupAsync(new[] { ("t1", Path.Combine(_directory, "gone.flac")) }, default))["t1"];

        Assert.False(lookup.Pending);
        Assert.Null(lookup.Row);
        Assert.Equal(0, _measurer.Calls);
    }

    [Fact]
    public async Task A_client_measurement_is_served_without_decoding()
    {
        Assert.True(await _service.StoreClientMeasurementAsync("t1", _file, new SoundBounds(10, 20), default));

        var lookup = (await _service.LookupAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(lookup.Pending);
        Assert.Equal("client", lookup.Row!.Source);
        Assert.Equal(new SoundBounds(10, 20), lookup.Row.Bounds);
        Assert.Equal(0, _measurer.Calls);
    }

    private sealed class FakeMeasurer : ISoundBoundsMeasurer
    {
        public SoundBounds? Result { get; set; }

        public Exception? Throw { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public int Calls { get; private set; }

        public async Task<SoundBounds?> MeasureAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hold is not null)
            {
                await Hold.Task.ConfigureAwait(false);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Result;
        }
    }
}
