using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Reading listens and flags back out of the op log, in the shape the clients write them.
/// </summary>
public sealed class ListeningRepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncRepository _sync;
    private readonly ListeningRepository _listening;
    private readonly Guid _user = Guid.NewGuid();

    public ListeningRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-listening-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var database = new SyncDatabase(Path.Combine(_directory, "s.db"), NullLogger<SyncDatabase>.Instance);
        _sync = new SyncRepository(database);
        _listening = new ListeningRepository(database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The payload as the phone writes it: camelCase, booleans as booleans, ids as
    /// Jellyfin hands them out.
    /// </summary>
    [Fact]
    public async Task Play_events_are_read_as_the_clients_write_them()
    {
        await _sync.AppendAsync(_user, "phone", new[]
        {
            Op("e1", SyncEntities.PlayEvents, """
                {"id":"e1","jellyfinId":"D6A1123256460A0FEAB3ED2EC8264644","contentKey":"k","startedAt":1760000000000,
                 "endedAt":1760000200000,"msPlayed":200000,"completed":true,"skipped":false,"source":"album","originDevice":"phone"}
                """),
            Op("e2", SyncEntities.PlayEvents, """
                {"id":"e2","jellyfinId":"71efab3aaef5ee2d00d67cf38d84e38e","contentKey":"k","startedAt":1760000300000,
                 "endedAt":1760000304000,"msPlayed":4000,"completed":false,"skipped":true,"originDevice":"desktop"}
                """),
            Op("e3", SyncEntities.PlayEvents, """
                {"id":"e3","jellyfinId":"71efab3aaef5ee2d00d67cf38d84e38e","contentKey":"k","startedAt":1760000400000,
                 "msPlayed":0,"completed":false,"skipped":false,"originDevice":"desktop"}
                """),
        }, 1, default);

        var events = (await _listening.PlayEventsAsync(Key(), default)).OrderBy(e => e.StartedAt).ToList();

        Assert.Equal(3, events.Count);
        Assert.Equal("d6a1123256460a0feab3ed2ec8264644", events[0].JellyfinId);
        Assert.True(events[0].Completed);
        Assert.Equal(1760000200000, events[0].EndedAt);
        Assert.True(events[1].Skipped);
        Assert.Null(events[2].EndedAt);
    }

    [Fact]
    public async Task A_re_pushed_event_is_one_listen_and_a_broken_payload_is_skipped()
    {
        await _sync.AppendAsync(_user, "phone", new[]
        {
            Op("e1", SyncEntities.PlayEvents, """{"id":"e1","jellyfinId":"aaaa","startedAt":1,"completed":true}"""),
            Op("e1-again", SyncEntities.PlayEvents, """{"id":"e1","jellyfinId":"aaaa","startedAt":1,"completed":true}""", entityId: "entity-e1"),
            Op("broken", SyncEntities.PlayEvents, """{"id":"x","startedAt":"soon"}"""),
        }, 1, default);

        var events = await _listening.PlayEventsAsync(Key(), default);

        Assert.Single(events);
    }

    [Fact]
    public async Task Another_users_listens_are_not_mine()
    {
        await _sync.AppendAsync(Guid.NewGuid(), "phone", new[]
        {
            Op("e1", SyncEntities.PlayEvents, """{"id":"e1","jellyfinId":"aaaa","startedAt":1,"completed":true}"""),
        }, 1, default);

        Assert.Empty(await _listening.PlayEventsAsync(Key(), default));
    }

    /// <summary>
    /// Flags are merged rows: the latest op for a track decides, so a flag set and later
    /// cleared is not a flag, and a deleted row is not one either.
    /// </summary>
    [Fact]
    public async Task The_latest_flag_op_per_track_wins()
    {
        await _sync.AppendAsync(_user, "phone", new[]
        {
            Op("f1", SyncEntities.TrackFlags, """{"id":"f1","jellyfinId":"aaaa","notInterested":true,"dontCount":false,"updatedAt":1,"deleted":false}"""),
            Op("f2", SyncEntities.TrackFlags, """{"id":"f2","jellyfinId":"bbbb","notInterested":true,"dontCount":false,"updatedAt":1,"deleted":false}"""),
            Op("f2-cleared", SyncEntities.TrackFlags, """{"id":"f2","jellyfinId":"bbbb","notInterested":false,"dontCount":false,"updatedAt":2,"deleted":false}"""),
            Op("f3", SyncEntities.TrackFlags, """{"id":"f3","jellyfinId":"cccc","notInterested":true,"dontCount":false,"updatedAt":1,"deleted":true}"""),
            Op("f4", SyncEntities.TrackFlags, """{"id":"f4","jellyfinId":"dddd","notInterested":false,"dontCount":true,"updatedAt":1,"deleted":false}"""),
        }, 1, default);

        var flagged = await _listening.NotInterestedAsync(Key(), default);

        Assert.Equal(new[] { "aaaa" }, flagged.OrderBy(f => f));
    }

    private string Key() => _user.ToString("N", CultureInfo.InvariantCulture);

    private static SyncOpDto Op(string opId, string entity, string payload, string? entityId = null) =>
        new()
        {
            OpId = opId,
            Entity = entity,
            EntityId = entityId ?? "entity-" + opId,
            Operation = SyncOperations.Upsert,
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
            CreatedAt = 1_754_500_000_000
        };
}
