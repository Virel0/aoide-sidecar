using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Lazy measurement, the mtime-keyed caches, dedupe of in-flight work, and the one decode
/// that fills both caches — with a fake measurer in place of ffmpeg.
/// </summary>
public sealed class AudioAnalysisServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SoundBoundsRepository _bounds;
    private readonly AudioAnalysisRepository _analysis;
    private readonly BeatGridRepository _grids;
    private readonly FakeMeasurer _measurer = new();
    private readonly AudioAnalysisService _service;
    private readonly string _file;

    public AudioAnalysisServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-sound-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var database = new SyncDatabase(Path.Combine(_directory, "s.db"), NullLogger<SyncDatabase>.Instance);
        _bounds = new SoundBoundsRepository(database);
        _analysis = new AudioAnalysisRepository(database);
        _grids = new BeatGridRepository(database);
        _service = new AudioAnalysisService(
            _bounds, _analysis, _grids, _measurer, NullLogger<AudioAnalysisService>.Instance);
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
        _measurer.Result = new AudioMeasurement(new SoundBounds(1940, 5200), null, null);

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
        _measurer.Result = new AudioMeasurement(null, null, null);

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
        _measurer.Result = new AudioMeasurement(new SoundBounds(100, 200), null, null);
        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(5));
        _measurer.Result = new AudioMeasurement(new SoundBounds(300, 400), null, null);

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
    public async Task A_failure_is_remembered_for_the_analysis_cache_too()
    {
        _measurer.Throw = new InvalidOperationException("ffmpeg exited 1: broken");
        await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var lookup = (await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default))["t1"];

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

    [Fact]
    public async Task A_client_analysis_is_served_without_decoding()
    {
        Assert.True(await _service.StoreClientAnalysisAsync(
            "t1", _file, new Loudness(-9.7, -0.3), new Tempo(128, 0.8, 0.7), default));

        var lookup = (await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(lookup.Pending);
        Assert.Equal("client", lookup.Row!.Source);
        Assert.Equal(new Loudness(-9.7, -0.3), lookup.Row.Loudness);
        Assert.Equal(new Tempo(128, 0.8, 0.7), lookup.Row.Tempo);
        Assert.Equal(0, _measurer.Calls);
    }

    /// <summary>
    /// The reason the two endpoints share a service: one decode answers both, so a client
    /// that asked about silence has already paid for loudness and tempo.
    /// </summary>
    [Fact]
    public async Task Asking_about_sound_bounds_fills_the_analysis_cache_too()
    {
        _measurer.Result = new AudioMeasurement(
            new SoundBounds(1940, 5200),
            new Loudness(-9.7, -0.3),
            new Tempo(128, 0.82, 1.0));

        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var analysis = (await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(analysis.Pending);
        Assert.Equal(new Loudness(-9.7, -0.3), analysis.Row!.Loudness);
        Assert.Equal(new Tempo(128, 0.82, 1.0), analysis.Row.Tempo);
        Assert.Equal(1, _measurer.Calls);
    }

    /// <summary>
    /// And the other way round, which is the case that actually saves the work: the phone
    /// asks about loudness, and the desktop's silence trimming is answered for free.
    /// </summary>
    [Fact]
    public async Task Asking_about_analysis_fills_the_sound_bounds_cache_too()
    {
        _measurer.Result = new AudioMeasurement(
            new SoundBounds(1940, 5200),
            new Loudness(-9.7, -0.3),
            new Tempo(128, 0.82, 1.0));

        await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var bounds = (await _service.LookupAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(bounds.Pending);
        Assert.Equal(new SoundBounds(1940, 5200), bounds.Row!.Bounds);
        Assert.Equal(1, _measurer.Calls);
    }

    /// <summary>
    /// The third cache filled by the same decode. A client that asked about silence has
    /// paid for the beat grid too.
    /// </summary>
    [Fact]
    public async Task One_decode_fills_the_beat_grid_cache_as_well()
    {
        var grid = new BeatGrid(
            new[] { new BeatSegment(0, 180000, 495.3, 128.0, 3.9, 384) },
            4,
            0,
            15431,
            167431);
        _measurer.Result = new AudioMeasurement(
            new SoundBounds(1940, 5200),
            new Loudness(-9.7, -0.3),
            new Tempo(128, 1.0, 1.0),
            grid,
            new MusicalKey("8A", 0.71));

        await _service.LookupAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var lookup = (await _service.LookupGridAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.False(lookup.Pending);

        // Compared field by field: a record's generated equality treats its segment list
        // by reference, so an array and the list read back from JSON never match.
        var stored = lookup.Row!.Grid!;
        Assert.Equal(grid.Segments, stored.Segments);
        Assert.Equal(grid.BeatsPerBar, stored.BeatsPerBar);
        Assert.Equal(grid.DownbeatIndex, stored.DownbeatIndex);
        Assert.Equal(grid.MixInMs, stored.MixInMs);
        Assert.Equal(grid.MixOutMs, stored.MixOutMs);
        Assert.Equal(new MusicalKey("8A", 0.71), lookup.Row.Key);
        Assert.Equal(1, _measurer.Calls);
    }

    /// <summary>
    /// Segments go into one column as JSON, so a round trip through the database is worth
    /// asserting on rather than assuming.
    /// </summary>
    [Fact]
    public async Task Several_segments_survive_the_round_trip()
    {
        _measurer.Result = new AudioMeasurement(
            null,
            null,
            new Tempo(128, 1.0, 0.4),
            new BeatGrid(
                new[]
                {
                    new BeatSegment(0, 96000, 431.2, 128.02, 9.4, 205),
                    new BeatSegment(96000, 214000, 96210.5, 140.01, 11.8, 275)
                },
                4,
                0,
                null,
                null),
            null);

        await _service.LookupGridAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);

        var row = (await _service.LookupGridAsync(new[] { ("t1", _file) }, default))["t1"].Row;

        Assert.Equal(2, row!.Grid!.Segments.Count);
        Assert.Equal(140.01, row.Grid.Segments[1].Bpm);
        Assert.Equal(96210.5, row.Grid.Segments[1].AnchorMs);
        Assert.Null(row.Grid.MixInMs);
        Assert.Null(row.Key);
    }

    /// <summary>
    /// A tempo too weak to report is still stored, so the threshold can be reconsidered
    /// without decoding the library again.
    /// </summary>
    [Fact]
    public async Task A_low_confidence_tempo_is_stored_as_measured()
    {
        _measurer.Result = new AudioMeasurement(null, null, new Tempo(96, 0.2));

        await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default);
        await _service.DrainAsync(default);
        var lookup = (await _service.LookupAnalysisAsync(new[] { ("t1", _file) }, default))["t1"];

        Assert.Equal(new Tempo(96, 0.2), lookup.Row!.Tempo);
    }

    private sealed class FakeMeasurer : IAudioMeasurer
    {
        public AudioMeasurement Result { get; set; } = new(null, null, null);

        public Exception? Throw { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public int Calls { get; private set; }

        public async Task<AudioMeasurement> MeasureAsync(string path, CancellationToken cancellationToken)
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
