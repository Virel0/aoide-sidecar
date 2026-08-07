using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Data;

namespace Jellyfin.Plugin.AoideSidecar.Export;

/// <summary>
/// A stored blob that no live playlist refers to.
/// </summary>
public class OrphanImageDto
{
    /// <summary>Gets or sets the blob's content hash.</summary>
    [JsonPropertyName("imageHash")]
    public string? ImageHash { get; set; }

    /// <summary>Gets or sets its size on disk.</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets whole days since it was stored.</summary>
    [JsonPropertyName("ageDays")]
    public int AgeDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is old enough to reclaim.
    /// Orphans younger than the grace period are still reported, so that a cover
    /// uploaded moments ago can be seen to have arrived.
    /// </summary>
    [JsonPropertyName("reclaimable")]
    public bool Reclaimable { get; set; }
}

/// <summary>
/// What the artwork store holds, and how much of it nothing points at.
/// </summary>
public class OrphanReportDto
{
    /// <summary>Gets or sets every blob stored for the caller.</summary>
    [JsonPropertyName("totalBlobs")]
    public int TotalBlobs { get; set; }

    /// <summary>Gets or sets the total bytes held.</summary>
    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the unreferenced blobs, oldest first.</summary>
    [JsonPropertyName("orphans")]
    public IReadOnlyList<OrphanImageDto> Orphans { get; set; } = Array.Empty<OrphanImageDto>();

    /// <summary>Gets or sets the bytes held by unreferenced blobs.</summary>
    [JsonPropertyName("orphanBytes")]
    public long OrphanBytes { get; set; }

    /// <summary>Gets or sets the age, in days, a blob must reach before it may be reclaimed.</summary>
    [JsonPropertyName("graceDays")]
    public int GraceDays { get; set; }

    /// <summary>Gets or sets how many blobs this call actually deleted. Zero on a report.</summary>
    [JsonPropertyName("reclaimed")]
    public int Reclaimed { get; set; }

    /// <summary>Gets or sets how many bytes this call actually freed. Zero on a report.</summary>
    [JsonPropertyName("reclaimedBytes")]
    public long ReclaimedBytes { get; set; }
}

/// <summary>
/// Works out which stored images nothing refers to any more.
/// </summary>
/// <remarks>
/// <para>
/// Reporting and reclaiming are separate on purpose. A blob that looks unreferenced is
/// not always safe to delete: the contract has clients upload bytes <em>before</em>
/// pushing the playlist row that names them, so between those two steps a blob is
/// genuinely unreferenced and genuinely still needed. A device that uploaded, went
/// offline, and returns weeks later with its op still queued is the same case stretched
/// out. The grace period, measured from when the blob was stored, is what covers it.
/// </para>
/// <para>
/// The asymmetry decides how generous that period should be: a wrongly reclaimed blob is
/// a permanently broken cover on every device, and a retained one is a few hundred
/// kilobytes. Keeping is the cheaper mistake by orders of magnitude.
/// </para>
/// </remarks>
public static class ArtworkSweep
{
    private const long MillisecondsPerDay = 86_400_000;

    /// <summary>
    /// Builds a report over the caller's blobs.
    /// </summary>
    /// <param name="blobs">Everything stored for the user.</param>
    /// <param name="referenced">Hashes named by live playlists, compared case-insensitively.</param>
    /// <param name="nowMs">Current time, milliseconds since epoch.</param>
    /// <param name="graceDays">Minimum age before a blob may be reclaimed.</param>
    /// <returns>The report, with orphans oldest first.</returns>
    public static OrphanReportDto Build(
        IReadOnlyList<StoredImageInfo> blobs,
        ISet<string> referenced,
        long nowMs,
        int graceDays)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(referenced);

        var orphans = new List<OrphanImageDto>();
        long orphanBytes = 0;
        long totalBytes = 0;

        foreach (var blob in blobs)
        {
            totalBytes += blob.SizeBytes;

            if (referenced.Contains(blob.ImageHash))
            {
                continue;
            }

            // Clamped at zero: a clock that has moved backwards since the upload would
            // otherwise produce a negative age, and a negative age is not old enough for
            // anything. Failing towards "too young to reclaim" is the safe direction.
            var ageDays = (int)Math.Max(0, (nowMs - blob.CreatedAt) / MillisecondsPerDay);

            orphans.Add(new OrphanImageDto
            {
                ImageHash = blob.ImageHash,
                SizeBytes = blob.SizeBytes,
                AgeDays = ageDays,
                Reclaimable = ageDays >= graceDays
            });

            orphanBytes += blob.SizeBytes;
        }

        return new OrphanReportDto
        {
            TotalBlobs = blobs.Count,
            TotalBytes = totalBytes,
            Orphans = orphans.OrderByDescending(o => o.AgeDays).ToList(),
            OrphanBytes = orphanBytes,
            GraceDays = graceDays
        };
    }

    /// <summary>
    /// Collects the image hashes that live playlists still name.
    /// </summary>
    /// <remarks>
    /// Taken from the projection rather than raw payloads, so this shares the collapsing
    /// rules and the both-naming-conventions reading that export already relies on. A
    /// hand-rolled payload read here would reintroduce exactly the silent miss that made
    /// export emit empty playlists before 1.3.1.0 — except the failure would be deleting
    /// artwork that is still in use.
    /// <para>
    /// Soft-deleted playlists do not count. Their covers really are unreferenced, and it
    /// is the grace period, not the tombstone, that protects them from a hasty sweep.
    /// </para>
    /// </remarks>
    /// <param name="playlists">The projected playlists.</param>
    /// <returns>Referenced hashes, compared case-insensitively.</returns>
    public static HashSet<string> ReferencedHashes(IEnumerable<ProjectedPlaylist> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in playlists)
        {
            if (!playlist.Deleted && !string.IsNullOrEmpty(playlist.ImageHash))
            {
                referenced.Add(playlist.ImageHash);
            }
        }

        return referenced;
    }
}
