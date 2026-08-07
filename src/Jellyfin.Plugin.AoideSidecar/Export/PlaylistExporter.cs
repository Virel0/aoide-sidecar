using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
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
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaylistExporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExporter"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="playlistManager">Jellyfin's playlist manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public PlaylistExporter(
        SyncRepository repository,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        ILogger<PlaylistExporter> logger)
    {
        _repository = repository;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
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
        var contentHash = HashContents(playlist.Name, trackIds);

        var target = mappedItemId is not null ? _libraryManager.GetItemById(Guid.Parse(mappedItemId)) : null;
        if (target is not null && string.Equals(mappedHash, contentHash, StringComparison.Ordinal))
        {
            report.Unchanged++;
            return;
        }

        if (target is null)
        {
            // Either never exported, or the Jellyfin side was removed behind our back.
            target = Adopt(userId, playlist, report) ?? await CreateAsync(userId, playlist, trackIds, report).ConfigureAwait(false);
        }
        else
        {
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = target.Id,
                UserId = userId,
                Name = playlist.Name,
                Ids = trackIds
            }).ConfigureAwait(false);

            report.Updated++;
        }

        if (target is null)
        {
            return;
        }

        await StampAsync(target, playlist, cancellationToken).ConfigureAwait(false);
        await _repository
            .RecordExportAsync(
                userId,
                playlist.Id,
                target.Id.ToString("N", CultureInfo.InvariantCulture),
                contentHash,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Takes over an existing same-named Jellyfin playlist instead of making a duplicate.
    /// </summary>
    /// <remarks>
    /// Only when exactly one unstamped playlist matches. Zero matches means there is
    /// nothing to adopt; more than one means the name does not identify anything, and
    /// guessing would put the user's playlist under our management by accident. Both
    /// fall through to creating a fresh playlist, which is the recoverable outcome.
    /// </remarks>
    private BaseItem? Adopt(Guid userId, ProjectedPlaylist playlist, ExportReport report)
    {
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

    private static string HashContents(string name, IReadOnlyList<Guid> trackIds)
    {
        var builder = new StringBuilder(name).Append('\n');
        foreach (var id in trackIds)
        {
            builder.Append(id.ToString("N", CultureInfo.InvariantCulture)).Append(',');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
