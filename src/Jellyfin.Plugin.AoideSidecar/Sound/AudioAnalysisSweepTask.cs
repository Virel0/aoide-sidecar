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
/// choice for the person running the server, not a side effect of a plugin update. One
/// run covers sound bounds, loudness and tempo together, because they come out of the
/// same decode.
/// </remarks>
public class AudioAnalysisSweepTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly AudioAnalysisService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AudioAnalysisSweepTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioAnalysisSweepTask"/> class.
    /// </summary>
    /// <param name="service">The measurement service.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="logger">Logger.</param>
    public AudioAnalysisSweepTask(AudioAnalysisService service, ILibraryManager libraryManager, ILogger<AudioAnalysisSweepTask> logger)
    {
        _service = service;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Analyse the library's audio";

    /// <inheritdoc />
    /// <remarks>
    /// Unchanged from when this only measured sound bounds. Jellyfin stores a task's
    /// triggers and its enabled state against this key, so renaming it would silently
    /// discard whatever schedule the server's owner had set.
    /// </remarks>
    public string Key => "AoideSidecarSoundBoundsSweep";

    /// <inheritdoc />
    public string Description =>
        "Measures where each track's sound starts and stops, how loud it is, how fast, and where its beats fall, "
        + "so clients can trim silence, level playback across a library mastered decades apart, and mix one track "
        + "into the next. Decodes every track once; a large library takes hours the first time.";

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

        // Lookup queues whatever is missing or stale and reports the rest as current. Both
        // caches are consulted because either can be the stale one, and a track queued
        // twice is queued once: the job is keyed by id and fills both.
        var queued = 0;
        const int batch = 500;
        for (var i = 0; i < tracks.Count; i += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = tracks.Skip(i).Take(batch).ToList();
            var bounds = await _service.LookupAsync(slice, cancellationToken).ConfigureAwait(false);
            var analysis = await _service.LookupAnalysisAsync(slice, cancellationToken).ConfigureAwait(false);
            queued += slice.Count(t =>
                (bounds.TryGetValue(t.Id, out var b) && b.Pending)
                || (analysis.TryGetValue(t.Id, out var a) && a.Pending));
            progress.Report(Math.Min(50, (i + batch) * 50.0 / tracks.Count));
        }

        _logger.LogInformation("Audio analysis sweep: {Queued} of {Total} tracks queued for measurement", queued, tracks.Count);

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
