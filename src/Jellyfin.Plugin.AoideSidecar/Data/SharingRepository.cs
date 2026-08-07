using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Sync;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AoideSidecar.Data;

/// <summary>
/// A playlist one user has given another access to.
/// </summary>
/// <param name="PlaylistId">The shared playlist.</param>
/// <param name="OwnerUserId">Who owns it.</param>
/// <param name="GranteeUserId">Who it is shared with.</param>
/// <param name="CanEdit">Whether the grantee may change it.</param>
/// <param name="CreatedAt">When the share was made.</param>
public sealed record PlaylistShare(
    string PlaylistId,
    Guid OwnerUserId,
    Guid GranteeUserId,
    bool CanEdit,
    long CreatedAt);

/// <summary>
/// Who owns which playlist, and who else may see or change it.
/// </summary>
public sealed class SharingRepository
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharingRepository"/> class.
    /// </summary>
    /// <param name="database">The sync database.</param>
    public SharingRepository(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Finds the playlist an op belongs to.
    /// </summary>
    /// <remarks>
    /// The only field the write path reads out of a payload. A <c>playlists</c> op is
    /// already keyed by its playlist, so nothing is parsed there; a <c>playlist_items</c>
    /// op names its playlist inside, under either naming convention. Anything else is
    /// personal and belongs to no playlist.
    /// </remarks>
    /// <param name="op">The op.</param>
    /// <returns>The playlist id, or null when the op is not playlist-scoped.</returns>
    public static string? PlaylistIdOf(SyncOpDto op)
    {
        ArgumentNullException.ThrowIfNull(op);

        if (op.Entity == SyncEntities.Playlists)
        {
            return op.EntityId;
        }

        if (op.Entity != SyncEntities.PlaylistItems || op.Payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "playlist_id", "playlistId" })
        {
            if (op.Payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Records ownership of a playlist if it has none yet. First writer wins.
    /// </summary>
    /// <param name="playlistIds">Playlists seen in a push.</param>
    /// <param name="userId">The pushing user.</param>
    /// <param name="now">Milliseconds since epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ClaimOwnershipAsync(
        IReadOnlyCollection<string> playlistIds,
        Guid userId,
        long now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlistIds);

        if (playlistIds.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playlist_owners (playlist_id, owner_user_id, created_at)
            VALUES ($p, $u, $t)
            ON CONFLICT (playlist_id) DO NOTHING;
            """;

        var playlist = command.Parameters.Add("$p", SqliteType.Text);
        command.Parameters.AddWithValue("$u", Key(userId));
        command.Parameters.AddWithValue("$t", now);

        foreach (var id in playlistIds)
        {
            playlist.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out which of the given playlists a user may write to.
    /// </summary>
    /// <param name="playlistIds">The playlists to check.</param>
    /// <param name="userId">The writing user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subset the user owns, has edit access to, or that nobody owns yet.</returns>
    public async Task<HashSet<string>> GetWritableAsync(
        IReadOnlyCollection<string> playlistIds,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlistIds);

        var writable = new HashSet<string>(playlistIds, StringComparer.Ordinal);
        if (writable.Count == 0)
        {
            return writable;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Owned by somebody else, and not shared with this user for editing.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.playlist_id
            FROM playlist_owners o
            WHERE o.owner_user_id <> $u
              AND NOT EXISTS (
                  SELECT 1 FROM playlist_shares s
                  WHERE s.playlist_id = o.playlist_id AND s.grantee_user_id = $u AND s.can_edit = 1);
            """;
        command.Parameters.AddWithValue("$u", Key(userId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writable.Remove(reader.GetString(0));
        }

        return writable;
    }

    /// <summary>
    /// Gets the owner of a playlist.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owner, or null if nobody has written it yet.</returns>
    public async Task<Guid?> GetOwnerAsync(string playlistId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_user_id FROM playlist_owners WHERE playlist_id = $p;";
        command.Parameters.AddWithValue("$p", playlistId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string text && Guid.TryParse(text, out var owner) ? owner : null;
    }

    /// <summary>
    /// Shares a playlist with another user, or updates an existing share.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="granteeUserId">Who to share with.</param>
    /// <param name="canEdit">Whether they may change it.</param>
    /// <param name="now">Milliseconds since epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ShareAsync(
        string playlistId,
        Guid granteeUserId,
        bool canEdit,
        long now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playlist_shares (playlist_id, grantee_user_id, can_edit, created_at)
            VALUES ($p, $g, $e, $t)
            ON CONFLICT (playlist_id, grantee_user_id) DO UPDATE SET can_edit = $e;
            """;
        command.Parameters.AddWithValue("$p", playlistId);
        command.Parameters.AddWithValue("$g", Key(granteeUserId));
        command.Parameters.AddWithValue("$e", canEdit ? 1 : 0);
        command.Parameters.AddWithValue("$t", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a share.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="granteeUserId">Whose access to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a share was removed.</returns>
    public async Task<bool> RevokeAsync(string playlistId, Guid granteeUserId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM playlist_shares WHERE playlist_id = $p AND grantee_user_id = $g;";
        command.Parameters.AddWithValue("$p", playlistId);
        command.Parameters.AddWithValue("$g", Key(granteeUserId));

        var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>
    /// Lists every share a user is party to, as owner or as grantee.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shares.</returns>
    public async Task<IReadOnlyList<PlaylistShare>> ListSharesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.playlist_id, o.owner_user_id, s.grantee_user_id, s.can_edit, s.created_at
            FROM playlist_shares s
            JOIN playlist_owners o ON o.playlist_id = s.playlist_id
            WHERE o.owner_user_id = $u OR s.grantee_user_id = $u
            ORDER BY s.created_at DESC;
            """;
        command.Parameters.AddWithValue("$u", Key(userId));

        var shares = new List<PlaylistShare>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            shares.Add(new PlaylistShare(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3) != 0,
                reader.GetInt64(4)));
        }

        return shares;
    }

    private static string Key(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);
}
