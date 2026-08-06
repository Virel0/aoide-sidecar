using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AoideSidecar.Api.Models;

/// <summary>
/// One entry in the client's append-only op log.
/// </summary>
public class SyncOpDto
{
    /// <summary>
    /// Gets or sets the client-generated UUID that makes the op idempotent.
    /// Re-pushing an op id the server already holds is accepted and ignored, which is
    /// what makes a retry after a timeout safe.
    /// </summary>
    public string? OpId { get; set; }

    /// <summary>Gets or sets the curation-store table this op applies to.</summary>
    public string? Entity { get; set; }

    /// <summary>Gets or sets the primary key of the affected row.</summary>
    public string? EntityId { get; set; }

    /// <summary>Gets or sets the mutation: <c>upsert</c> or <c>delete</c>.</summary>
    public string? Operation { get; set; }

    /// <summary>
    /// Gets or sets the full row after the change.
    /// The sidecar stores this verbatim and never interprets it, so the client schema
    /// can evolve without a server deploy.
    /// </summary>
    public JsonElement Payload { get; set; }

    /// <summary>Gets or sets the writing device's clock reading, in milliseconds since epoch.</summary>
    public long CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the server sequence number. Assigned on push, echoed on pull, and
    /// ignored on input — it is how a client learns its op was durably accepted.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>
    /// Gets or sets the device that authored the op. Populated by the server from the
    /// pushing device so a client can recognise and skip its own ops on pull.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the server's receipt time, in milliseconds since epoch.
    /// Carried alongside the client's own clock so that a device with a badly wrong
    /// clock cannot win every field-level conflict forever.
    /// </summary>
    public long ReceivedAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /aoide/sync/push</c>.
/// </summary>
public class PushRequest
{
    /// <summary>Gets or sets the pushing device's stable identifier.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the ops to append.</summary>
    public IReadOnlyList<SyncOpDto> Ops { get; set; } = Array.Empty<SyncOpDto>();
}

/// <summary>
/// Explains why a single op was not stored. An op that appears here will never be
/// accepted, so the client should quarantine it rather than retry it forever.
/// </summary>
public class RejectedOpDto
{
    /// <summary>Gets or sets the rejected op's id.</summary>
    public string? OpId { get; set; }

    /// <summary>Gets or sets a human-readable reason.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Response body for <c>POST /aoide/sync/push</c>.
/// </summary>
public class PushResponse
{
    /// <summary>
    /// Gets or sets the op ids the server now durably holds, including ones it had
    /// already seen. Any op id absent from this list was not stored.
    /// </summary>
    public IReadOnlyList<string> Accepted { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the ops that were refused, with reasons. Always present, empty in
    /// the normal case. Valid ops in the same batch are still accepted, so one
    /// malformed op cannot wedge a client's queue.
    /// </summary>
    public IReadOnlyList<RejectedOpDto> Rejected { get; set; } = Array.Empty<RejectedOpDto>();

    /// <summary>
    /// Gets or sets the server's head sequence number for this user.
    /// This is informational: it is not a pull cursor, because ops from other devices
    /// may sit below it unseen. Advance the pull cursor only by pulling.
    /// </summary>
    public long Cursor { get; set; }
}

/// <summary>
/// Response body for <c>GET /aoide/sync/pull</c>.
/// </summary>
public class PullResponse
{
    /// <summary>Gets or sets the ops, in ascending sequence order.</summary>
    public IReadOnlyList<SyncOpDto> Ops { get; set; } = Array.Empty<SyncOpDto>();

    /// <summary>
    /// Gets or sets the sequence number of the last op in this batch, or the requested
    /// <c>since</c> when the batch is empty. Store it only after applying the whole
    /// batch, so that an interrupted sync replays rather than skips.
    /// </summary>
    public long Cursor { get; set; }

    /// <summary>Gets or sets a value indicating whether more ops are waiting past the cursor.</summary>
    public bool HasMore { get; set; }
}
