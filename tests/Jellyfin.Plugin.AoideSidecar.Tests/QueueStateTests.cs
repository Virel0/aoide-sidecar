using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Handing playback between a user's own devices.
/// </summary>
public sealed class QueueStateTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncRepository _repository;

    public QueueStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-queue-tests", Guid.NewGuid().ToString("N"));
        _repository = new SyncRepository(
            new SyncDatabase(Path.Combine(_directory, "aoide-sync.db"), NullLogger<SyncDatabase>.Instance));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyncOpDto Queue(string opId, string deviceId, int position) => new()
    {
        OpId = opId,
        Entity = SyncEntities.QueueState,
        EntityId = deviceId,
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument
            .Parse($$"""{"device_id":"{{deviceId}}","deviceName":"Phone","position":{{position}},"elapsedMs":0}""")
            .RootElement.Clone(),
        CreatedAt = 100 + position
    };

    private static SyncOpDto Other(string opId, string entity) => new()
    {
        OpId = opId,
        Entity = entity,
        EntityId = "e-" + opId,
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument.Parse($$"""{"id":"{{opId}}"}""").RootElement.Clone(),
        CreatedAt = 1
    };

    [Fact]
    public async Task Only_the_latest_queue_survives_for_a_device()
    {
        // Playback moves constantly. Every superseded snapshot is a value that has been
        // overwritten, not history, and keeping them would make this the fastest-growing
        // table in the store.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[] { Queue("q1", "phone", 1), Queue("q2", "phone", 2), Queue("q3", "phone", 3) },
            receivedAt: 1000,
            default);

        var removed = await _repository.CompactQueueStateAsync(user, new[] { "phone" }, default);

        Assert.Equal(2, removed);
        var state = Assert.Single(await _repository.GetQueueStatesAsync(user, default));
        Assert.Equal(3, state.Payload.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task Each_device_keeps_its_own_queue()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[] { Queue("q1", "phone", 1), Queue("q2", "laptop", 5), Queue("q3", "phone", 2) },
            receivedAt: 1000,
            default);

        await _repository.CompactQueueStateAsync(user, new[] { "phone", "laptop" }, default);

        var states = await _repository.GetQueueStatesAsync(user, default);

        Assert.Equal(2, states.Count);
        Assert.Equal(2, states.Single(s => s.EntityId == "phone").Payload.GetProperty("position").GetInt32());
        Assert.Equal(5, states.Single(s => s.EntityId == "laptop").Payload.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task Compaction_touches_nothing_but_queue_state()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[]
            {
                Other("e1", SyncEntities.PlayEvents),
                Queue("q1", "phone", 1),
                Other("p1", SyncEntities.Playlists),
                Queue("q2", "phone", 2)
            },
            receivedAt: 1000,
            default);

        await _repository.CompactQueueStateAsync(user, new[] { "phone" }, default);

        var counts = await _repository.CountByEntityAsync(user, default);
        Assert.Equal(1, counts[SyncEntities.QueueState]);
        Assert.Equal(1, counts[SyncEntities.PlayEvents]);
        Assert.Equal(1, counts[SyncEntities.Playlists]);
    }

    [Fact]
    public async Task The_most_recently_updated_device_is_listed_first()
    {
        // Which is how a client picks the queue to offer: the one someone was last using.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Queue("q1", "phone", 1) }, 1000, default);
        await _repository.AppendAsync(user, "laptop", new[] { Queue("q2", "laptop", 1) }, 2000, default);

        var states = await _repository.GetQueueStatesAsync(user, default);

        Assert.Equal(new[] { "laptop", "phone" }, states.Select(s => s.EntityId));
    }

    [Fact]
    public async Task One_users_queue_is_invisible_to_another()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _repository.AppendAsync(alice, "phone", new[] { Queue("q1", "phone", 1) }, 1000, default);

        Assert.Empty(await _repository.GetQueueStatesAsync(bob, default));
    }

    [Fact]
    public async Task Compacting_a_device_with_one_queue_removes_nothing()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Queue("q1", "phone", 1) }, 1000, default);

        Assert.Equal(0, await _repository.CompactQueueStateAsync(user, new[] { "phone" }, default));
        Assert.Single(await _repository.GetQueueStatesAsync(user, default));
    }

    [Fact]
    public async Task The_server_receipt_time_travels_with_the_queue()
    {
        // Handover picks the freshest queue. Judging that on the client's own clock would
        // let a device with a wrong one always claim to be the most recent.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Queue("q1", "phone", 1) }, receivedAt: 7777, default);

        var state = Assert.Single(await _repository.GetQueueStatesAsync(user, default));

        Assert.Equal(7777, state.ReceivedAt);
        Assert.Equal(101, state.CreatedAt);
    }
}
