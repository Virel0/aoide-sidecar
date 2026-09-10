using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A cached beat grid for one file.
/// </summary>
/// <param name="JellyfinId">The item.</param>
/// <param name="MtimeTicks">The file's modification time when measured; a changed file is re-measured.</param>
/// <param name="Grid">The grid, or null for "measured, no grid worth having".</param>
/// <param name="Key">The key, or null.</param>
/// <param name="Source">"server" or "client".</param>
/// <param name="Error">Why measurement failed, or null.</param>
/// <param name="MeasuredAt">Milliseconds since epoch.</param>
public sealed record BeatGridRow(
    string JellyfinId,
    long MtimeTicks,
    BeatGrid? Grid,
    MusicalKey? Key,
    string Source,
    string? Error,
    long MeasuredAt);

/// <summary>
/// The beat-grid cache. Global, not per user: where a track's beats fall is a fact about
/// the file, and access is decided at the endpoint against the item instead.
/// </summary>
public sealed class BeatGridRepository
{
    /// <summary>
    /// Segments are stored as JSON in one column. They vary in number, are only ever read
    /// and written whole, and are the server's own shape rather than a client payload, so
    /// a table of their own would buy nothing but joins.
    /// </summary>
    private static readonly JsonSerializerOptions SegmentFormat = new() { WriteIndented = false };

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="BeatGridRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public BeatGridRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads cached rows for the given ids. Ids with no row are simply absent.
    /// </summary>
    /// <param name="ids">Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows keyed by id.</returns>
    public async Task<Dictionary<string, BeatGridRow>> GetManyAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var rows = new Dictionary<string, BeatGridRow>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return rows;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jellyfin_id, mtime_ticks, segments, beats_per_bar, downbeat_index,
                   mix_in_ms, mix_out_ms, key_camelot, key_confidence, source, error, measured_at
            FROM beat_grids WHERE jellyfin_id = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);

        foreach (var value in ids)
        {
            id.Value = value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            BeatGrid? grid = null;
            if (!reader.IsDBNull(2))
            {
                var segments = Deserialize(reader.GetString(2));
                if (segments is { Count: > 0 })
                {
                    grid = new BeatGrid(
                        segments,
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        reader.IsDBNull(6) ? null : reader.GetDouble(6));
                }
            }

            var key = reader.IsDBNull(7) || reader.IsDBNull(8)
                ? null
                : new MusicalKey(reader.GetString(7), reader.GetDouble(8));

            rows[reader.GetString(0)] = new BeatGridRow(
                reader.GetString(0),
                reader.GetInt64(1),
                grid,
                key,
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11));
        }

        return rows;
    }

    /// <summary>
    /// How many files have been measured, and how many of those actually got a grid.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Files measured, and files with a usable grid.</returns>
    /// <remarks>
    /// The two differ on purpose. A spoken-word recording that has been decoded and found
    /// to have no beat is finished, not outstanding, and a progress figure that counted it
    /// as unmeasured would never reach the end.
    /// </remarks>
    public async Task<(long Measured, long Gridded)> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(segments) FROM beat_grids;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : (0, 0);
    }

    /// <summary>
    /// Stores a measurement, replacing any earlier one.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UpsertAsync(BeatGridRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO beat_grids
                (jellyfin_id, mtime_ticks, segments, beats_per_bar, downbeat_index,
                 mix_in_ms, mix_out_ms, key_camelot, key_confidence, source, error, measured_at)
            VALUES ($id, $mtime, $segments, $bar, $downbeat, $in, $out, $key, $keyConfidence, $source, $error, $at)
            ON CONFLICT (jellyfin_id) DO UPDATE SET
                mtime_ticks = $mtime, segments = $segments, beats_per_bar = $bar,
                downbeat_index = $downbeat, mix_in_ms = $in, mix_out_ms = $out,
                key_camelot = $key, key_confidence = $keyConfidence,
                source = $source, error = $error, measured_at = $at;
            """;
        command.Parameters.AddWithValue("$id", row.JellyfinId);
        command.Parameters.AddWithValue("$mtime", row.MtimeTicks);
        command.Parameters.AddWithValue(
            "$segments",
            row.Grid is null ? DBNull.Value : JsonSerializer.Serialize(row.Grid.Segments, SegmentFormat));
        command.Parameters.AddWithValue("$bar", (object?)row.Grid?.BeatsPerBar ?? DBNull.Value);
        command.Parameters.AddWithValue("$downbeat", (object?)row.Grid?.DownbeatIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$in", (object?)row.Grid?.MixInMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$out", (object?)row.Grid?.MixOutMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)row.Key?.Camelot ?? DBNull.Value);
        command.Parameters.AddWithValue("$keyConfidence", (object?)row.Key?.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", row.Source);
        command.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", row.MeasuredAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads segments back, treating anything unreadable as no grid rather than throwing.
    /// </summary>
    private static IReadOnlyList<BeatSegment>? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<BeatSegment>>(json, SegmentFormat);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
