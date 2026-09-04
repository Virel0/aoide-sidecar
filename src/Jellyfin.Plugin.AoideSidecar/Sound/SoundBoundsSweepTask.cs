using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Sound;

/// <summary>
/// Queues every unmeasured track in the library, so most are ready before anyone asks.
/// </summary>
/// <remarks>
/// Off by default: a first run over a large library is hours of decoding, and that is a
/// choice for the person running the server, not a side effect of a plugin update.
/// </remarks>
public class SoundBoundsSweepTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SoundBoundsService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SoundBoundsSweepTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundBoundsSweepTask"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="logger">Logger.</param>
    public SoundBoundsSweepTask(SoundBoundsService service, ILibraryManager libraryManager, ILogger<SoundBoundsSweepTask> logger)
    {
        _service = service;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Measure sound bounds for the library";

    /// <inheritdoc />
    public string Key => "AoideSidecarSoundBoundsSweep";

    /// <inheritdoc />
    public string Description =>
        "Finds where each track's sound starts and stops so clients can trim the silence recordings carry. "
        + "Decodes every track once; a large library takes hours the first time.";

    /// <inheritdoc />
    public string Category => "Aoide Sidecar";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration.EnableSoundBoundsSweep ?? false;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var tracks = _libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Audio }, Recursive = true })
            .OfType<Audio>()
            .Where(a => !string.IsNullOrEmpty(a.Path))
            .Select(a => (Id: a.Id.ToString("N", CultureInfo.InvariantCulture), a.Path))
            .ToList();

        if (tracks.Count == 0)
        {
            progress.Report(100);
            return;
        }

        // Lookup queues whatever is missing or stale and reports the rest as current.
        var queued = 0;
        const int batch = 500;
        for (var i = 0; i < tracks.Count; i += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lookups = await _service.LookupAsync(tracks.Skip(i).Take(batch).ToList(), cancellationToken).ConfigureAwait(false);
            queued += lookups.Values.Count(l => l.Pending);
            progress.Report(Math.Min(50, (i + batch) * 50.0 / tracks.Count));
        }

        _logger.LogInformation("Sound-bounds sweep: {Queued} of {Total} tracks queued for measurement", queued, tracks.Count);

        // Then wait for the queue so the task's progress means something.
        var started = _service.InFlight;
        while (_service.InFlight > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            progress.Report(started == 0 ? 100 : 50 + ((started - _service.InFlight) * 50.0 / started));
        }

        progress.Report(100);
    }
}
