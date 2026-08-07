using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Jellyfin.Plugin.AoideSidecar.Configuration;
using Jellyfin.Plugin.AoideSidecar.Data;
using Jellyfin.Plugin.AoideSidecar.Sync;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// The two endpoints of the sync contract.
/// </summary>
/// <remarks>
/// Authentication is Jellyfin's own. Running in-process means the authenticated user
/// arrives with the request instead of costing a round-trip to <c>/Users/Me</c>, and it
/// means the sidecar never runs an account system of its own — a second set of
/// credentials would hand it a password database it has no business owning.
/// </remarks>
[ApiController]

// Bare [Authorize] on purpose. Jellyfin 10.10 configures a DefaultPolicy requiring its
// CustomAuthentication scheme, which is what core controllers rely on; there is no
// "DefaultAuthorization" named policy in this version, and asking for one by name
// throws at request time rather than at startup.
[Authorize]
[Route("aoide/sync")]
[Produces(MediaTypeNames.Application.Json)]
public class SyncController : ControllerBase
{
    private readonly SyncRepository _repository;
    private readonly SharingRepository _sharing;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="sharing">Playlist ownership and shares.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public SyncController(
        SyncRepository repository,
        SharingRepository sharing,
        IAuthorizationContext authorizationContext,
        ILogger<SyncController> logger)
    {
        _repository = repository;
        _sharing = sharing;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    /// <remarks>
    /// The body is parsed here rather than by model binding so that MaxDepth is ours to
    /// set. It sits well above the per-op depth limit on purpose: an over-nested op then
    /// reaches the validator and is refused individually with a reason, instead of
    /// throwing during binding and failing the whole batch — which would wedge a
    /// client's outbound queue for one bad row, exactly what `rejected` exists to avoid.
    /// It stays bounded so a hostile body still cannot run the parser away.
    /// </remarks>
    private static readonly JsonSerializerOptions PushJsonOptions =
        new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Appends a batch of ops to the caller's log.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accepted op ids, any rejections, and the caller's head sequence.</returns>
    /// <response code="200">Ops processed; check <c>accepted</c> for what was stored.</response>
    /// <response code="400">The batch was malformed or larger than the configured limit.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpPost("push")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(PushResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PushResponse>> Push(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        PushRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<PushRequest>(Request.Body, PushJsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Malformed request body",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var configuration = Configuration;
        var ops = request?.Ops ?? Array.Empty<SyncOpDto>();
        var limits = new OpLimits(configuration.MaxPayloadBytes, configuration.MaxPayloadDepth);

        if (ops.Count > configuration.MaxOpsPerPush)
        {
            // A hard rejection rather than a partial accept: the client can simply
            // split the batch, so there is no data to lose and no queue to wedge.
            return BadRequest(new ProblemDetails
            {
                Title = "Batch too large",
                Detail = $"{ops.Count} ops exceeds the limit of {configuration.MaxOpsPerPush}. Split the batch.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // The body's device id is the curation store's own, which is the one that has to
        // match origin_device. Jellyfin's is a reasonable fallback and never empty.
        var deviceId = !string.IsNullOrWhiteSpace(request?.DeviceId)
            ? request.DeviceId
            : authorization.DeviceId;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing device id",
                Detail = "deviceId is required; it is how another device recognises whose ops these are.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var valid = new List<SyncOpDto>(ops.Count);
        var accepted = new List<string>(ops.Count);
        var rejected = new List<RejectedOpDto>();

        foreach (var op in ops)
        {
            if (OpValidator.TryValidate(op, limits, out var reason))
            {
                valid.Add(op);
                accepted.Add(op.OpId!);
            }
            else
            {
                // Reported per-op rather than failing the batch. A malformed op is a
                // client bug; failing the whole push would stall every good op behind
                // it forever, and dropping it silently would lose it without a trace.
                rejected.Add(new RejectedOpDto { OpId = op?.OpId, Reason = reason });
            }
        }

        if (rejected.Count > 0)
        {
            _logger.LogWarning(
                "Rejected {Count} of {Total} ops from device {DeviceId}: {Reason}",
                rejected.Count,
                ops.Count,
                deviceId,
                rejected[0].Reason);
        }

        // Playlist-scoped ops may only be written by the owner, by someone the owner has
        // granted edit access, or by whoever creates the playlist in the first place.
        // Refused individually, like any other invalid op, so one denied edit cannot
        // stall the rest of a device's queue.
        var playlists = valid
            .Select(SharingRepository.PlaylistIdOf)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (playlists.Count > 0)
        {
            HashSet<string> writable;
            try
            {
                writable = await _sharing
                    .GetWritableAsync(playlists, authorization.UserId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return StorageFailure(ex, "push");
            }

            for (var i = valid.Count - 1; i >= 0; i--)
            {
                var playlistId = SharingRepository.PlaylistIdOf(valid[i]);
                if (playlistId is null || writable.Contains(playlistId))
                {
                    continue;
                }

                rejected.Add(new RejectedOpDto
                {
                    OpId = valid[i].OpId,
                    Reason = $"Playlist '{playlistId}' belongs to another user and is not shared with you for editing."
                });

                accepted.Remove(valid[i].OpId!);
                valid.RemoveAt(i);
            }

            try
            {
                await _sharing
                    .ClaimOwnershipAsync(writable, authorization.UserId, UnixNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return StorageFailure(ex, "push");
            }
        }

        long cursor;
        try
        {
            cursor = await _repository
                .AppendAsync(authorization.UserId, deviceId, valid, UnixNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StorageFailure(ex, "push");
        }

        // Queue state is one row per device, replaced whole on every move of playback.
        // Left alone it would append thousands of superseded rows a day; the losers carry
        // nothing, since an overwritten snapshot of where a device was is not history.
        var queueDevices = valid
            .Where(op => op.Entity == SyncEntities.QueueState && !string.IsNullOrEmpty(op.EntityId))
            .Select(op => op.EntityId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (queueDevices.Count > 0)
        {
            try
            {
                await _repository
                    .CompactQueueStateAsync(authorization.UserId, queueDevices, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The ops are already durably stored, so a failure to tidy up must not be
                // reported as a failed push — the client would resend what already landed.
                _logger.LogWarning(ex, "Could not compact queue state for {User}", authorization.UserId);
            }
        }

        return Ok(new PushResponse
        {
            Accepted = accepted,
            Rejected = rejected,
            Cursor = cursor
        });
    }

    /// <summary>
    /// Reports the sidecar's view of its own storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Database path, schema version, whether writes succeed, and op counts.</returns>
    /// <response code="200">The status. Check <c>writable</c> and <c>error</c>.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SyncStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SyncStatusDto>> Status(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var status = await _repository.GetStatusAsync(authorization.UserId, cancellationToken).ConfigureAwait(false);
        if (!status.Writable)
        {
            _logger.LogError(
                "Aoide sync storage is not writable at {Path}: {Error}",
                status.DatabasePath,
                status.Error ?? "the write probe failed without an exception");
        }

        return Ok(status);
    }

    /// <summary>
    /// Turns a storage exception into something a client and its user can act on.
    /// </summary>
    /// <remarks>
    /// 503 rather than 500 on purpose: a store that cannot be written is usually
    /// transient or fixable, and the client must keep the ops queued and retry rather
    /// than treat them as delivered. The detail is echoed because the alternative — the
    /// host's generic error page — leaves the cause visible only in the server log.
    /// </remarks>
    private ObjectResult StorageFailure(Exception exception, string operation)
    {
        _logger.LogError(
            exception,
            "Aoide sync {Operation} failed against {Path}",
            operation,
            _repository.DatabasePath);

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
        {
            Title = "Sync storage unavailable",
            Detail = $"{exception.GetType().Name}: {exception.Message}",
            Status = StatusCodes.Status503ServiceUnavailable,
            Instance = _repository.DatabasePath
        });
    }

    /// <summary>
    /// Reads ops the caller has not seen yet.
    /// </summary>
    /// <param name="since">Exclusive cursor; omit or pass 0 for a full history sync.</param>
    /// <param name="limit">Maximum ops to return; clamped to the configured ceiling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A batch of ops in sequence order.</returns>
    /// <response code="200">The batch, its cursor, and whether more remain.</response>
    /// <response code="400">The cursor was negative.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet("pull")]
    [ProducesResponseType(typeof(PullResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PullResponse>> Pull(
        [FromQuery] long since,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (since < 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid cursor",
                Detail = "since must be zero or greater.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var configuration = Configuration;
        var effectiveLimit = Math.Clamp(
            limit ?? configuration.DefaultPullLimit,
            1,
            configuration.MaxPullLimit);

        try
        {
            var response = await _repository
                .ReadAsync(authorization.UserId, since, effectiveLimit, cancellationToken)
                .ConfigureAwait(false);

            // Remember how far this device has read. Nothing in the sync contract needs
            // it, but retention does: without a record of who has seen what, trimming
            // old history would destroy it for any device that had not caught up.
            if (!string.IsNullOrWhiteSpace(authorization.DeviceId))
            {
                await _repository
                    .RecordDeviceCursorAsync(
                        authorization.UserId,
                        authorization.DeviceId,
                        response.Cursor,
                        UnixNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Ok(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StorageFailure(ex, "pull");
        }
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
