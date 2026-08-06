using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Xunit;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Envelope validation. The payload's contents are deliberately not checked.
/// </summary>
public class OpValidatorTests
{
    private const int MaxPayload = 256 * 1024;

    private static SyncOpDto Valid() => new()
    {
        OpId = "3f1c9a2e-0b7d-4c1a-9e5f-2d8b6a4c1e70",
        Entity = SyncEntities.Playlists,
        EntityId = "playlist-1",
        Operation = SyncOperations.Upsert,
        Payload = JsonDocument.Parse("""{"id":"playlist-1","name":"Late Night"}""").RootElement.Clone(),
        CreatedAt = 1_754_500_000_000
    };

    [Fact]
    public void Accepts_a_well_formed_op()
    {
        Assert.True(OpValidator.TryValidate(Valid(), MaxPayload, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData(SyncEntities.Playlists)]
    [InlineData(SyncEntities.PlaylistItems)]
    [InlineData(SyncEntities.Folders)]
    [InlineData(SyncEntities.Likes)]
    [InlineData(SyncEntities.PlayEvents)]
    [InlineData(SyncEntities.QueueState)]
    public void Accepts_every_syncable_entity(string entity)
    {
        var op = Valid();
        op.Entity = entity;

        Assert.True(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Fact]
    public void Rejects_the_tracks_table_with_an_explanation()
    {
        // tracks is a per-device cache rebuilt from each client's own Jellyfin
        // connection. Keeping it out of the log is what keeps a full history sync small.
        var op = Valid();
        op.Entity = SyncEntities.Tracks;

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out var reason));
        Assert.Contains("per-device cache", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("albums")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_an_unknown_entity(string? entity)
    {
        var op = Valid();
        op.Entity = entity;

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("UPSERT")]
    [InlineData(null)]
    public void Rejects_an_unknown_operation(string? operation)
    {
        var op = Valid();
        op.Operation = operation;

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Fact]
    public void Accepts_a_soft_delete()
    {
        var op = Valid();
        op.Operation = SyncOperations.Delete;
        op.Payload = JsonDocument.Parse("""{"id":"playlist-1","deleted":1}""").RootElement.Clone();

        Assert.True(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_a_missing_op_id(string? opId)
    {
        var op = Valid();
        op.OpId = opId;

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Fact]
    public void Rejects_a_missing_entity_id()
    {
        var op = Valid();
        op.EntityId = "";

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void Rejects_a_payload_that_is_not_a_row(string payload)
    {
        var op = Valid();
        op.Payload = JsonDocument.Parse(payload).RootElement.Clone();

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Fact]
    public void Rejects_a_payload_over_the_limit()
    {
        var op = Valid();
        op.Payload = JsonDocument
            .Parse($$"""{"id":"p","blob":"{{new string('x', 2048)}}"}""")
            .RootElement.Clone();

        Assert.False(OpValidator.TryValidate(op, maxPayloadBytes: 1024, out var reason));
        Assert.Contains("over the", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_nonsensical_created_at(long createdAt)
    {
        var op = Valid();
        op.CreatedAt = createdAt;

        Assert.False(OpValidator.TryValidate(op, MaxPayload, out _));
    }

    [Fact]
    public void Rejects_a_null_op()
    {
        Assert.False(OpValidator.TryValidate(null, MaxPayload, out _));
    }

    [Fact]
    public void Does_not_inspect_payload_fields()
    {
        // A payload the server has no schema for must still relay: that is what lets
        // the curation store add a column without a coordinated server deploy.
        var op = Valid();
        op.Payload = JsonDocument
            .Parse("""{"totally_new_column":true,"nested":{"deep":[1,2,3]}}""")
            .RootElement.Clone();

        Assert.True(OpValidator.TryValidate(op, MaxPayload, out _));
    }
}
