using Jellyfin.Plugin.AoideSidecar.Sound;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A cached loudness and tempo measurement for one file.
/// </summary>
/// <param name="JellyfinId">The item.</param>
/// <param name="MtimeTicks">The file's modification time when measured; a changed file is re-measured.</param>
/// <param name="Loudness">Integrated loudness and true peak, or null when there was nothing to measure.</param>
/// <param name="Tempo">Tempo and confidence as measured, whatever the confidence.</param>
/// <param name="Source">"server" or "client".</param>
/// <param name="Error">Why measurement failed, or null.</param>
/// <param name="MeasuredAt">Milliseconds since epoch.</param>
/// <remarks>
/// The tempo is stored at whatever confidence it came out with, and the threshold below
/// which it is not worth reporting is applied when the row is served. Storing the raw
/// number means that threshold can be reconsidered without decoding a library again.
/// </remarks>
public sealed record AudioAnalysisRow(
    string JellyfinId,
    long MtimeTicks,
    Loudness? Loudness,
    Tempo? Tempo,
    string Source,
    string? Error,
    long MeasuredAt);

/// <summary>
/// The loudness and tempo cache. Global, not per user: both are facts about the file,
/// and access is decided at the endpoint against the item instead.
/// </summary>
public sealed class AudioAnalysisRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioAnalysisRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public AudioAnalysisRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads cached rows for the given ids. Ids with no row are simply absent.
    /// </summary>
    /// <param name="ids">Jellyfin item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows keyed by id.</returns>
    public async Task<Dictionary<string, AudioAnalysisRow>> GetManyAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var rows = new Dictionary<string, AudioAnalysisRow>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return rows;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jellyfin_id, mtime_ticks, loudness_lufs, true_peak_dbfs, bpm, bpm_confidence,
                   source, error, measured_at, bpm_stability
            FROM audio_analysis WHERE jellyfin_id = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);

        foreach (var value in ids)
        {
            id.Value = value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var loudness = reader.IsDBNull(2) || reader.IsDBNull(3)
                    ? null
                    : new Loudness(reader.GetDouble(2), reader.GetDouble(3));
                var tempo = reader.IsDBNull(4) || reader.IsDBNull(5)
                    ? null
                    : new Tempo(
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.IsDBNull(9) ? null : reader.GetDouble(9));
                rows[reader.GetString(0)] = new AudioAnalysisRow(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    loudness,
                    tempo,
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8));
            }
        }

        return rows;
    }

    /// <summary>
    /// How many files have a measurement stored.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row count.</returns>
    public async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audio_analysis;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stores a measurement, replacing any earlier one.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UpsertAsync(AudioAnalysisRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audio_analysis
                (jellyfin_id, mtime_ticks, loudness_lufs, true_peak_dbfs, bpm, bpm_confidence,
                 bpm_stability, source, error, measured_at)
            VALUES ($id, $mtime, $lufs, $peak, $bpm, $confidence, $stability, $source, $error, $at)
            ON CONFLICT (jellyfin_id) DO UPDATE SET
                mtime_ticks = $mtime, loudness_lufs = $lufs, true_peak_dbfs = $peak,
                bpm = $bpm, bpm_confidence = $confidence, bpm_stability = $stability,
                source = $source, error = $error, measured_at = $at;
            """;
        command.Parameters.AddWithValue("$id", row.JellyfinId);
        command.Parameters.AddWithValue("$mtime", row.MtimeTicks);
        command.Parameters.AddWithValue("$lufs", (object?)row.Loudness?.LoudnessLufs ?? DBNull.Value);
        command.Parameters.AddWithValue("$peak", (object?)row.Loudness?.TruePeakDbfs ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpm", (object?)row.Tempo?.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (object?)row.Tempo?.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$stability", (object?)row.Tempo?.Stability ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", row.Source);
        command.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", row.MeasuredAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
