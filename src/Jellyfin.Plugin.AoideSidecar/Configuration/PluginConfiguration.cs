using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AoideSidecar.Configuration;

/// <summary>
/// Operational limits for the sync endpoints. These are guard rails against a
/// misbehaving client, not tuning knobs — the defaults suit a household-sized server.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the largest number of ops accepted in a single push.
    /// A client with a long offline backlog is expected to push in several batches.
    /// </summary>
    public int MaxOpsPerPush { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the largest accepted size, in bytes, of a single op payload.
    /// A queue_state row holding a very long queue is the realistic upper bound.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Gets or sets the deepest payload nesting accepted on push.
    /// </summary>
    /// <remarks>
    /// A stored payload is echoed to every other device on pull, and JSON decoders
    /// reject an over-nested document whole rather than element by element — so a single
    /// pathological row would wedge inbound sync everywhere, with no seam for a client to
    /// skip it. Bounding it here is the only place that can be defended. Real rows nest a
    /// handful of levels; nested smart-playlist rule groups are the deepest legitimate
    /// case, so 32 is generous.
    /// </remarks>
    public int MaxPayloadDepth { get; set; } = 32;

    /// <summary>
    /// Gets or sets the largest accepted playlist image, in bytes.
    /// </summary>
    /// <remarks>
    /// Artwork is stored whole and served back verbatim, so this is a real ceiling on
    /// what one playlist costs. Cover art at a sensible resolution lands well under it.
    /// </remarks>
    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the number of ops returned by a pull that does not specify a limit.
    /// </summary>
    public int DefaultPullLimit { get; set; } = 500;

    /// <summary>
    /// Gets or sets the ceiling applied to a client-supplied pull limit.
    /// </summary>
    public int MaxPullLimit { get; set; } = 1000;
}
