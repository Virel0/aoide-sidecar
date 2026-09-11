using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Next;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// Reads what a listener has heard and flagged out of the op log.
/// </summary>
/// <remarks>
/// <para>
/// The third place the server reads inside a payload, and the first where it reads whole
/// rows rather than one field. The op log is opaque by design and stays so on the sync
/// path; this reads it the way a client would, after the fact, to answer a question the
/// clients asked the server to answer for them. The fields read are the ones both clients
/// write — <c>jellyfinId</c>, <c>startedAt</c>, <c>endedAt</c>, <c>completed</c>,
/// <c>skipped</c>, <c>notInterested</c>, <c>deleted</c> — in either naming convention, and
/// an op missing any of them is skipped rather than failing the request.
/// </para>
/// <para>
/// Play events are append-only, so every op is one listen. Track flags are merged rows,
/// so the latest op per track wins.
/// </para>
/// </remarks>
public sealed class ListeningRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListeningRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public ListeningRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Every listen the server holds for a user, from every device.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The listens, in no particular order.</returns>
    internal async Task<IReadOnlyList<PlayEvent>> PlayEventsAsync(string userId, CancellationToken cancellationToken)
    {
        var events = new Dictionary<string, PlayEvent>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_id, payload FROM ops
            WHERE user_id = $user AND entity = 'play_events'
            ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$user", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Parse(reader.GetString(1)) is { } play)
            {
                // Keyed by the event's own id: a re-pushed event is the same listen.
                events[reader.GetString(0)] = play;
            }
        }

        return events.Values.ToList();
    }

    /// <summary>
    /// The tracks a user has flagged not interested.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalised Jellyfin ids.</returns>
    public async Task<IReadOnlySet<string>> NotInterestedAsync(string userId, CancellationToken cancellationToken)
    {
        // The latest op per track decides: a flag set and later cleared is not a flag.
        var latest = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload FROM ops
            WHERE user_id = $user AND entity = 'track_flags'
            ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$user", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(0));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Text(root, "jellyfinId", "jellyfin_id") is not { } id)
                {
                    continue;
                }

                var flagged = Flag(root, "notInterested", "not_interested") && !Flag(root, "deleted", "deleted");
                latest[AudioIds.Normalise(id)] = flagged;
            }
            catch (JsonException)
            {
                // A payload the server cannot read is not a flag.
            }
        }

        return latest.Where(f => f.Value).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static PlayEvent? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "jellyfinId", "jellyfin_id") is not { } id
                || Number(root, "startedAt", "started_at") is not { } startedAt)
            {
                return null;
            }

            return new PlayEvent(
                AudioIds.Normalise(id),
                startedAt,
                Number(root, "endedAt", "ended_at"),
                Flag(root, "completed", "completed"),
                Flag(root, "skipped", "skipped"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string camel, string snake)
    {
        if ((root.TryGetProperty(camel, out var value) || root.TryGetProperty(snake, out value))
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static long? Number(JsonElement root, string camel, string snake)
    {
        if (!(root.TryGetProperty(camel, out var value) || root.TryGetProperty(snake, out value)))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
            JsonValueKind.Number when value.TryGetDouble(out var real) && double.IsFinite(real) => (long)real,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// A boolean however a client wrote it: true, 1, or "1". Absent is false.
    /// </summary>
    private static bool Flag(JsonElement root, string camel, string snake)
    {
        if (!(root.TryGetProperty(camel, out var value) || root.TryGetProperty(snake, out value)))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetDouble(out var n) && n != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }
}
