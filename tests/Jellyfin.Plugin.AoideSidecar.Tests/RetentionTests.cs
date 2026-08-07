using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Device pull cursors, and trimming play history against a real database.
/// </summary>
public sealed class RetentionTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncRepository _repository;

    public RetentionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-retention-tests", Guid.NewGuid().ToString("N"));
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

    private static SyncOpDto Op(string opId, string entity) => new()
    {
        OpId = opId,
        Entity = entity,
        EntityId = "e-" + opId,
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument.Parse($$"""{"id":"{{opId}}"}""").RootElement.Clone(),
        CreatedAt = 1
    };

    [Fact]
    public async Task A_device_cursor_only_ever_moves_forward()
    {
        // Replaying from an earlier cursor is legitimate. It must not be read as the
        // device having seen less than it already has, or retention would think history
        // it already collected is still owed to it.
        var user = Guid.NewGuid();
        await _repository.RecordDeviceCursorAsync(user, "phone", 100, 1, default);
        await _repository.RecordDeviceCursorAsync(user, "phone", 20, 2, default);

        var cursor = Assert.Single(await _repository.GetDeviceCursorsAsync(user, default));
        Assert.Equal(100, cursor.Cursor);
        Assert.Equal(2, cursor.UpdatedAt);
    }

    [Fact]
    public async Task Device_cursors_are_tracked_separately_per_device_and_user()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _repository.RecordDeviceCursorAsync(alice, "phone", 10, 1, default);
        await _repository.RecordDeviceCursorAsync(alice, "laptop", 5, 1, default);
        await _repository.RecordDeviceCursorAsync(bob, "phone", 99, 1, default);

        Assert.Equal(2, (await _repository.GetDeviceCursorsAsync(alice, default)).Count);
        Assert.Equal(99, Assert.Single(await _repository.GetDeviceCursorsAsync(bob, default)).Cursor);
    }

    [Fact]
    public async Task Pruning_never_touches_anything_but_play_history()
    {
        // Every other entity is current state, not history: a playlist row is the only
        // description of that playlist, so deleting it would not trim anything, it would
        // remove the playlist for any device syncing from scratch.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[]
            {
                Op("e1", SyncEntities.PlayEvents),
                Op("p1", SyncEntities.Playlists),
                Op("i1", SyncEntities.PlaylistItems),
                Op("l1", SyncEntities.Likes),
                Op("f1", SyncEntities.Folders),
                Op("q1", SyncEntities.QueueState)
            },
            receivedAt: 1000,
            default);

        var pruned = await _repository.PrunablePlayEventsAsync(user, long.MaxValue, 5000, delete: true, default);

        Assert.Equal(1, pruned);

        var counts = await _repository.CountByEntityAsync(user, default);
        Assert.False(counts.ContainsKey(SyncEntities.PlayEvents));
        Assert.Equal(1, counts[SyncEntities.Playlists]);
        Assert.Equal(1, counts[SyncEntities.Likes]);
        Assert.Equal(1, counts[SyncEntities.QueueState]);
    }

    [Fact]
    public async Task History_no_device_has_pulled_yet_is_not_prunable()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user, "phone", new[] { Op("e1", SyncEntities.PlayEvents), Op("e2", SyncEntities.Likes) }, 1000, default);

        // Safe cursor 0 stands for "nobody has reported reading anything".
        var count = await _repository.PrunablePlayEventsAsync(user, 0, 5000, delete: false, default);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task History_beyond_the_safe_cursor_is_left_alone()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[] { Op("e1", SyncEntities.PlayEvents), Op("e2", SyncEntities.PlayEvents) },
            receivedAt: 1000,
            default);

        // Only the first op has been read by everyone.
        var pruned = await _repository.PrunablePlayEventsAsync(user, 1, 5000, delete: true, default);

        Assert.Equal(1, pruned);
        Assert.Equal(1, (await _repository.CountByEntityAsync(user, default))[SyncEntities.PlayEvents]);
    }

    [Fact]
    public async Task History_inside_the_retention_window_is_left_alone()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user, "phone", new[] { Op("e1", SyncEntities.PlayEvents), Op("e2", SyncEntities.Likes) }, 9000, default);

        // Received after the cutoff, so still within retention however far it has synced.
        var count = await _repository.PrunablePlayEventsAsync(user, long.MaxValue, 5000, delete: false, default);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Counting_does_not_delete()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user, "phone", new[] { Op("e1", SyncEntities.PlayEvents), Op("e2", SyncEntities.Likes) }, 1000, default);

        var counted = await _repository.PrunablePlayEventsAsync(user, long.MaxValue, 5000, delete: false, default);

        Assert.Equal(1, counted);
        Assert.Equal(1, (await _repository.CountByEntityAsync(user, default))[SyncEntities.PlayEvents]);
    }

    [Fact]
    public async Task Pruning_one_user_leaves_another_users_history_intact()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        // A later op of another kind, so the play event is not the head sequence —
        // the newest op is always retained to keep the head from moving backwards.
        await _repository.AppendAsync(
            alice, "phone", new[] { Op("a1", SyncEntities.PlayEvents), Op("a2", SyncEntities.Likes) }, 1000, default);
        await _repository.AppendAsync(bob, "phone", new[] { Op("b1", SyncEntities.PlayEvents) }, 1000, default);

        await _repository.PrunablePlayEventsAsync(alice, long.MaxValue, 5000, delete: true, default);

        Assert.False((await _repository.CountByEntityAsync(alice, default)).ContainsKey(SyncEntities.PlayEvents));
        Assert.Equal(1, (await _repository.CountByEntityAsync(bob, default))[SyncEntities.PlayEvents]);
    }

    [Fact]
    public async Task Pruning_leaves_the_cursor_where_it_was()
    {
        // Sequences are never reused, so trimming old rows must not make a puller
        // revisit ground it has already covered.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(
            user,
            "phone",
            new[] { Op("e1", SyncEntities.PlayEvents), Op("e2", SyncEntities.PlayEvents) },
            receivedAt: 1000,
            default);

        var before = await _repository.GetCursorAsync(user, default);
        await _repository.PrunablePlayEventsAsync(user, long.MaxValue, 5000, delete: true, default);

        Assert.Equal(before, await _repository.GetCursorAsync(user, default));
    }

    [Fact]
    public async Task Users_are_listed_from_the_log()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _repository.AppendAsync(alice, "phone", new[] { Op("a1", SyncEntities.Likes) }, 1, default);
        await _repository.AppendAsync(bob, "phone", new[] { Op("b1", SyncEntities.Likes) }, 1, default);

        var users = await _repository.ListUsersAsync(default);

        Assert.Contains(alice, users);
        Assert.Contains(bob, users);
    }
}
