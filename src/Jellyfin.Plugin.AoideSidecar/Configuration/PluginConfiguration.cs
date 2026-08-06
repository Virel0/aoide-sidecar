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
    /// Gets or sets the number of ops returned by a pull that does not specify a limit.
    /// </summary>
    public int DefaultPullLimit { get; set; } = 500;

    /// <summary>
    /// Gets or sets the ceiling applied to a client-supplied pull limit.
    /// </summary>
    public int MaxPullLimit { get; set; } = 1000;
}
