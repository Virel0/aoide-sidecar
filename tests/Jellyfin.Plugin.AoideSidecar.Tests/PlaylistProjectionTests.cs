using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Export;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Collapsing the op log into current playlist state.
/// </summary>
public class PlaylistProjectionTests
{
    private static SyncOpDto Op(string entity, string entityId, long seq, string payload, string operation = SyncOperations.Upsert) =>
        new()
        {
            OpId = $"{entity}-{entityId}-{seq}",
            Entity = entity,
            EntityId = entityId,
            Operation = operation,
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
            CreatedAt = 1,
            Seq = seq
        };

    private static SyncOpDto Playlist(string id, long seq, string name, long updatedAt, bool smart = false, bool deleted = false) =>
        Op(SyncEntities.Playlists, id, seq,
            $$"""{"id":"{{id}}","name":"{{name}}","is_smart":{{(smart ? 1 : 0)}},"deleted":{{(deleted ? 1 : 0)}},"updated_at":{{updatedAt}}}""");

    private static SyncOpDto Item(string id, long seq, string playlistId, string trackId, string position, bool deleted = false) =>
        Op(SyncEntities.PlaylistItems, id, seq,
            $$"""{"id":"{{id}}","playlist_id":"{{playlistId}}","jellyfin_id":"{{trackId}}","position":"{{position}}","deleted":{{(deleted ? 1 : 0)}},"updated_at":{{seq}}}""");

    [Fact]
    public void Orders_members_by_fractional_index_not_arrival()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Late Night", 100),
            Item("i1", 2, "p1", "trackC", "a2"),
            Item("i2", 3, "p1", "trackA", "a0"),
            Item("i3", 4, "p1", "trackB", "a1")
        });

        Assert.Equal(new[] { "trackA", "trackB", "trackC" }, Assert.Single(result).TrackIds);
    }

    [Fact]
    public void Later_edit_wins_by_updated_at_not_sequence()
    {
        // A device editing offline lands a higher sequence than an edit made after it.
        // Ordering by sequence alone would let the stale name win.
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Newer", 500),
            Playlist("p1", 2, "StaleOfflineEdit", 100)
        });

        Assert.Equal("Newer", Assert.Single(result).Name);
    }

    [Fact]
    public void Sequence_breaks_a_tie_on_equal_timestamps()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "First", 100),
            Playlist("p1", 2, "Second", 100)
        });

        Assert.Equal("Second", Assert.Single(result).Name);
    }

    [Fact]
    public void Soft_deleted_members_drop_out()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Late Night", 100),
            Item("i1", 2, "p1", "trackA", "a0"),
            Item("i2", 3, "p1", "trackB", "a1"),
            Item("i2", 4, "p1", "trackB", "a1", deleted: true)
        });

        Assert.Equal(new[] { "trackA" }, Assert.Single(result).TrackIds);
    }

    [Fact]
    public void A_delete_operation_marks_the_playlist_even_without_a_deleted_field()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Late Night", 100),
            Op(SyncEntities.Playlists, "p1", 2, """{"id":"p1","updated_at":200}""", SyncOperations.Delete)
        });

        Assert.True(Assert.Single(result).Deleted);
    }

    [Fact]
    public void Deleted_playlists_are_still_reported_so_exports_can_be_cleaned_up()
    {
        var result = PlaylistProjection.Build(new[] { Playlist("p1", 1, "Gone", 100, deleted: true) });

        Assert.True(Assert.Single(result).Deleted);
    }

    [Fact]
    public void Smart_playlists_are_flagged()
    {
        var result = PlaylistProjection.Build(new[] { Playlist("p1", 1, "Jazz", 100, smart: true) });

        Assert.True(Assert.Single(result).IsSmart);
    }

    [Fact]
    public void Members_are_grouped_by_their_own_playlist()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "One", 100),
            Playlist("p2", 2, "Two", 100),
            Item("i1", 3, "p1", "trackA", "a0"),
            Item("i2", 4, "p2", "trackB", "a0")
        });

        Assert.Equal(new[] { "trackA" }, result.Single(p => p.Id == "p1").TrackIds);
        Assert.Equal(new[] { "trackB" }, result.Single(p => p.Id == "p2").TrackIds);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public void Booleans_are_accepted_as_numbers_or_literals(string raw)
    {
        // Clients have written these both ways; both mean the same thing.
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1, $$"""{"id":"p1","name":"X","is_smart":{{raw}},"updated_at":1}""")
        });

        Assert.True(Assert.Single(result).IsSmart);
    }

    [Fact]
    public void Rows_missing_expected_fields_are_skipped_not_fatal()
    {
        // The server does not own the payload schema, so an unrecognised row must not
        // take the whole export down with it.
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Late Night", 100),
            Op(SyncEntities.PlaylistItems, "bad", 2, """{"nothing":"useful"}"""),
            Item("i1", 3, "p1", "trackA", "a0")
        });

        Assert.Equal(new[] { "trackA" }, Assert.Single(result).TrackIds);
    }

    [Theory]
    [InlineData("sourceJellyfinId")]
    [InlineData("source_jellyfin_id")]
    public void Source_playlist_id_is_read_in_either_spelling(string field)
    {
        // The store's columns are snake_case but this one arrived camelCase. Missing it
        // would silently cost an exact match and fall back to guessing by name.
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1,
                $$"""{"id":"p1","name":"X","updated_at":1,"{{field}}":"abc123"}""")
        });

        Assert.Equal("abc123", Assert.Single(result).SourceJellyfinId);
    }

    [Fact]
    public void Artwork_hash_is_read()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1,
                """{"id":"p1","name":"X","updated_at":1,"image_hash":"8581e780"}""")
        });

        Assert.Equal("8581e780", Assert.Single(result).ImageHash);
    }

    [Fact]
    public void A_playlist_without_a_source_or_artwork_reports_neither()
    {
        var result = PlaylistProjection.Build(new[] { Playlist("p1", 1, "Fresh", 100) });

        var playlist = Assert.Single(result);
        Assert.Null(playlist.SourceJellyfinId);
        Assert.Null(playlist.ImageHash);
    }

    [Fact]
    public void Reads_the_camelCase_payloads_the_client_actually_sends()
    {
        // Taken from real ops on a live server. The design doc names these columns in
        // snake_case; the client serialises camelCase. Reading only snake_case fails
        // silently and catastrophically — playlistId groups nothing, so every exported
        // playlist comes out empty, and isSmart reads false, so smart playlists get
        // pushed to Jellyfin when they must never be.
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1, """{"id":"p1","name":"Late Night","isSmart":false,"deleted":false,"updatedAt":500,"originDevice":"dev","sourceJellyfinId":"aa71a764","imageHash":"8581e780"}"""),
            Op(SyncEntities.Playlists, "p2", 2, """{"id":"p2","name":"Jazz","isSmart":true,"deleted":false,"updatedAt":500}"""),
            Op(SyncEntities.PlaylistItems, "i1", 3, """{"id":"i1","playlistId":"p1","jellyfinId":"aa71a76469e986510b6952fa2bd5b328","position":"a1","deleted":false,"updatedAt":500}"""),
            Op(SyncEntities.PlaylistItems, "i2", 4, """{"id":"i2","playlistId":"p1","jellyfinId":"83297a62041d3e6a5942db68863f4f40","position":"a0","deleted":false,"updatedAt":500}""")
        });

        var normal = result.Single(p => p.Id == "p1");
        Assert.Equal("Late Night", normal.Name);
        Assert.False(normal.IsSmart);
        Assert.Equal("aa71a764", normal.SourceJellyfinId);
        Assert.Equal("8581e780", normal.ImageHash);
        Assert.Equal(
            new[] { "83297a62041d3e6a5942db68863f4f40", "aa71a76469e986510b6952fa2bd5b328" },
            normal.TrackIds);

        Assert.True(result.Single(p => p.Id == "p2").IsSmart);
    }

    [Fact]
    public void A_camelCase_soft_delete_is_seen()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1, """{"id":"p1","name":"Gone","deleted":true,"updatedAt":9}""")
        });

        Assert.True(Assert.Single(result).Deleted);
    }

    [Fact]
    public void Later_camelCase_edit_still_wins_on_updatedAt()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Op(SyncEntities.Playlists, "p1", 1, """{"id":"p1","name":"Newer","updatedAt":500}"""),
            Op(SyncEntities.Playlists, "p1", 2, """{"id":"p1","name":"StaleOffline","updatedAt":100}""")
        });

        Assert.Equal("Newer", Assert.Single(result).Name);
    }

    [Fact]
    public void Unrelated_entities_are_ignored()
    {
        var result = PlaylistProjection.Build(new[]
        {
            Playlist("p1", 1, "Late Night", 100),
            Op(SyncEntities.PlayEvents, "e1", 2, """{"id":"e1","ms_played":1000}"""),
            Op(SyncEntities.Likes, "l1", 3, """{"id":"l1","liked":1}""")
        });

        Assert.Single(result);
    }
}
