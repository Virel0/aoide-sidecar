using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// Reads and writes the op log.
/// </summary>
public sealed class SyncRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public SyncRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the full path of the SQLite file, for diagnostics and log context.
    /// </summary>
    public string DatabasePath => _database.DatabasePath;

    /// <summary>
    /// Reports what the sidecar can see of its own storage, including whether the
    /// database actually accepts a write.
    /// </summary>
    /// <remarks>
    /// A store that reads but will not write produces exactly one symptom from the
    /// outside — pull works, push fails — and nothing in a normal response says why.
    /// This makes that case answer for itself.
    /// </remarks>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status, with <c>Error</c> set if the check could not complete.</returns>
    public async Task<SyncStatusDto> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var status = new SyncStatusDto
        {
            DatabasePath = _database.DatabasePath,
            SchemaVersion = -1
        };

        // Probed before opening the database, because this is the check that still
        // answers when opening is the thing that fails.
        status.DirectoryWritable = ProbeDirectory(_database.DatabasePath);

        try
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var user = ToKey(userId);

            status.JournalMode = (await ScalarAsync(connection, "PRAGMA journal_mode;", cancellationToken)
                .ConfigureAwait(false))?.ToString();

            status.SchemaVersion = Convert.ToInt32(
                await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);

            status.Cursor = await ReadCursorAsync(connection, null, user, cancellationToken).ConfigureAwait(false);

            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM ops WHERE user_id = $userId;";
                count.Parameters.AddWithValue("$userId", user);
                status.OpCount = Convert.ToInt64(
                    await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            // Take the write lock and dirty a page for real, then commit. Re-setting
            // user_version to the value it already holds is a genuine write with no
            // change of meaning, so this exercises the same path push does.
            await using var probe = connection.BeginTransaction(deferred: false);
            await using (var write = connection.CreateCommand())
            {
                write.Transaction = probe;
                write.CommandText = $"PRAGMA user_version={status.SchemaVersion};";
                await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await probe.CommitAsync(cancellationToken).ConfigureAwait(false);
            status.Writable = true;
        }
        catch (Exception ex)
        {
            status.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return status;
    }

    /// <summary>
    /// Reads every playlist and playlist-item op for a user, oldest first.
    /// </summary>
    /// <remarks>
    /// The whole history rather than a window, because the projection has to know the
    /// current state of rows that may not have been touched in a long time. Only two of
    /// the six entities are read, which keeps this far smaller than the full log —
    /// play_events, the one table that grows without bound, is not among them.
    /// </remarks>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ops, in ascending sequence order.</returns>
    public async Task<IReadOnlyList<SyncOpDto>> ReadPlaylistOpsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, op_id, device_id, entity, entity_id, operation, payload, created_at, received_at
            FROM ops
            WHERE user_id = $userId AND entity IN ('playlists', 'playlist_items')
            ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$userId", ToKey(userId));

        var ops = new List<SyncOpDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(6));
            ops.Add(new SyncOpDto
            {
                Seq = reader.GetInt64(0),
                OpId = reader.GetString(1),
                DeviceId = reader.GetString(2),
                Entity = reader.GetString(3),
                EntityId = reader.GetString(4),
                Operation = reader.GetString(5),
                Payload = document.RootElement.Clone(),
                CreatedAt = reader.GetInt64(7),
                ReceivedAt = reader.GetInt64(8)
            });
        }

        return ops;
    }

    /// <summary>
    /// Reads the map of exported playlists for a user.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aoide playlist id to the Jellyfin item id and last exported content hash.</returns>
    public async Task<Dictionary<string, (string JellyfinItemId, string ContentHash)>> GetExportMapAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aoide_playlist_id, jellyfin_item_id, content_hash
            FROM exported_playlists WHERE user_id = $userId;
            """;
        command.Parameters.AddWithValue("$userId", ToKey(userId));

        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            map[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        }

        return map;
    }

    /// <summary>
    /// Records that a playlist has been exported.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="aoidePlaylistId">The curation-store playlist id.</param>
    /// <param name="jellyfinItemId">The Jellyfin playlist item id.</param>
    /// <param name="contentHash">Hash of what was written, so an unchanged playlist is skipped next run.</param>
    /// <param name="now">Milliseconds since epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecordExportAsync(
        Guid userId,
        string aoidePlaylistId,
        string jellyfinItemId,
        string contentHash,
        long now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO exported_playlists (user_id, aoide_playlist_id, jellyfin_item_id, content_hash, exported_at)
            VALUES ($u, $a, $j, $h, $t)
            ON CONFLICT (user_id, aoide_playlist_id)
            DO UPDATE SET jellyfin_item_id = $j, content_hash = $h, exported_at = $t;
            """;
        command.Parameters.AddWithValue("$u", ToKey(userId));
        command.Parameters.AddWithValue("$a", aoidePlaylistId);
        command.Parameters.AddWithValue("$j", jellyfinItemId);
        command.Parameters.AddWithValue("$h", contentHash);
        command.Parameters.AddWithValue("$t", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets an exported playlist.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="aoidePlaylistId">The curation-store playlist id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ForgetExportAsync(Guid userId, string aoidePlaylistId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM exported_playlists WHERE user_id = $u AND aoide_playlist_id = $a;";
        command.Parameters.AddWithValue("$u", ToKey(userId));
        command.Parameters.AddWithValue("$a", aoidePlaylistId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ProbeDirectory(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, ".aoide-write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends validated ops to the log and returns the user's head sequence.
    /// </summary>
    /// <remarks>
    /// Every op carries a client-generated id under a unique index, so re-pushing a
    /// batch the server already holds inserts nothing and still reports it accepted.
    /// A client that times out mid-push can simply send the batch again.
    /// </remarks>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="deviceId">The pushing device.</param>
    /// <param name="ops">Ops that have already passed validation.</param>
    /// <param name="receivedAt">Server receipt time, in milliseconds since epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's head sequence number after the append.</returns>
    public async Task<long> AppendAsync(
        Guid userId,
        string deviceId,
        IReadOnlyList<SyncOpDto> ops,
        long receivedAt,
        CancellationToken cancellationToken)
    {
        var user = ToKey(userId);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // IMMEDIATE takes the write lock up front. A deferred transaction that reads
        // before it writes can fail to upgrade under contention, and there is no safe
        // way to retry that from inside the transaction.
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (ops.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ops (op_id, user_id, device_id, entity, entity_id, operation, payload, created_at, received_at)
                VALUES ($opId, $userId, $deviceId, $entity, $entityId, $operation, $payload, $createdAt, $receivedAt)
                ON CONFLICT (user_id, op_id) DO NOTHING;
                """;

            var opId = command.Parameters.Add("$opId", SqliteType.Text);
            var userParam = command.Parameters.Add("$userId", SqliteType.Text);
            var device = command.Parameters.Add("$deviceId", SqliteType.Text);
            var entity = command.Parameters.Add("$entity", SqliteType.Text);
            var entityId = command.Parameters.Add("$entityId", SqliteType.Text);
            var operation = command.Parameters.Add("$operation", SqliteType.Text);
            var payload = command.Parameters.Add("$payload", SqliteType.Text);
            var createdAt = command.Parameters.Add("$createdAt", SqliteType.Integer);
            var received = command.Parameters.Add("$receivedAt", SqliteType.Integer);

            userParam.Value = user;
            device.Value = deviceId;
            received.Value = receivedAt;

            foreach (var op in ops)
            {
                opId.Value = op.OpId;
                entity.Value = op.Entity;
                entityId.Value = op.EntityId;
                operation.Value = op.Operation;
                payload.Value = op.Payload.GetRawText();
                createdAt.Value = op.CreatedAt;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var cursor = await ReadCursorAsync(connection, transaction, user, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return cursor;
    }

    /// <summary>
    /// Reads a page of ops past a cursor, in sequence order.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="since">Exclusive lower bound; pass 0 for a full history sync.</param>
    /// <param name="limit">Maximum ops to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch, its cursor, and whether more remain.</returns>
    public async Task<PullResponse> ReadAsync(
        Guid userId,
        long since,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // One row beyond the limit answers hasMore exactly, so a client is never told
        // to come back for a page that turns out to be empty.
        command.CommandText = """
            SELECT seq, op_id, device_id, entity, entity_id, operation, payload, created_at, received_at
            FROM ops
            WHERE user_id = $userId AND seq > $since
            ORDER BY seq
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$userId", ToKey(userId));
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$limit", limit + 1);

        var ops = new List<SyncOpDto>(limit);
        var hasMore = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ops.Count == limit)
            {
                hasMore = true;
                break;
            }

            using var document = JsonDocument.Parse(reader.GetString(6));

            ops.Add(new SyncOpDto
            {
                Seq = reader.GetInt64(0),
                OpId = reader.GetString(1),
                DeviceId = reader.GetString(2),
                Entity = reader.GetString(3),
                EntityId = reader.GetString(4),
                Operation = reader.GetString(5),

                // Clone detaches the element from the document being disposed here.
                Payload = document.RootElement.Clone(),
                CreatedAt = reader.GetInt64(7),
                ReceivedAt = reader.GetInt64(8)
            });
        }

        return new PullResponse
        {
            Ops = ops,
            Cursor = ops.Count > 0 ? ops[^1].Seq : since,
            HasMore = hasMore
        };
    }

    /// <summary>
    /// Reads the user's head sequence number without returning any ops.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The highest sequence number stored for the user, or 0.</returns>
    public async Task<long> GetCursorAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCursorAsync(connection, null, ToKey(userId), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadCursorAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string userKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM ops WHERE user_id = $userId;";
        command.Parameters.AddWithValue("$userId", userKey);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static string ToKey(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);
}
