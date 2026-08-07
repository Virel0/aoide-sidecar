using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Export;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Deciding which stored artwork nothing points at any more.
/// </summary>
public class ArtworkSweepTests
{
    private const long Now = 1_800_000_000_000;
    private const long Day = 86_400_000;

    private static StoredImageInfo Blob(string hash, int ageDays, long size = 1000) =>
        new(hash, size, Now - (ageDays * Day));

    private static SyncOpDto Playlist(string id, string? imageHash, bool deleted = false)
    {
        var image = imageHash is null ? "null" : $"\"{imageHash}\"";
        return new SyncOpDto
        {
            OpId = id,
            Entity = SyncEntities.Playlists,
            EntityId = id,
            Operation = SyncOperations.Upsert,
            Payload = JsonDocument
                .Parse($$"""{"id":"{{id}}","name":"n","deleted":{{(deleted ? "true" : "false")}},"updatedAt":1,"imageHash":{{image}}}""")
                .RootElement.Clone(),
            CreatedAt = 1,
            Seq = 1
        };
    }

    private static HashSet<string> Referenced(params SyncOpDto[] ops) =>
        ArtworkSweep.ReferencedHashes(PlaylistProjection.Build(ops));

    [Fact]
    public void Artwork_a_live_playlist_uses_is_never_an_orphan()
    {
        var report = ArtworkSweep.Build(
            new[] { Blob("aaa", 400) },
            Referenced(Playlist("p1", "aaa")),
            Now,
            graceDays: 30);

        Assert.Empty(report.Orphans);
        Assert.Equal(1, report.TotalBlobs);
    }

    [Fact]
    public void Artwork_only_a_deleted_playlist_used_is_an_orphan()
    {
        var report = ArtworkSweep.Build(
            new[] { Blob("aaa", 400) },
            Referenced(Playlist("p1", "aaa", deleted: true)),
            Now,
            graceDays: 30);

        Assert.Equal("aaa", Assert.Single(report.Orphans).ImageHash);
    }

    [Fact]
    public void Artwork_shared_by_two_playlists_survives_one_being_deleted()
    {
        // Content addressing makes sharing normal: identical images collapse to one
        // blob. A sweep that reasoned per playlist would delete a cover still on screen.
        var report = ArtworkSweep.Build(
            new[] { Blob("shared", 400) },
            Referenced(Playlist("p1", "shared", deleted: true), Playlist("p2", "shared")),
            Now,
            graceDays: 30);

        Assert.Empty(report.Orphans);
    }

    [Fact]
    public void An_orphan_inside_the_grace_period_is_reported_but_not_reclaimable()
    {
        // Reported because this is also how you confirm an upload arrived; not
        // reclaimable because its playlist row may still be queued on some device.
        var report = ArtworkSweep.Build(new[] { Blob("fresh", 1) }, Referenced(), Now, graceDays: 30);

        var orphan = Assert.Single(report.Orphans);
        Assert.False(orphan.Reclaimable);
        Assert.Equal(1, orphan.AgeDays);
    }

    [Fact]
    public void An_orphan_past_the_grace_period_is_reclaimable()
    {
        var report = ArtworkSweep.Build(new[] { Blob("old", 31) }, Referenced(), Now, graceDays: 30);

        Assert.True(Assert.Single(report.Orphans).Reclaimable);
    }

    [Fact]
    public void The_grace_boundary_is_inclusive()
    {
        var report = ArtworkSweep.Build(new[] { Blob("edge", 30) }, Referenced(), Now, graceDays: 30);

        Assert.True(Assert.Single(report.Orphans).Reclaimable);
    }

    [Fact]
    public void A_clock_that_moved_backwards_never_makes_a_blob_reclaimable()
    {
        // Age would go negative, and a negative number is not "older than" anything.
        var report = ArtworkSweep.Build(
            new[] { new StoredImageInfo("future", 1000, Now + (10 * Day)) },
            Referenced(),
            Now,
            graceDays: 30);

        var orphan = Assert.Single(report.Orphans);
        Assert.Equal(0, orphan.AgeDays);
        Assert.False(orphan.Reclaimable);
    }

    [Fact]
    public void Hashes_are_matched_regardless_of_case()
    {
        var report = ArtworkSweep.Build(
            new[] { Blob("ABCDEF", 400) },
            Referenced(Playlist("p1", "abcdef")),
            Now,
            graceDays: 30);

        Assert.Empty(report.Orphans);
    }

    [Fact]
    public void A_playlist_with_no_artwork_references_nothing()
    {
        // Mosaic covers are deliberately hash-less; they must not keep anything alive.
        Assert.Empty(Referenced(Playlist("p1", null)));
    }

    [Fact]
    public void Totals_cover_everything_and_orphan_bytes_only_the_unreferenced()
    {
        var report = ArtworkSweep.Build(
            new[] { Blob("live", 400, 500), Blob("dead", 400, 300) },
            Referenced(Playlist("p1", "live")),
            Now,
            graceDays: 30);

        Assert.Equal(2, report.TotalBlobs);
        Assert.Equal(800, report.TotalBytes);
        Assert.Equal(300, report.OrphanBytes);
        Assert.Equal(30, report.GraceDays);
    }

    [Fact]
    public void Orphans_are_listed_oldest_first()
    {
        var report = ArtworkSweep.Build(
            new[] { Blob("young", 5), Blob("ancient", 900), Blob("middle", 60) },
            Referenced(),
            Now,
            graceDays: 30);

        Assert.Equal(new[] { "ancient", "middle", "young" }, report.Orphans.Select(o => o.ImageHash));
    }

    [Fact]
    public async Task Reclaiming_one_users_orphan_leaves_another_users_identical_image_alone()
    {
        // Two accounts storing the same picture store the same hash. Without a user
        // predicate on the delete, sweeping one would take the other's live cover.
        var directory = Path.Combine(Path.GetTempPath(), "aoide-sweep-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var images = new PlaylistImageRepository(
                new SyncDatabase(Path.Combine(directory, "s.db"), NullLogger<SyncDatabase>.Instance));

            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();
            var bytes = new byte[] { 1, 2, 3 };
            const string Hash = "deadbeef";

            await images.StoreAsync(alice, Hash, "image/png", bytes, 1, default);
            await images.StoreAsync(bob, Hash, "image/png", bytes, 1, default);

            var deleted = await images.DeleteAsync(alice, new[] { Hash }, default);

            Assert.Equal(1, deleted);
            Assert.False(await images.ExistsAsync(alice, Hash, default));
            Assert.True(await images.ExistsAsync(bob, Hash, default));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Listing_reports_size_and_age_without_loading_bytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aoide-sweep-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var images = new PlaylistImageRepository(
                new SyncDatabase(Path.Combine(directory, "s.db"), NullLogger<SyncDatabase>.Instance));

            var user = Guid.NewGuid();
            await images.StoreAsync(user, "aaa", "image/png", new byte[64], 5000, default);

            var listed = Assert.Single(await images.ListAsync(user, default));

            Assert.Equal("aaa", listed.ImageHash);
            Assert.Equal(64, listed.SizeBytes);
            Assert.Equal(5000, listed.CreatedAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
