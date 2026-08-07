using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A stored image and the type it was uploaded as.
/// </summary>
/// <param name="Bytes">The raw image.</param>
/// <param name="MimeType">The content type to serve it back with.</param>
public sealed record StoredImage(byte[] Bytes, string MimeType);

/// <summary>
/// Content-addressed storage for playlist artwork.
/// </summary>
/// <remarks>
/// Artwork lives here rather than in an op payload because every device replays the
/// whole op log: carrying image bytes through it would make a full history sync grow
/// without bound. The log carries only the hash, which is what makes the reference
/// cheap, and this holds the bytes behind that hash.
/// </remarks>
public sealed class PlaylistImageRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistImageRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public PlaylistImageRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets a value indicating whether the user already has this image.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="hash">Lowercase hex SHA-256 of the bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the bytes are already stored.</returns>
    public async Task<bool> ExistsAsync(Guid userId, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM playlist_images WHERE user_id = $u AND image_hash = $h;";
        command.Parameters.AddWithValue("$u", Key(userId));
        command.Parameters.AddWithValue("$h", hash);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Reads an image back.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="hash">Lowercase hex SHA-256 of the bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image, or <c>null</c> if the user has no such hash.</returns>
    public async Task<StoredImage?> GetAsync(Guid userId, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bytes, mime_type FROM playlist_images WHERE user_id = $u AND image_hash = $h;";
        command.Parameters.AddWithValue("$u", Key(userId));
        command.Parameters.AddWithValue("$h", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredImage((byte[])reader[0], reader.GetString(1));
    }

    /// <summary>
    /// Stores an image. Re-uploading the same hash is a no-op, because the bytes behind
    /// a content address cannot differ.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="hash">Lowercase hex SHA-256 of the bytes.</param>
    /// <param name="mimeType">The content type.</param>
    /// <param name="bytes">The image.</param>
    /// <param name="now">Milliseconds since epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task StoreAsync(
        Guid userId,
        string hash,
        string mimeType,
        byte[] bytes,
        long now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playlist_images (user_id, image_hash, mime_type, bytes, size, created_at)
            VALUES ($u, $h, $m, $b, $s, $c)
            ON CONFLICT (user_id, image_hash) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$u", Key(userId));
        command.Parameters.AddWithValue("$h", hash);
        command.Parameters.AddWithValue("$m", mimeType);
        command.Parameters.AddWithValue("$b", bytes);
        command.Parameters.AddWithValue("$s", bytes.Length);
        command.Parameters.AddWithValue("$c", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every blob the user has, without loading any image data.
    /// </summary>
    /// <remarks>
    /// The bytes column is deliberately not selected. A sweep over a store holding tens
    /// of megabytes has no reason to read any of it into memory to answer questions
    /// about size and age.
    /// </remarks>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every stored blob, newest first.</returns>
    public async Task<IReadOnlyList<StoredImageInfo>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT image_hash, size, created_at
            FROM playlist_images
            WHERE user_id = $u
            ORDER BY created_at DESC;
            """;
        command.Parameters.AddWithValue("$u", Key(userId));

        var images = new List<StoredImageInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            images.Add(new StoredImageInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return images;
    }

    /// <summary>
    /// Deletes blobs by hash.
    /// </summary>
    /// <remarks>
    /// Every statement is scoped by <c>user_id</c> as well as hash. Content addresses are
    /// derived from the bytes, so two accounts holding the same image hold the same hash —
    /// without the user predicate, reclaiming one person's orphan would delete another
    /// person's live cover.
    /// </remarks>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="hashes">The hashes to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of blobs actually deleted.</returns>
    public async Task<int> DeleteAsync(
        Guid userId,
        IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        if (hashes.Count == 0)
        {
            return 0;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM playlist_images WHERE user_id = $u AND image_hash = $h;";

        var user = command.Parameters.Add("$u", SqliteType.Text);
        var hash = command.Parameters.Add("$h", SqliteType.Text);
        user.Value = Key(userId);

        var deleted = 0;
        foreach (var value in hashes)
        {
            hash.Value = value;
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static string Key(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// A stored blob's metadata, without its bytes.
/// </summary>
/// <param name="ImageHash">Lowercase hex SHA-256 of the content.</param>
/// <param name="SizeBytes">Size on disk.</param>
/// <param name="CreatedAt">Milliseconds since epoch when it was first stored.</param>
public sealed record StoredImageInfo(string ImageHash, long SizeBytes, long CreatedAt);
