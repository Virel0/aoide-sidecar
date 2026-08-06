using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;

namespace Jellyfin.Plugin.AoideSidecar.Sync;

/// <summary>
/// Checks an inbound op against the sync contract before it reaches the log.
/// </summary>
/// <remarks>
/// The sidecar validates the envelope and nothing else. It never inspects the payload's
/// fields, because the payload is the client's own row shape: leaving it opaque is what
/// lets the curation-store schema evolve without a coordinated server deploy.
/// </remarks>
public static class OpValidator
{
    private const int MaxIdLength = 128;
    private const int MaxEntityIdLength = 256;

    /// <summary>
    /// Validates a single op.
    /// </summary>
    /// <param name="op">The op to check.</param>
    /// <param name="maxPayloadBytes">The configured payload ceiling.</param>
    /// <param name="reason">On failure, a human-readable explanation.</param>
    /// <returns><c>true</c> when the op may be stored.</returns>
    public static bool TryValidate(SyncOpDto? op, int maxPayloadBytes, out string reason)
    {
        if (op is null)
        {
            reason = "Op was null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(op.OpId) || op.OpId.Length > MaxIdLength)
        {
            reason = $"opId must be a non-empty string of at most {MaxIdLength} characters.";
            return false;
        }

        if (op.Entity == SyncEntities.Tracks)
        {
            reason = "The tracks table is a per-device cache and is never synced. "
                + "Rebuild it from this device's own Jellyfin connection.";
            return false;
        }

        if (!SyncEntities.IsSyncable(op.Entity))
        {
            reason = $"Unknown entity '{op.Entity}'.";
            return false;
        }

        if (!SyncOperations.IsKnown(op.Operation))
        {
            reason = $"Unknown operation '{op.Operation}'; expected "
                + $"'{SyncOperations.Upsert}' or '{SyncOperations.Delete}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(op.EntityId) || op.EntityId.Length > MaxEntityIdLength)
        {
            reason = $"entityId must be a non-empty string of at most {MaxEntityIdLength} characters.";
            return false;
        }

        if (op.Payload.ValueKind != JsonValueKind.Object)
        {
            reason = "payload must be a JSON object holding the full row after the change.";
            return false;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(op.Payload.GetRawText());
        if (payloadBytes > maxPayloadBytes)
        {
            reason = $"payload is {payloadBytes} bytes, over the {maxPayloadBytes} byte limit.";
            return false;
        }

        if (op.CreatedAt <= 0)
        {
            reason = "createdAt must be a positive millisecond timestamp.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
