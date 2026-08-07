using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AoideSidecar.Api.Models;

// Every property carries an explicit [JsonPropertyName]. Jellyfin configures MVC's
// serialiser with a PascalCase naming policy for its own API, so without these the
// responses go out as "Accepted"/"Cursor"/"HasMore" — silently violating the documented
// camelCase contract and failing to decode on a case-sensitive client. Pinning the wire
// names here makes the contract independent of the host's serialiser settings.

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
    [JsonPropertyName("opId")]
    public string? OpId { get; set; }

    /// <summary>Gets or sets the curation-store table this op applies to.</summary>
    [JsonPropertyName("entity")]
    public string? Entity { get; set; }

    /// <summary>Gets or sets the primary key of the affected row.</summary>
    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    /// <summary>Gets or sets the mutation: <c>upsert</c> or <c>delete</c>.</summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    /// <summary>
    /// Gets or sets the full row after the change.
    /// The sidecar stores this verbatim and never interprets it, so the client schema
    /// can evolve without a server deploy.
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    /// <summary>Gets or sets the writing device's clock reading, in milliseconds since epoch.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the server sequence number. Assigned on push, echoed on pull, and
    /// ignored on input — it is how a client learns its op was durably accepted.
    /// </summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    /// <summary>
    /// Gets or sets the device that authored the op. Populated by the server from the
    /// pushing device so a client can recognise and skip its own ops on pull.
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the account that authored the op.
    /// </summary>
    /// <remarks>
    /// Only differs from the caller on a shared playlist, where a collaborator's edits
    /// arrive through the same pull. Set by the server; ignored on input.
    /// </remarks>
    [JsonPropertyName("authorUserId")]
    public string? AuthorUserId { get; set; }

    /// <summary>
    /// Gets or sets the server's receipt time, in milliseconds since epoch.
    /// Carried alongside the client's own clock so that a device with a badly wrong
    /// clock cannot win every field-level conflict forever.
    /// </summary>
    [JsonPropertyName("receivedAt")]
    public long ReceivedAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /aoide/sync/push</c>.
/// </summary>
public class PushRequest
{
    /// <summary>Gets or sets the pushing device's stable identifier.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the ops to append.</summary>
    [JsonPropertyName("ops")]
    public IReadOnlyList<SyncOpDto> Ops { get; set; } = Array.Empty<SyncOpDto>();
}

/// <summary>
/// Explains why a single op was not stored. An op that appears here will never be
/// accepted, so the client should quarantine it rather than retry it forever.
/// </summary>
public class RejectedOpDto
{
    /// <summary>Gets or sets the rejected op's id.</summary>
    [JsonPropertyName("opId")]
    public string? OpId { get; set; }

    /// <summary>Gets or sets a human-readable reason.</summary>
    [JsonPropertyName("reason")]
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
    [JsonPropertyName("accepted")]
    public IReadOnlyList<string> Accepted { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the ops that were refused, with reasons. Always present, empty in
    /// the normal case. Valid ops in the same batch are still accepted, so one
    /// malformed op cannot wedge a client's queue.
    /// </summary>
    [JsonPropertyName("rejected")]
    public IReadOnlyList<RejectedOpDto> Rejected { get; set; } = Array.Empty<RejectedOpDto>();

    /// <summary>
    /// Gets or sets the server's head sequence number for this user.
    /// This is informational: it is not a pull cursor, because ops from other devices
    /// may sit below it unseen. Advance the pull cursor only by pulling.
    /// </summary>
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }
}

/// <summary>
/// Response body for <c>GET /aoide/sync/pull</c>.
/// </summary>
public class PullResponse
{
    /// <summary>Gets or sets the ops, in ascending sequence order.</summary>
    [JsonPropertyName("ops")]
    public IReadOnlyList<SyncOpDto> Ops { get; set; } = Array.Empty<SyncOpDto>();

    /// <summary>
    /// Gets or sets the sequence number of the last op in this batch, or the requested
    /// <c>since</c> when the batch is empty. Store it only after applying the whole
    /// batch, so that an interrupted sync replays rather than skips.
    /// </summary>
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    /// <summary>Gets or sets a value indicating whether more ops are waiting past the cursor.</summary>
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

/// <summary>
/// Response body for <c>GET /aoide/sync/status</c>: what the sidecar can see of its own
/// storage. Exists so that a failure to write is self-diagnosing rather than a bare 500.
/// </summary>
public class SyncStatusDto
{
    /// <summary>Gets or sets the full path of the SQLite file the plugin is using.</summary>
    [JsonPropertyName("databasePath")]
    public string? DatabasePath { get; set; }

    /// <summary>Gets or sets the applied schema version, or -1 if it could not be read.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets a value indicating whether the database accepted a real write.</summary>
    [JsonPropertyName("writable")]
    public bool Writable { get; set; }

    /// <summary>
    /// Gets or sets the journal mode actually in force.
    /// </summary>
    /// <remarks>
    /// Expected to be <c>wal</c>. SQLite silently declines to enter WAL on filesystems
    /// without shared-memory support — a network share, typically — and falls back to
    /// <c>delete</c>, which needs to create a rollback journal beside the database on
    /// every write. That is one of the few states where reads succeed and writes do not.
    /// </remarks>
    [JsonPropertyName("journalMode")]
    public string? JournalMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the containing directory accepts new files.
    /// </summary>
    /// <remarks>
    /// Checked separately from the database file because they fail independently: a
    /// readable database in a directory that refuses new files serves pulls perfectly
    /// and cannot write a journal, which looks exactly like a broken push.
    /// </remarks>
    [JsonPropertyName("directoryWritable")]
    public bool DirectoryWritable { get; set; }

    /// <summary>Gets or sets the caller's head sequence number.</summary>
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    /// <summary>Gets or sets the number of ops stored for the caller.</summary>
    [JsonPropertyName("opCount")]
    public long OpCount { get; set; }

    /// <summary>
    /// Gets or sets the failure that stopped the check, when something went wrong.
    /// Null when everything is healthy.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
