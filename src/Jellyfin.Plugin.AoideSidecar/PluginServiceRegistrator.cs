using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar;

/// <summary>
/// Registers the sidecar's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(provider =>
        {
            // Deliberately not BasePlugin.DataFolderPath. That property appends the
            // assembly version when the unversioned folder is absent, so it resolves to
            // a different directory after every upgrade — which would silently hand the
            // sidecar an empty database. Clients would not notice: they do not re-push
            // ops they have already marked synced, so the server's copy of a user's
            // curation history would be gone for good and a new device would sync
            // nothing. This path is stable across versions.
            var directory = Path.Combine(
                provider.GetRequiredService<IApplicationPaths>().DataPath,
                "aoide-sidecar");

            return new SyncDatabase(
                Path.Combine(directory, "aoide-sync.db"),
                provider.GetRequiredService<ILogger<SyncDatabase>>());
        });

        serviceCollection.AddSingleton<SyncRepository>();
        serviceCollection.AddSingleton<PlaylistImageRepository>();
        serviceCollection.AddSingleton<Export.PlaylistExporter>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, Export.PlaylistExportTask>();
    }
}
