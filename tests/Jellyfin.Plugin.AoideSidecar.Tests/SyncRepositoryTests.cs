using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Exercises the op log against a real SQLite file in a temp directory.
/// </summary>
public sealed class SyncRepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncRepository _repository;

    public SyncRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-sidecar-tests", Guid.NewGuid().ToString("N"));
        var database = new SyncDatabase(
            Path.Combine(_directory, "aoide-sync.db"),
            NullLogger<SyncDatabase>.Instance);
        _repository = new SyncRepository(database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyncOpDto Op(string opId, string entity = SyncEntities.Playlists, string? payload = null) =>
        new()
        {
            OpId = opId,
            Entity = entity,
            EntityId = "entity-" + opId,
            Operation = SyncOperations.Upsert,
            Payload = JsonDocument.Parse(payload ?? $$"""{"id":"{{opId}}","name":"Late Night"}""").RootElement.Clone(),
            CreatedAt = 1_754_500_000_000
        };

    [Fact]
    public async Task Append_then_pull_returns_ops_in_sequence_order()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Op("a"), Op("b"), Op("c") }, 1, default);

        var page = await _repository.ReadAsync(user, since: 0, limit: 10, default);

        Assert.Equal(new[] { "a", "b", "c" }, page.Ops.Select(o => o.OpId));
        Assert.Equal(page.Ops.Select(o => o.Seq).OrderBy(s => s), page.Ops.Select(o => o.Seq));
        Assert.False(page.HasMore);
        Assert.Equal(page.Ops[^1].Seq, page.Cursor);
    }

    [Fact]
    public async Task Re_pushing_the_same_ops_is_a_no_op()
    {
        // The property that makes retry-after-timeout safe: a client that never saw the
        // response sends the batch again, and the log must not grow a second copy.
        var user = Guid.NewGuid();
        var batch = new[] { Op("a"), Op("b") };

        var first = await _repository.AppendAsync(user, "phone", batch, 1, default);
        var second = await _repository.AppendAsync(user, "phone", batch, 2, default);

        Assert.Equal(first, second);

        var page = await _repository.ReadAsync(user, since: 0, limit: 10, default);
        Assert.Equal(2, page.Ops.Count);
    }

    [Fact]
    public async Task Op_ids_do_not_collide_across_users()
    {
        // Op ids are client-generated, so a shared namespace would let one account
        // silently void another's op by pushing the same id first.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _repository.AppendAsync(alice, "phone", new[] { Op("shared") }, 1, default);
        await _repository.AppendAsync(bob, "laptop", new[] { Op("shared") }, 2, default);

        var alicePage = await _repository.ReadAsync(alice, since: 0, limit: 10, default);
        var bobPage = await _repository.ReadAsync(bob, since: 0, limit: 10, default);

        Assert.Single(alicePage.Ops);
        Assert.Single(bobPage.Ops);
        Assert.Equal("phone", alicePage.Ops[0].DeviceId);
        Assert.Equal("laptop", bobPage.Ops[0].DeviceId);
    }

    [Fact]
    public async Task Pull_is_scoped_to_the_calling_user()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _repository.AppendAsync(alice, "phone", new[] { Op("a1"), Op("a2") }, 1, default);
        await _repository.AppendAsync(bob, "laptop", new[] { Op("b1") }, 2, default);

        var page = await _repository.ReadAsync(alice, since: 0, limit: 10, default);

        Assert.Equal(new[] { "a1", "a2" }, page.Ops.Select(o => o.OpId));
    }

    [Fact]
    public async Task Paging_walks_the_log_without_gaps_or_repeats()
    {
        var user = Guid.NewGuid();
        var ops = Enumerable.Range(0, 25).Select(i => Op("op" + i)).ToArray();
        await _repository.AppendAsync(user, "phone", ops, 1, default);

        var seen = new List<string>();
        long cursor = 0;
        bool hasMore;

        do
        {
            var page = await _repository.ReadAsync(user, cursor, limit: 10, default);
            seen.AddRange(page.Ops.Select(o => o.OpId!));
            cursor = page.Cursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        Assert.Equal(ops.Select(o => o.OpId), seen);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task HasMore_is_exact_when_the_page_lands_on_the_boundary()
    {
        // Exactly `limit` rows remain: reporting hasMore here would send the client
        // back for a page it turns out is empty.
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Op("a"), Op("b") }, 1, default);

        var page = await _repository.ReadAsync(user, since: 0, limit: 2, default);

        Assert.Equal(2, page.Ops.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Empty_page_holds_the_cursor_where_it_was()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Op("a") }, 1, default);
        var first = await _repository.ReadAsync(user, since: 0, limit: 10, default);

        var second = await _repository.ReadAsync(user, first.Cursor, limit: 10, default);

        Assert.Empty(second.Ops);
        Assert.Equal(first.Cursor, second.Cursor);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task Payload_round_trips_verbatim()
    {
        // The sidecar never interprets a payload, so the client schema can change
        // without a server deploy. Nested shapes must survive untouched.
        var user = Guid.NewGuid();
        const string Payload = """
            {"id":"p1","name":"Jazz","smart_rules":{"match":"all","rules":[{"field":"genre","op":"is","value":"Jazz"}]},"deleted":0}
            """;

        await _repository.AppendAsync(user, "phone", new[] { Op("a", payload: Payload) }, 1, default);
        var page = await _repository.ReadAsync(user, since: 0, limit: 10, default);

        using var expected = JsonDocument.Parse(Payload);
        Assert.Equal(
            JsonSerializer.Serialize(expected.RootElement),
            JsonSerializer.Serialize(page.Ops[0].Payload));
    }

    [Fact]
    public async Task Server_receipt_time_is_recorded_alongside_the_client_clock()
    {
        // A device with a badly wrong clock must not be able to win every field-level
        // conflict forever, so the server stamps its own receipt time.
        var user = Guid.NewGuid();
        var op = Op("a");
        op.CreatedAt = 32_503_680_000_000; // year 3000, per a broken client clock

        await _repository.AppendAsync(user, "phone", new[] { op }, receivedAt: 1_754_500_000_000, default);
        var page = await _repository.ReadAsync(user, since: 0, limit: 10, default);

        Assert.Equal(32_503_680_000_000, page.Ops[0].CreatedAt);
        Assert.Equal(1_754_500_000_000, page.Ops[0].ReceivedAt);
    }

    [Fact]
    public async Task Cursor_is_per_user_and_starts_at_zero()
    {
        var user = Guid.NewGuid();
        Assert.Equal(0, await _repository.GetCursorAsync(user, default));

        await _repository.AppendAsync(user, "phone", new[] { Op("a") }, 1, default);

        Assert.True(await _repository.GetCursorAsync(user, default) > 0);
        Assert.Equal(0, await _repository.GetCursorAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Empty_push_reports_the_current_head()
    {
        var user = Guid.NewGuid();
        await _repository.AppendAsync(user, "phone", new[] { Op("a") }, 1, default);
        var head = await _repository.GetCursorAsync(user, default);

        var cursor = await _repository.AppendAsync(user, "phone", Array.Empty<SyncOpDto>(), 2, default);

        Assert.Equal(head, cursor);
    }

    [Fact]
    public async Task Concurrent_pushes_produce_a_gapless_view_for_readers()
    {
        // The load-bearing assumption behind a monotonic cursor: because SQLite
        // serialises writers, a reader that sees sequence N already sees everything
        // below it. A cursor can therefore never skip an op that was still in flight.
        var user = Guid.NewGuid();

        var pushes = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            _repository.AppendAsync(user, "device" + i, new[] { Op("op" + i) }, i + 1, default)));
        await Task.WhenAll(pushes);

        var page = await _repository.ReadAsync(user, since: 0, limit: 100, default);

        Assert.Equal(8, page.Ops.Count);
        var sequences = page.Ops.Select(o => o.Seq).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Equal(8, sequences.Distinct().Count());
    }
}
