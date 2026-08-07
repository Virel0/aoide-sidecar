using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// What one device is playing.
/// </summary>
public class DeviceQueueDto
{
    /// <summary>Gets or sets the device the queue belongs to.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the device asking.</summary>
    [JsonPropertyName("isCurrentDevice")]
    public bool IsCurrentDevice { get; set; }

    /// <summary>Gets or sets whole seconds since the device last reported.</summary>
    [JsonPropertyName("ageSeconds")]
    public long AgeSeconds { get; set; }

    /// <summary>Gets or sets the server's receipt time for this state.</summary>
    [JsonPropertyName("receivedAt")]
    public long ReceivedAt { get; set; }

    /// <summary>Gets or sets the client's own clock reading for this state.</summary>
    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the queue_state row exactly as the device wrote it — track ids,
    /// position, elapsed time, device name. Stored verbatim and never interpreted.
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

/// <summary>
/// Where playback currently is, on every device the user owns.
/// </summary>
/// <remarks>
/// <para>
/// The same <c>queue_state</c> rows the sync log already carries, read directly rather
/// than reconstructed from a pull. Picking up on the desktop what the phone was playing
/// is a foreground action with someone waiting on it; walking the op log to answer it
/// would make handover as slow as a full sync.
/// </para>
/// <para>
/// Superseded rows are compacted away on push, so this reads one row per device however
/// long playback has been running.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/queue")]
[Produces(MediaTypeNames.Application.Json)]
public class QueueController : ControllerBase
{
    private readonly SyncRepository _repository;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueController"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    public QueueController(SyncRepository repository, IAuthorizationContext authorizationContext)
    {
        _repository = repository;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Lists the current queue on each of the caller's devices, most recent first.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per device.</returns>
    /// <response code="200">The queues.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceQueueDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<DeviceQueueDto>>> Get(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var states = await _repository.GetQueueStatesAsync(authorization.UserId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(states.Select(state => new DeviceQueueDto
        {
            DeviceId = state.EntityId,

            // Matched on the pushing device rather than the row key, because the row key
            // is the client's own device id and the two need not be the same string.
            IsCurrentDevice = string.Equals(state.DeviceId, authorization.DeviceId, StringComparison.Ordinal),

            // From the server's clock on both sides, so a device with a wrong clock
            // cannot report itself as the freshest queue and win the handover.
            AgeSeconds = Math.Max(0, (now - state.ReceivedAt) / 1000),
            ReceivedAt = state.ReceivedAt,
            UpdatedAt = state.CreatedAt,
            Payload = state.Payload
        }).ToList());
    }
}
