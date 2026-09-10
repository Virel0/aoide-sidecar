using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A cached arrangement for one file.
/// </summary>
/// <param name="JellyfinId">The item.</param>
/// <param name="MtimeTicks">The file's modification time when measured; a changed file is re-measured.</param>
/// <param name="Arrangement">The arrangement, or null for "measured, no structure worth describing".</param>
/// <param name="Source">"server" or "client".</param>
/// <param name="Error">Why measurement failed, or null.</param>
/// <param name="MeasuredAt">Milliseconds since epoch.</param>
public sealed record ArrangementRow(
    string JellyfinId,
    long MtimeTicks,
    Arrangement? Arrangement,
    string Source,
    string? Error,
    long MeasuredAt);

/// <summary>
/// The arrangement cache. Global, not per user: what a track is made of is a fact about
/// the file, and access is decided at the endpoint against the item instead.
/// </summary>
public sealed class ArrangementRepository
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrangementRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public ArrangementRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads cached rows for the given ids. Ids with no row are simply absent.
    /// </summary>
    /// <param name="ids">Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows keyed by id.</returns>
    public async Task<Dictionary<string, ArrangementRow>> GetManyAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var rows = new Dictionary<string, ArrangementRow>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return rows;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jellyfin_id, mtime_ticks, sections, phrase_bars, phrase_anchor_ms, vocals,
                   source, error, measured_at
            FROM arrangements WHERE jellyfin_id = $id;
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

            Arrangement? arrangement = null;
            if (!reader.IsDBNull(2))
            {
                var sections = Read<List<Section>>(reader.GetString(2));
                if (sections is { Count: > 0 })
                {
                    // A null vocals column is "could not tell", which is a different answer
                    // from an empty list, so the distinction has to survive the round trip.
                    arrangement = new Arrangement(
                        sections,
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        reader.IsDBNull(5) ? null : Read<List<VocalSpan>>(reader.GetString(5)));
                }
            }

            rows[reader.GetString(0)] = new ArrangementRow(
                reader.GetString(0),
                reader.GetInt64(1),
                arrangement,
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8));
        }

        return rows;
    }

    /// <summary>
    /// Stores a measurement, replacing any earlier one.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UpsertAsync(ArrangementRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO arrangements
                (jellyfin_id, mtime_ticks, sections, phrase_bars, phrase_anchor_ms, vocals,
                 source, error, measured_at)
            VALUES ($id, $mtime, $sections, $bars, $anchor, $vocals, $source, $error, $at)
            ON CONFLICT (jellyfin_id) DO UPDATE SET
                mtime_ticks = $mtime, sections = $sections, phrase_bars = $bars,
                phrase_anchor_ms = $anchor, vocals = $vocals,
                source = $source, error = $error, measured_at = $at;
            """;
        command.Parameters.AddWithValue("$id", row.JellyfinId);
        command.Parameters.AddWithValue("$mtime", row.MtimeTicks);
        command.Parameters.AddWithValue(
            "$sections",
            row.Arrangement is null ? DBNull.Value : JsonSerializer.Serialize(row.Arrangement.Sections, Format));
        command.Parameters.AddWithValue("$bars", (object?)row.Arrangement?.PhraseBars ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchor", (object?)row.Arrangement?.PhraseAnchorMs ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$vocals",
            row.Arrangement?.Vocals is null ? DBNull.Value : JsonSerializer.Serialize(row.Arrangement.Vocals, Format));
        command.Parameters.AddWithValue("$source", row.Source);
        command.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", row.MeasuredAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How many files have an arrangement stored, and how many of those got one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Files measured, and files with sections.</returns>
    public async Task<(long Measured, long Described)> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(sections) FROM arrangements;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : (0, 0);
    }

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Format);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
