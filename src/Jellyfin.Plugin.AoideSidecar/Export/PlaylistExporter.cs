using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Export;

/// <summary>
/// What one export run did.
/// </summary>
public class ExportReport
{
    /// <summary>Gets or sets playlists newly created in Jellyfin.</summary>
    [JsonPropertyName("created")]
    public int Created { get; set; }

    /// <summary>Gets or sets pre-existing Jellyfin playlists taken over by name.</summary>
    [JsonPropertyName("adopted")]
    public int Adopted { get; set; }

    /// <summary>Gets or sets exported playlists whose contents were rewritten.</summary>
    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    /// <summary>Gets or sets exported playlists that had not changed.</summary>
    [JsonPropertyName("unchanged")]
    public int Unchanged { get; set; }

    /// <summary>Gets or sets exported playlists removed because the source was deleted.</summary>
    [JsonPropertyName("deleted")]
    public int Deleted { get; set; }

    /// <summary>Gets or sets smart playlists passed over.</summary>
    [JsonPropertyName("skippedSmart")]
    public int SkippedSmart { get; set; }

    /// <summary>Gets or sets member tracks that did not resolve to a Jellyfin item.</summary>
    [JsonPropertyName("unresolvedTracks")]
    public int UnresolvedTracks { get; set; }

    /// <summary>Gets or sets playlists whose Jellyfin cover was set from the blob store.</summary>
    [JsonPropertyName("artworkApplied")]
    public int ArtworkApplied { get; set; }

    /// <summary>
    /// Gets or sets playlists naming an image hash the blob store does not hold —
    /// normally a client that pushed the op before uploading the bytes.
    /// </summary>
    [JsonPropertyName("artworkMissing")]
    public int ArtworkMissing { get; set; }

    /// <summary>Gets or sets anything that went wrong, per playlist.</summary>
    [JsonPropertyName("errors")]
    public IList<string> Errors { get; } = new List<string>();
}

/// <summary>
/// Mirrors the curation store's playlists into Jellyfin's own.
/// </summary>
/// <remarks>
/// <para>
/// Strictly one-way, and structurally so: this reads the op log and writes to Jellyfin,
/// and there is no path by which a Jellyfin playlist becomes an op. That is what makes a
/// feedback loop impossible here rather than merely unlikely. The matching rule lives on
/// the client side — nothing may import Jellyfin playlists automatically, or an exported
/// edit returns as an inbound change and exports again.
/// </para>
/// <para>
/// Every playlist this touches is stamped with <see cref="ProviderKey"/> carrying its
/// Aoide id. That stamp is the guard: it lets an importer tell a mirror from a real
/// Jellyfin playlist, and it is the only thing this will ever delete.
/// </para>
/// </remarks>
public sealed class PlaylistExporter
{
    /// <summary>
    /// The provider id stamped on every playlist this manages.
    /// </summary>
    public const string ProviderKey = "AoideSidecar";

    private readonly SyncRepository _repository;
    private readonly PlaylistImageRepository _images;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<PlaylistExporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExporter"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="images">Artwork storage.</param>
    /// <param name="playlistManager">Jellyfin's playlist manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="paths">Server application paths.</param>
    /// <param name="logger">Logger.</param>
    public PlaylistExporter(
        SyncRepository repository,
        PlaylistImageRepository images,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IApplicationPaths paths,
        ILogger<PlaylistExporter> logger)
    {
        _repository = repository;
        _images = images;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Brings Jellyfin's playlists in line with the curation store.
    /// </summary>
    /// <param name="userId">The user whose playlists to export.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of the run.</returns>
    public async Task<ExportReport> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var report = new ExportReport();

        var ops = await _repository.ReadPlaylistOpsAsync(userId, cancellationToken).ConfigureAwait(false);
        var projected = PlaylistProjection.Build(ops);
        var exported = await _repository.GetExportMapAsync(userId, cancellationToken).ConfigureAwait(false);

        foreach (var playlist in projected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ReconcileAsync(userId, playlist, exported, report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad playlist must not abandon the rest of the run.
                _logger.LogError(ex, "Failed to export playlist {Name} ({Id})", playlist.Name, playlist.Id);
                report.Errors.Add($"{playlist.Name}: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Playlist export for {User}: {Created} created, {Adopted} adopted, {Updated} updated, "
            + "{Unchanged} unchanged, {Deleted} deleted, {Smart} smart skipped, {Missing} tracks unresolved",
            userId,
            report.Created,
            report.Adopted,
            report.Updated,
            report.Unchanged,
            report.Deleted,
            report.SkippedSmart,
            report.UnresolvedTracks);

        return report;
    }

    private async Task ReconcileAsync(
        Guid userId,
        ProjectedPlaylist playlist,
        Dictionary<string, (string JellyfinItemId, string ContentHash)> exported,
        ExportReport report,
        CancellationToken cancellationToken)
    {
        string? mappedItemId = null;
        string? mappedHash = null;
        if (exported.TryGetValue(playlist.Id, out var record))
        {
            mappedItemId = record.JellyfinItemId;
            mappedHash = record.ContentHash;
        }

        // Smart playlists are rules, and Jellyfin has no concept of one. Evaluating them
        // needs track metadata and play-event aggregates the sidecar does not hold, so
        // they stay an Aoide-only feature rather than exporting as a stale snapshot.
        if (playlist.IsSmart)
        {
            report.SkippedSmart++;
            return;
        }

        if (playlist.Deleted)
        {
            if (mappedItemId is not null)
            {
                DeleteExported(userId, mappedItemId, playlist, report);
                await _repository.ForgetExportAsync(userId, playlist.Id, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var trackIds = ResolveTracks(playlist, report);
        var contentHash = HashContents(playlist.Name, trackIds, playlist.ImageHash);

        var target = mappedItemId is not null ? _libraryManager.GetItemById(Guid.Parse(mappedItemId)) : null;

        // The stamp is checked alongside the contents, not just the contents. A playlist
        // that lost its provider id — a metadata refresh, an edit in Jellyfin's UI, an
        // older version of this exporter — would otherwise match on content forever and
        // never be stamped again, and an unstamped playlist is one this will refuse to
        // delete. Verifying it here makes that self-healing rather than permanent.
        if (target is not null
            && string.Equals(mappedHash, contentHash, StringComparison.Ordinal)
            && target.ProviderIds.TryGetValue(ProviderKey, out var currentStamp)
            && string.Equals(currentStamp, playlist.Id, StringComparison.Ordinal))
        {
            report.Unchanged++;
            return;
        }

        var freshlyCreated = false;
        var wasAdopted = false;
        if (target is null)
        {
            // Either never exported, or the Jellyfin side was removed behind our back.
            var adoptedBefore = report.Adopted;
            target = Adopt(userId, playlist, report);
            wasAdopted = report.Adopted > adoptedBefore;

            if (target is null)
            {
                target = await CreateAsync(userId, playlist, trackIds, report).ConfigureAwait(false);
                freshlyCreated = true;
            }
        }

        if (target is null)
        {
            return;
        }

        if (!freshlyCreated)
        {
            // Adopted playlists need this every bit as much as already-mapped ones:
            // adoption hands back the server's playlist under its own name and contents,
            // so without this a rename made in Aoide would never reach Jellyfin.
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = target.Id,
                UserId = userId,
                Name = playlist.Name,
                Ids = trackIds
            }).ConfigureAwait(false);

            if (!wasAdopted)
            {
                report.Updated++;
            }

            // UpdatePlaylist replaces the stored item, which leaves the copy fetched
            // above stale. Stamping that copy would write the provider id onto a version
            // that no longer exists: the stamp silently disappears, taking with it both
            // the importer's guard and the only evidence that authorises a later delete.
            target = _libraryManager.GetItemById(target.Id);
            if (target is null)
            {
                return;
            }
        }

        await StampAsync(target, playlist, cancellationToken).ConfigureAwait(false);

        var missingBefore = report.ArtworkMissing;
        await ApplyArtworkAsync(userId, target, playlist, report, cancellationToken).ConfigureAwait(false);

        // Artwork named but not yet uploaded must not be recorded as settled, or the
        // unchanged-check would skip this playlist forever and the cover would never
        // appear. The marker cannot equal a computed hash, so the next run retries.
        var settled = report.ArtworkMissing == missingBefore
            ? contentHash
            : contentHash + ":pending-artwork";

        await _repository
            .RecordExportAsync(
                userId,
                playlist.Id,
                target.Id.ToString("N", CultureInfo.InvariantCulture),
                settled,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Takes over the Jellyfin playlist this one came from, instead of duplicating it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source id is tried first because it is an identity rather than a guess. It
    /// stays right where a name cannot: two server playlists sharing a name are still
    /// two distinct ids, and renaming the copy in Aoide before the first export no
    /// longer strands the original. Following the recommended migration, every playlist
    /// carries one at the point export is switched on.
    /// </para>
    /// <para>
    /// The name rule remains for playlists created fresh in Aoide, where no source
    /// exists and the name is genuinely the only signal. It fires only on exactly one
    /// unstamped match: zero means there is nothing to adopt, several means the name
    /// identifies nothing, and guessing would put a user's own playlist under our
    /// management. Both fall through to creating a new playlist, the recoverable outcome.
    /// </para>
    /// </remarks>
    private BaseItem? Adopt(Guid userId, ProjectedPlaylist playlist, ExportReport report)
    {
        if (!string.IsNullOrEmpty(playlist.SourceJellyfinId)
            && Guid.TryParse(playlist.SourceJellyfinId, out var sourceId)
            && _libraryManager.GetItemById(sourceId) is Playlist source
            && IsAdoptable(source, playlist))
        {
            _logger.LogInformation(
                "Adopting Jellyfin playlist {Id} as the source of Aoide playlist {Aoide}",
                sourceId,
                playlist.Id);

            report.Adopted++;
            return source;
        }

        var candidates = _playlistManager.GetPlaylists(userId)
            .Where(existing =>
                string.Equals(existing.Name, playlist.Name, StringComparison.OrdinalIgnoreCase)
                && !existing.ProviderIds.ContainsKey(ProviderKey))
            .Take(2)
            .ToList();

        if (candidates.Count != 1)
        {
            return null;
        }

        _logger.LogInformation(
            "Adopting existing Jellyfin playlist {Name} for Aoide playlist {Id}",
            playlist.Name,
            playlist.Id);

        report.Adopted++;
        return candidates[0];
    }

    /// <summary>
    /// Gets a value indicating whether a playlist may be taken over.
    /// </summary>
    /// <remarks>
    /// Free, or already ours. A playlist stamped for a <em>different</em> Aoide id is
    /// refused so two playlists cannot end up fighting over one mirror, each rewriting
    /// it on alternate runs.
    /// </remarks>
    private static bool IsAdoptable(BaseItem item, ProjectedPlaylist playlist) =>
        !item.ProviderIds.TryGetValue(ProviderKey, out var owner)
        || string.Equals(owner, playlist.Id, StringComparison.Ordinal);

    /// <summary>
    /// Points the Jellyfin playlist's primary image at the artwork the op log names.
    /// </summary>
    /// <remarks>
    /// The file is written under the hash, so identical artwork is stored once and an
    /// unchanged image costs nothing on a re-run. A hash the blob store does not yet
    /// hold is counted rather than treated as a failure: it means the client pushed the
    /// playlist before uploading the bytes, which the next run resolves on its own.
    /// </remarks>
    private async Task ApplyArtworkAsync(
        Guid userId,
        BaseItem target,
        ProjectedPlaylist playlist,
        ExportReport report,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(playlist.ImageHash))
        {
            return;
        }

        var image = await _images.GetAsync(userId, playlist.ImageHash, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            report.ArtworkMissing++;
            return;
        }

        var directory = Path.Combine(_paths.DataPath, "aoide-sidecar", "artwork");
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, playlist.ImageHash + ExtensionFor(image.MimeType));
        if (!File.Exists(file))
        {
            await File.WriteAllBytesAsync(file, image.Bytes, cancellationToken).ConfigureAwait(false);
        }

        var current = target.GetImageInfo(ImageType.Primary, 0);
        if (current is not null && string.Equals(current.Path, file, StringComparison.Ordinal))
        {
            return;
        }

        target.SetImage(new ItemImageInfo { Path = file, Type = ImageType.Primary }, 0);
        await _libraryManager
            .UpdateItemAsync(target, target.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken)
            .ConfigureAwait(false);

        report.ArtworkApplied++;
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private async Task<BaseItem?> CreateAsync(
        Guid userId,
        ProjectedPlaylist playlist,
        IReadOnlyList<Guid> trackIds,
        ExportReport report)
    {
        var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = playlist.Name,
            ItemIdList = trackIds,
            UserId = userId,
            MediaType = MediaType.Audio
        }).ConfigureAwait(false);

        report.Created++;
        return _libraryManager.GetItemById(Guid.Parse(result.Id));
    }

    private async Task StampAsync(BaseItem target, ProjectedPlaylist playlist, CancellationToken cancellationToken)
    {
        if (target.ProviderIds.TryGetValue(ProviderKey, out var existing)
            && string.Equals(existing, playlist.Id, StringComparison.Ordinal))
        {
            return;
        }

        target.ProviderIds[ProviderKey] = playlist.Id;
        await _libraryManager
            .UpdateItemAsync(target, target.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
    }

    private void DeleteExported(Guid userId, string jellyfinItemId, ProjectedPlaylist playlist, ExportReport report)
    {
        var item = _libraryManager.GetItemById(Guid.Parse(jellyfinItemId));
        if (item is null)
        {
            return;
        }

        // Refuse to delete anything that is not ours. The stamp is the only evidence
        // that this playlist exists because we put it there.
        if (!item.ProviderIds.ContainsKey(ProviderKey))
        {
            _logger.LogWarning(
                "Not deleting Jellyfin playlist {Name}: it no longer carries the {Key} stamp",
                item.Name,
                ProviderKey);
            return;
        }

        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, notifyParentItem: true);
        report.Deleted++;
        _logger.LogInformation("Removed exported playlist {Name} for user {User}", playlist.Name, userId);
    }

    private IReadOnlyList<Guid> ResolveTracks(ProjectedPlaylist playlist, ExportReport report)
    {
        var resolved = new List<Guid>(playlist.TrackIds.Count);
        foreach (var trackId in playlist.TrackIds)
        {
            // A track that does not resolve is skipped, never treated as a reason to
            // fail: the file may simply be offline, and the curation store still holds
            // the entry so it returns on its own once the id resolves again.
            if (Guid.TryParse(trackId, out var guid) && _libraryManager.GetItemById(guid) is not null)
            {
                resolved.Add(guid);
            }
            else
            {
                report.UnresolvedTracks++;
            }
        }

        return resolved;
    }

    private static string HashContents(string name, IReadOnlyList<Guid> trackIds, string? imageHash)
    {
        var builder = new StringBuilder(name).Append('\n').Append(imageHash).Append('\n');
        foreach (var id in trackIds)
        {
            builder.Append(id.ToString("N", CultureInfo.InvariantCulture)).Append(',');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
