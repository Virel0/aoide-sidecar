using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;

namespace Jellyfin.Plugin.AoideSidecar.Sync;

/// <summary>
/// The envelope limits an op is checked against.
/// </summary>
/// <param name="MaxPayloadBytes">Largest accepted payload, in UTF-8 bytes.</param>
/// <param name="MaxPayloadDepth">Deepest accepted payload nesting.</param>
public sealed record OpLimits(int MaxPayloadBytes, int MaxPayloadDepth);

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
    /// <param name="limits">The configured envelope limits.</param>
    /// <param name="reason">On failure, a human-readable explanation.</param>
    /// <returns><c>true</c> when the op may be stored.</returns>
    public static bool TryValidate(SyncOpDto? op, OpLimits limits, out string reason)
    {
        ArgumentNullException.ThrowIfNull(limits);

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

        // A stored payload is echoed to every other device on pull, and a client's JSON
        // decoder refuses an over-nested document whole rather than per-element — so one
        // pathological row would wedge inbound sync for every device with no way to skip
        // past it. Bounding depth on the way in is the only place this can be defended.
        var depth = MeasureDepth(op.Payload);
        if (depth > limits.MaxPayloadDepth)
        {
            reason = $"payload nests {depth} levels deep, over the {limits.MaxPayloadDepth} level limit.";
            return false;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(op.Payload.GetRawText());
        if (payloadBytes > limits.MaxPayloadBytes)
        {
            reason = $"payload is {payloadBytes} bytes, over the {limits.MaxPayloadBytes} byte limit.";
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

    /// <summary>
    /// Measures how deeply a JSON value nests. A scalar is 0, <c>{"a":1}</c> is 1.
    /// </summary>
    /// <remarks>
    /// Recursion is safe here: the request was already parsed under a bounded
    /// <see cref="JsonSerializerOptions.MaxDepth"/>, so this cannot run deeper than that.
    /// </remarks>
    /// <param name="element">The value to measure.</param>
    /// <returns>The nesting depth.</returns>
    public static int MeasureDepth(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var objectDepth = 0;
                foreach (var property in element.EnumerateObject())
                {
                    objectDepth = Math.Max(objectDepth, MeasureDepth(property.Value));
                }

                return objectDepth + 1;

            case JsonValueKind.Array:
                var arrayDepth = 0;
                foreach (var item in element.EnumerateArray())
                {
                    arrayDepth = Math.Max(arrayDepth, MeasureDepth(item));
                }

                return arrayDepth + 1;

            default:
                return 0;
        }
    }
}
