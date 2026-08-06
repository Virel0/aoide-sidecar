namespace Jellyfin.Plugin.AoideSidecar.Sync;

/// <summary>
/// The set of curation-store tables whose ops the sidecar will relay.
/// </summary>
/// <remarks>
/// This is an allow-list rather than a deny-list so that an unrecognised entity is
/// rejected loudly at the boundary instead of accumulating in the log as data no
/// client knows how to apply.
/// <para>
/// <c>tracks</c> is deliberately absent. It is a per-device cache rebuilt from each
/// client's own Jellyfin connection, and keeping it out of the op log is what keeps a
/// full history sync small. A client that pushes it has a bug worth surfacing.
/// </para>
/// </remarks>
public static class SyncEntities
{
    /// <summary>Playlists, both static and smart.</summary>
    public const string Playlists = "playlists";

    /// <summary>Playlist membership, ordered by fractional index.</summary>
    public const string PlaylistItems = "playlist_items";

    /// <summary>The playlist folder tree.</summary>
    public const string Folders = "folders";

    /// <summary>Per-track like state, owned locally and written through to Jellyfin.</summary>
    public const string Likes = "likes";

    /// <summary>Append-only playback history.</summary>
    public const string PlayEvents = "play_events";

    /// <summary>One row per device, for resume-across-devices.</summary>
    public const string QueueState = "queue_state";

    /// <summary>The local-only track cache, named here so it can be rejected with a useful message.</summary>
    public const string Tracks = "tracks";

    private static readonly HashSet<string> SyncableEntities = new(StringComparer.Ordinal)
    {
        Playlists,
        PlaylistItems,
        Folders,
        Likes,
        PlayEvents,
        QueueState
    };

    /// <summary>
    /// Gets a value indicating whether ops for the given table are relayed by the sidecar.
    /// </summary>
    /// <param name="entity">The curation-store table name.</param>
    /// <returns><c>true</c> if the entity syncs.</returns>
    public static bool IsSyncable(string? entity) =>
        entity is not null && SyncableEntities.Contains(entity);
}

/// <summary>
/// The mutations an op can express. Deletes are soft — the payload still carries the
/// full row with <c>deleted = 1</c> — so that a removal is a change other devices can
/// observe rather than an absence they cannot distinguish from never having seen the row.
/// </summary>
public static class SyncOperations
{
    /// <summary>Insert or update the row carried in the payload.</summary>
    public const string Upsert = "upsert";

    /// <summary>Soft-delete the row carried in the payload.</summary>
    public const string Delete = "delete";

    private static readonly HashSet<string> KnownOperations = new(StringComparer.Ordinal)
    {
        Upsert,
        Delete
    };

    /// <summary>
    /// Gets a value indicating whether the given operation is understood.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <returns><c>true</c> if the operation is known.</returns>
    public static bool IsKnown(string? operation) =>
        operation is not null && KnownOperations.Contains(operation);
}
