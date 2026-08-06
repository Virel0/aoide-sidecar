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
    private static readonly OpLimits Limits = new(256 * 1024, 32);

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
        Assert.True(OpValidator.TryValidate(Valid(), Limits, out var reason));
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

        Assert.True(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Rejects_the_tracks_table_with_an_explanation()
    {
        // tracks is a per-device cache rebuilt from each client's own Jellyfin
        // connection. Keeping it out of the log is what keeps a full history sync small.
        var op = Valid();
        op.Entity = SyncEntities.Tracks;

        Assert.False(OpValidator.TryValidate(op, Limits, out var reason));
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

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("UPSERT")]
    [InlineData(null)]
    public void Rejects_an_unknown_operation(string? operation)
    {
        var op = Valid();
        op.Operation = operation;

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Accepts_a_soft_delete()
    {
        var op = Valid();
        op.Operation = SyncOperations.Delete;
        op.Payload = JsonDocument.Parse("""{"id":"playlist-1","deleted":1}""").RootElement.Clone();

        Assert.True(OpValidator.TryValidate(op, Limits, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_a_missing_op_id(string? opId)
    {
        var op = Valid();
        op.OpId = opId;

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Rejects_a_missing_entity_id()
    {
        var op = Valid();
        op.EntityId = "";

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
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

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Rejects_a_payload_over_the_limit()
    {
        var op = Valid();
        op.Payload = JsonDocument
            .Parse($$"""{"id":"p","blob":"{{new string('x', 2048)}}"}""")
            .RootElement.Clone();

        Assert.False(OpValidator.TryValidate(op, new OpLimits(1024, 32), out var reason));
        Assert.Contains("over the", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_nonsensical_created_at(long createdAt)
    {
        var op = Valid();
        op.CreatedAt = createdAt;

        Assert.False(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Rejects_a_null_op()
    {
        Assert.False(OpValidator.TryValidate(null, Limits, out _));
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

        Assert.True(OpValidator.TryValidate(op, Limits, out _));
    }

    private static JsonElement Nested(int depth)
    {
        var json = new System.Text.StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            json.Append("{\"a\":");
        }

        json.Append('1');
        for (var i = 0; i < depth; i++)
        {
            json.Append('}');
        }

        // Above the parser default of 64 so the helper itself is not the constraint —
        // the point is to hand the validator something deeper than it will accept.
        var options = new JsonDocumentOptions { MaxDepth = 256 };
        return JsonDocument.Parse(json.ToString(), options).RootElement.Clone();
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("\"s\"", 0)]
    [InlineData("""{"a":1}""", 1)]
    [InlineData("""{"a":{"b":1}}""", 2)]
    [InlineData("[[1]]", 2)]
    [InlineData("""{"a":[{"b":1}]}""", 3)]
    [InlineData("""{"a":1,"b":{"c":{"d":1}}}""", 3)]
    public void MeasureDepth_counts_nesting_levels(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OpValidator.MeasureDepth(document.RootElement));
    }

    [Fact]
    public void Accepts_a_payload_at_the_depth_limit()
    {
        var op = Valid();
        op.Payload = Nested(32);

        Assert.True(OpValidator.TryValidate(op, Limits, out _));
    }

    [Fact]
    public void Rejects_a_payload_past_the_depth_limit()
    {
        // A stored payload is echoed to every device on pull, and a JSON decoder refuses
        // an over-nested document whole rather than per-element. One such row would wedge
        // inbound sync everywhere with no seam for a client to skip it, so the only place
        // this can be stopped is on the way in.
        var op = Valid();
        op.Payload = Nested(33);

        Assert.False(OpValidator.TryValidate(op, Limits, out var reason));
        Assert.Contains("levels deep", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_over_nested_op_does_not_condemn_its_batch()
    {
        // The property that matters: a bad op is refused on its own so the good ops
        // beside it still land. Failing the batch would stall the client's queue for one
        // malformed row.
        // 100 levels: past the 32 the validator allows, but still inside the 128 the
        // controller parses, so this is the case that reaches per-op validation at all.
        var deep = Valid();
        deep.OpId = "deep";
        deep.Payload = Nested(100);

        var fine = Valid();
        fine.OpId = "fine";

        Assert.False(OpValidator.TryValidate(deep, Limits, out _));
        Assert.True(OpValidator.TryValidate(fine, Limits, out _));
    }

    [Fact]
    public void The_default_depth_limit_stays_far_below_what_clients_can_decode()
    {
        // Foundation's JSONSerialization gives up around 512 levels, and it fails the
        // whole document. The stored ceiling has to leave obvious room under that.
        var configured = new Configuration.PluginConfiguration().MaxPayloadDepth;

        Assert.InRange(configured, 1, 64);
    }
}
