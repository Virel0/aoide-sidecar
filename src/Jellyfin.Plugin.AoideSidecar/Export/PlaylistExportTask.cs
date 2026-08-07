using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Export;

/// <summary>
/// Runs the one-way playlist export for every user who has ever synced.
/// </summary>
/// <remarks>
/// Export is convergent and cheap when nothing has changed — an untouched playlist is
/// recognised by its content hash and skipped — so running it on a timer costs little
/// and keeps Jellyfin's view current without anyone asking.
/// </remarks>
public class PlaylistExportTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SyncRepository _repository;
    private readonly PlaylistExporter _exporter;
    private readonly ILogger<PlaylistExportTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExportTask"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="exporter">The playlist exporter.</param>
    /// <param name="logger">Logger.</param>
    public PlaylistExportTask(
        SyncRepository repository,
        PlaylistExporter exporter,
        ILogger<PlaylistExportTask> logger)
    {
        _repository = repository;
        _exporter = exporter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Export Aoide playlists to Jellyfin";

    /// <inheritdoc />
    public string Key => "AoideSidecarPlaylistExport";

    /// <inheritdoc />
    public string Description =>
        "Mirrors each user's Aoide playlists into their Jellyfin playlists. One-way: "
        + "playlists edited in Jellyfin are overwritten from Aoide. Smart playlists are skipped.";

    /// <inheritdoc />
    public string Category => "Aoide Sidecar";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration.EnableScheduledExport ?? false;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Nightly rather than hourly. Export only has something to do after a device
        // syncs a playlist change, and a run that finds nothing still walks every
        // playlist's membership.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var users = await _repository.ListUsersAsync(cancellationToken).ConfigureAwait(false);
        if (users.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var done = 0;
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var report = await _exporter.ExportAsync(user, cancellationToken).ConfigureAwait(false);
                if (report.Errors.Count > 0)
                {
                    _logger.LogWarning(
                        "Playlist export for {User} finished with {Count} errors; first: {Error}",
                        user,
                        report.Errors.Count,
                        report.Errors[0]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's failure must not stop the others from being exported.
                _logger.LogError(ex, "Playlist export failed for {User}", user);
            }

            done++;
            progress.Report(done * 100.0 / users.Count);
        }
    }
}
