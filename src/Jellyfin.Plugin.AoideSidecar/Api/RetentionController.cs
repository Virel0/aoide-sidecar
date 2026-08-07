using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Configuration;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// One device's pull progress.
/// </summary>
public class DeviceCursorDto
{
    /// <summary>Gets or sets the device id.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the highest sequence it has pulled.</summary>
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    /// <summary>Gets or sets whole days since it last pulled.</summary>
    [JsonPropertyName("lastSeenDays")]
    public int LastSeenDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this device still counts.
    /// A device silent longer than the retention window is excluded from the safe
    /// cursor, so one retired phone cannot block pruning forever.
    /// </summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

/// <summary>
/// What the log holds and how much of it is safe to drop.
/// </summary>
public class RetentionReportDto
{
    /// <summary>Gets or sets op counts per entity.</summary>
    [JsonPropertyName("opsByEntity")]
    public IReadOnlyDictionary<string, long> OpsByEntity { get; set; } = new Dictionary<string, long>();

    /// <summary>Gets or sets the caller's head sequence.</summary>
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    /// <summary>Gets or sets every device that has ever pulled.</summary>
    [JsonPropertyName("devices")]
    public IReadOnlyList<DeviceCursorDto> Devices { get; set; } = Array.Empty<DeviceCursorDto>();

    /// <summary>
    /// Gets or sets the highest sequence every active device has pulled past.
    /// Zero when no device has reported one, which makes nothing prunable.
    /// </summary>
    [JsonPropertyName("safeCursor")]
    public long SafeCursor { get; set; }

    /// <summary>Gets or sets the retention age, in days.</summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; }

    /// <summary>Gets or sets play-history ops that are both seen by everyone and old enough.</summary>
    [JsonPropertyName("prunablePlayEvents")]
    public long PrunablePlayEvents { get; set; }

    /// <summary>Gets or sets how many ops this call actually deleted. Zero on a report.</summary>
    [JsonPropertyName("pruned")]
    public long Pruned { get; set; }
}

/// <summary>
/// Reports and trims play history.
/// </summary>
/// <remarks>
/// <para>
/// The op log grows forever, and <c>play_events</c> is the only table that grows without
/// a ceiling. Trimming it is safe only for history every device has already collected,
/// which is why the server now records each device's pull cursor: "old enough to delete"
/// alone would destroy history for a device that had not read it yet.
/// </para>
/// <para>
/// A device that has never synced at all still gets a shortened history after a prune —
/// no cursor can protect a device the server has never met. That is the trade being made,
/// and it is why nothing here runs on a schedule.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/retention")]
[Produces(MediaTypeNames.Application.Json)]
public class RetentionController : ControllerBase
{
    private const long MillisecondsPerDay = 86_400_000;

    private readonly SyncRepository _repository;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<RetentionController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionController"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public RetentionController(
        SyncRepository repository,
        IAuthorizationContext authorizationContext,
        ILogger<RetentionController> logger)
    {
        _repository = repository;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Reports log size, device progress, and what could be pruned. Deletes nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    /// <response code="200">The report.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(RetentionReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RetentionReportDto>> Report(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(await SurveyAsync(authorization.UserId, Configuration.PlayEventRetentionDays, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes play history every active device has already pulled and that has aged out.
    /// </summary>
    /// <param name="olderThanDays">Minimum age to delete; clamped upward to the configured retention.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, with what was actually removed.</returns>
    /// <response code="200">The prune ran; see <c>pruned</c>.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost("prune")]
    [ProducesResponseType(typeof(RetentionReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RetentionReportDto>> Prune(
        [FromQuery] int? olderThanDays,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        // As with artwork, the parameter may only make the sweep more cautious.
        var days = Math.Max(olderThanDays ?? Configuration.PlayEventRetentionDays, Configuration.PlayEventRetentionDays);
        var report = await SurveyAsync(authorization.UserId, days, cancellationToken).ConfigureAwait(false);

        if (report.PrunablePlayEvents == 0)
        {
            return Ok(report);
        }

        report.Pruned = await _repository
            .PrunablePlayEventsAsync(
                authorization.UserId,
                report.SafeCursor,
                UtcNow() - (days * MillisecondsPerDay),
                delete: true,
                cancellationToken)
            .ConfigureAwait(false);

        report.PrunablePlayEvents -= report.Pruned;

        _logger.LogInformation(
            "Pruned {Count} play events older than {Days} days and below cursor {Cursor} for {User}",
            report.Pruned,
            days,
            report.SafeCursor,
            authorization.UserId);

        return Ok(report);
    }

    private async Task<RetentionReportDto> SurveyAsync(Guid userId, int days, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var counts = await _repository.CountByEntityAsync(userId, cancellationToken).ConfigureAwait(false);
        var cursors = await _repository.GetDeviceCursorsAsync(userId, cancellationToken).ConfigureAwait(false);

        var devices = cursors
            .Select(c =>
            {
                var lastSeenDays = (int)Math.Max(0, (now - c.UpdatedAt) / MillisecondsPerDay);
                return new DeviceCursorDto
                {
                    DeviceId = c.DeviceId,
                    Cursor = c.Cursor,
                    LastSeenDays = lastSeenDays,
                    Active = lastSeenDays <= days
                };
            })
            .ToList();

        // No device has reported a cursor, so nothing is known to have been read, so
        // nothing is safe. Zero is the correct answer rather than a missing one.
        var active = devices.Where(d => d.Active).ToList();
        var safeCursor = active.Count == 0 ? 0 : active.Min(d => d.Cursor);

        var prunable = safeCursor == 0
            ? 0
            : await _repository
                .PrunablePlayEventsAsync(userId, safeCursor, now - (days * MillisecondsPerDay), delete: false, cancellationToken)
                .ConfigureAwait(false);

        return new RetentionReportDto
        {
            OpsByEntity = counts,
            Cursor = await _repository.GetCursorAsync(userId, cancellationToken).ConfigureAwait(false),
            Devices = devices,
            SafeCursor = safeCursor,
            RetentionDays = days,
            PrunablePlayEvents = prunable
        };
    }

    private static long UtcNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
