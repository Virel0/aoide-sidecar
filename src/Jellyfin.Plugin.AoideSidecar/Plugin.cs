using Jellyfin.Plugin.AoideSidecar.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AoideSidecar;

/// <summary>
/// The Aoide Sidecar plugin. Hosts the curation-store sync endpoints described in
/// <c>docs/sync-design.md</c>: Jellyfin remains the source of truth for the library,
/// while this plugin relays the append-only op log that carries playlists, folders,
/// likes, play history and queue state between a user's devices.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">Serializer used to persist plugin configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance, or <c>null</c> before the server has constructed it.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Aoide Sidecar";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("959763ae-fc57-4339-b8dc-a9c1800a2883");

    /// <inheritdoc />
    public override string Description =>
        "Sync service for the Aoide curation store: playlists, folders, likes, play history and queue state.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
