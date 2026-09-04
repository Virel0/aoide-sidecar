using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A cached measurement for one file.
/// </summary>
/// <param name="JellyfinId">The item.</param>
/// <param name="MtimeTicks">The file's modification time when measured; a changed file is re-measured.</param>
/// <param name="Bounds">The result, or null for "measured, nothing to trim".</param>
/// <param name="Source">"server" or "client".</param>
/// <param name="Error">Why measurement failed, or null.</param>
/// <param name="MeasuredAt">Milliseconds since epoch.</param>
public sealed record SoundBoundsRow(
    string JellyfinId,
    long MtimeTicks,
    SoundBounds? Bounds,
    string Source,
    string? Error,
    long MeasuredAt);

/// <summary>
/// The sound-bounds cache. Global, not per user: where a file's sound starts is a fact
/// about the file, and access is decided at the endpoint against the item instead.
/// </summary>
public sealed class SoundBoundsRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundBoundsRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public SoundBoundsRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads cached rows for the given ids. Ids with no row are simply absent.
    /// </summary>
    /// <param name="ids">Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows keyed by id.</returns>
    public async Task<Dictionary<string, SoundBoundsRow>> GetManyAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var rows = new Dictionary<string, SoundBoundsRow>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return rows;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jellyfin_id, mtime_ticks, sound_start_ms, sound_end_ms, source, error, measured_at
            FROM sound_bounds WHERE jellyfin_id = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);

        foreach (var value in ids)
        {
            id.Value = value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var bounds = reader.IsDBNull(2) || reader.IsDBNull(3)
                    ? null
                    : new SoundBounds(reader.GetInt64(2), reader.GetInt64(3));
                rows[reader.GetString(0)] = new SoundBoundsRow(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    bounds,
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6));
            }
        }

        return rows;
    }

    /// <summary>
    /// Stores a measurement, replacing any earlier one.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UpsertAsync(SoundBoundsRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sound_bounds (jellyfin_id, mtime_ticks, sound_start_ms, sound_end_ms, source, error, measured_at)
            VALUES ($id, $mtime, $start, $end, $source, $error, $at)
            ON CONFLICT (jellyfin_id) DO UPDATE SET
                mtime_ticks = $mtime, sound_start_ms = $start, sound_end_ms = $end,
                source = $source, error = $error, measured_at = $at;
            """;
        command.Parameters.AddWithValue("$id", row.JellyfinId);
        command.Parameters.AddWithValue("$mtime", row.MtimeTicks);
        command.Parameters.AddWithValue("$start", (object?)row.Bounds?.SoundStartMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$end", (object?)row.Bounds?.SoundEndMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", row.Source);
        command.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", row.MeasuredAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
