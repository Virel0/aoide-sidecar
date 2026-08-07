using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Playlist ownership, edit permission, and what a collaborator can see.
/// </summary>
public sealed class SharingTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncRepository _repository;
    private readonly SharingRepository _sharing;

    public SharingTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aoide-sharing-tests", Guid.NewGuid().ToString("N"));
        var database = new SyncDatabase(
            Path.Combine(_directory, "aoide-sync.db"), NullLogger<SyncDatabase>.Instance);
        _repository = new SyncRepository(database);
        _sharing = new SharingRepository(database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyncOpDto PlaylistOp(string opId, string playlistId) => new()
    {
        OpId = opId,
        Entity = SyncEntities.Playlists,
        EntityId = playlistId,
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument.Parse($$"""{"id":"{{playlistId}}","name":"Shared"}""").RootElement.Clone(),
        CreatedAt = 1
    };

    private static SyncOpDto ItemOp(string opId, string playlistId, string field = "playlistId") => new()
    {
        OpId = opId,
        Entity = SyncEntities.PlaylistItems,
        EntityId = opId,
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument
            .Parse($$"""{"id":"{{opId}}","{{field}}":"{{playlistId}}","jellyfinId":"t1","position":"a0"}""")
            .RootElement.Clone(),
        CreatedAt = 1
    };

    [Theory]
    [InlineData("playlistId")]
    [InlineData("playlist_id")]
    public void A_members_playlist_is_read_in_either_spelling(string field)
    {
        Assert.Equal("p1", SharingRepository.PlaylistIdOf(ItemOp("i1", "p1", field)));
    }

    [Fact]
    public void A_playlist_op_is_keyed_by_its_own_id_without_parsing()
    {
        Assert.Equal("p1", SharingRepository.PlaylistIdOf(PlaylistOp("o1", "p1")));
    }

    [Fact]
    public void Personal_entities_belong_to_no_playlist()
    {
        var op = PlaylistOp("o1", "p1");
        op.Entity = SyncEntities.PlayEvents;

        Assert.Null(SharingRepository.PlaylistIdOf(op));
    }

    [Fact]
    public async Task The_first_writer_becomes_the_owner_and_a_later_one_does_not()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, bob, 2, default);

        Assert.Equal(alice, await _sharing.GetOwnerAsync("p1", default));
    }

    [Fact]
    public async Task An_unclaimed_playlist_is_writable_by_anyone()
    {
        // Otherwise nobody could ever create one.
        var writable = await _sharing.GetWritableAsync(new[] { "brand-new" }, Guid.NewGuid(), default);

        Assert.Contains("brand-new", writable);
    }

    [Fact]
    public async Task Someone_elses_playlist_is_not_writable_until_shared_for_editing()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);

        Assert.DoesNotContain("p1", await _sharing.GetWritableAsync(new[] { "p1" }, bob, default));

        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);
        Assert.Contains("p1", await _sharing.GetWritableAsync(new[] { "p1" }, bob, default));
    }

    [Fact]
    public async Task A_view_only_share_does_not_grant_writing()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: false, 2, default);

        Assert.DoesNotContain("p1", await _sharing.GetWritableAsync(new[] { "p1" }, bob, default));
    }

    [Fact]
    public async Task Revoking_takes_write_access_away_again()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        Assert.True(await _sharing.RevokeAsync("p1", bob, default));

        Assert.DoesNotContain("p1", await _sharing.GetWritableAsync(new[] { "p1" }, bob, default));
    }

    [Fact]
    public async Task A_collaborator_pulls_the_owners_playlist_and_the_owner_pulls_theirs()
    {
        // The whole point: one playlist, two accounts, each seeing the other's edits
        // through the pull they already make. No second loop, no separate cursor.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _repository.AppendAsync(alice, "alice-phone", new[] { PlaylistOp("a1", "p1") }, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        await _repository.AppendAsync(bob, "bob-phone", new[] { ItemOp("b1", "p1") }, 3, default);

        var bobSees = await _repository.ReadAsync(bob, 0, 50, default);
        Assert.Equal(new[] { "a1", "b1" }, bobSees.Ops.Select(o => o.OpId));

        var aliceSees = await _repository.ReadAsync(alice, 0, 50, default);
        Assert.Equal(new[] { "a1", "b1" }, aliceSees.Ops.Select(o => o.OpId));
    }

    [Fact]
    public async Task An_op_says_which_account_wrote_it()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _repository.AppendAsync(alice, "alice-phone", new[] { PlaylistOp("a1", "p1") }, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);
        await _repository.AppendAsync(bob, "bob-phone", new[] { ItemOp("b1", "p1") }, 3, default);

        var ops = (await _repository.ReadAsync(alice, 0, 50, default)).Ops;

        Assert.Equal(alice.ToString("N"), ops.Single(o => o.OpId == "a1").AuthorUserId);
        Assert.Equal(bob.ToString("N"), ops.Single(o => o.OpId == "b1").AuthorUserId);
    }

    [Fact]
    public async Task An_unrelated_user_sees_none_of_it()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await _repository.AppendAsync(alice, "alice-phone", new[] { PlaylistOp("a1", "p1") }, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        Assert.Empty((await _repository.ReadAsync(stranger, 0, 50, default)).Ops);
    }

    [Fact]
    public async Task Revoking_stops_the_collaborator_seeing_further_changes()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _repository.AppendAsync(alice, "alice-phone", new[] { PlaylistOp("a1", "p1") }, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        Assert.Single((await _repository.ReadAsync(bob, 0, 50, default)).Ops);

        await _sharing.RevokeAsync("p1", bob, default);

        Assert.Empty((await _repository.ReadAsync(bob, 0, 50, default)).Ops);
    }

    [Fact]
    public async Task Personal_history_is_never_visible_to_a_collaborator()
    {
        // Sharing a playlist shares the playlist, not the account. Play events, likes
        // and queue state carry no playlist id, so nothing routes them across.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var play = PlaylistOp("a-play", "p1");
        play.Entity = SyncEntities.PlayEvents;
        play.EntityId = "e1";

        await _repository.AppendAsync(alice, "alice-phone", new[] { PlaylistOp("a1", "p1"), play }, 1, default);
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        var bobSees = await _repository.ReadAsync(bob, 0, 50, default);

        Assert.Equal(new[] { "a1" }, bobSees.Ops.Select(o => o.OpId));
    }

    [Fact]
    public async Task Both_sides_can_see_the_share_listed()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _sharing.ClaimOwnershipAsync(new[] { "p1" }, alice, 1, default);
        await _sharing.ShareAsync("p1", bob, canEdit: true, 2, default);

        Assert.Single(await _sharing.ListSharesAsync(alice, default));
        Assert.Single(await _sharing.ListSharesAsync(bob, default));
    }
}
