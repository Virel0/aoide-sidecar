using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Sync;

namespace Jellyfin.Plugin.AoideSidecar.Export;

/// <summary>
/// A playlist as the op log currently describes it.
/// </summary>
public sealed class ProjectedPlaylist
{
    /// <summary>Gets the curation-store playlist id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the playlist name, or an empty string if the rows never carried one.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a value indicating whether this is a smart playlist.</summary>
    public bool IsSmart { get; init; }

    /// <summary>Gets a value indicating whether the playlist has been soft-deleted.</summary>
    public bool Deleted { get; init; }

    /// <summary>Gets the member track ids, as Jellyfin item ids, in playlist order.</summary>
    public required IReadOnlyList<string> TrackIds { get; init; }
}

/// <summary>
/// Collapses the op log into the current state of each playlist.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place the sidecar reads inside a payload. Storage and relay stay
/// strictly opaque; export is a downstream consumer that has to understand what it is
/// exporting. It reads defensively for that reason — a row missing the fields it expects
/// is skipped rather than allowed to fail the run, because a client is free to add or
/// rename fields without telling the server.
/// </para>
/// <para>
/// Rows are collapsed with the same rule clients use: highest <c>updated_at</c> wins,
/// with the server sequence as the tiebreak. Sequence alone would be wrong — a device
/// that edits offline and syncs an hour later lands a higher sequence than an edit made
/// after it, and would otherwise overwrite the newer value.
/// </para>
/// </remarks>
public static class PlaylistProjection
{
    /// <summary>
    /// Builds current playlist state from a user's ops.
    /// </summary>
    /// <param name="ops">Playlist and playlist-item ops, in ascending sequence order.</param>
    /// <returns>Every known playlist, including soft-deleted ones so callers can clean up.</returns>
    public static IReadOnlyList<ProjectedPlaylist> Build(IEnumerable<SyncOpDto> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var playlists = new Dictionary<string, SyncOpDto>(StringComparer.Ordinal);
        var items = new Dictionary<string, SyncOpDto>(StringComparer.Ordinal);

        foreach (var op in ops)
        {
            if (string.IsNullOrEmpty(op.EntityId) || op.Payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var target = op.Entity switch
            {
                SyncEntities.Playlists => playlists,
                SyncEntities.PlaylistItems => items,
                _ => null
            };

            if (target is null)
            {
                continue;
            }

            if (!target.TryGetValue(op.EntityId, out var winner) || Wins(op, winner))
            {
                target[op.EntityId] = op;
            }
        }

        // Group surviving members by playlist, ordered by fractional index. The indices
        // are sortable strings by construction, so ordinal comparison is the whole sort.
        var membership = new Dictionary<string, List<(string Position, string TrackId)>>(StringComparer.Ordinal);
        foreach (var op in items.Values)
        {
            if (IsDeleted(op))
            {
                continue;
            }

            var playlistId = ReadString(op.Payload, "playlist_id");
            var trackId = ReadString(op.Payload, "jellyfin_id");
            if (playlistId is null || string.IsNullOrEmpty(trackId))
            {
                continue;
            }

            var position = ReadString(op.Payload, "position") ?? string.Empty;
            if (!membership.TryGetValue(playlistId, out var list))
            {
                list = new List<(string, string)>();
                membership[playlistId] = list;
            }

            list.Add((position, trackId));
        }

        var results = new List<ProjectedPlaylist>(playlists.Count);
        foreach (var (id, op) in playlists)
        {
            var tracks = membership.TryGetValue(id, out var list)
                ? list.OrderBy(entry => entry.Position, StringComparer.Ordinal)
                    .Select(entry => entry.TrackId)
                    .ToList()
                : new List<string>();

            results.Add(new ProjectedPlaylist
            {
                Id = id,
                Name = ReadString(op.Payload, "name") ?? string.Empty,
                IsSmart = ReadBool(op.Payload, "is_smart"),
                Deleted = IsDeleted(op),
                TrackIds = tracks
            });
        }

        return results;
    }

    private static bool Wins(SyncOpDto candidate, SyncOpDto incumbent)
    {
        var candidateStamp = Stamp(candidate);
        var incumbentStamp = Stamp(incumbent);

        return candidateStamp != incumbentStamp
            ? candidateStamp > incumbentStamp
            : candidate.Seq > incumbent.Seq;
    }

    private static long Stamp(SyncOpDto op)
    {
        if (op.Payload.ValueKind == JsonValueKind.Object
            && op.Payload.TryGetProperty("updated_at", out var updated)
            && updated.ValueKind == JsonValueKind.Number
            && updated.TryGetInt64(out var value))
        {
            return value;
        }

        return op.CreatedAt;
    }

    private static bool IsDeleted(SyncOpDto op) =>
        string.Equals(op.Operation, SyncOperations.Delete, StringComparison.Ordinal)
        || ReadBool(op.Payload, "deleted");

    private static string? ReadString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    // Clients have written these as 0/1 and as true/false at different times; both are
    // the same fact, so both are accepted rather than one being silently ignored.
    private static bool ReadBool(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed)
                ? parsed
                : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0,
            _ => false
        };
    }
}
