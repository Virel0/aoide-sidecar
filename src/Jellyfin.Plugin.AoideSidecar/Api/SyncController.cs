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
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="repository">The op log.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public SyncController(
        SyncRepository repository,
        IAuthorizationContext authorizationContext,
        ILogger<SyncController> logger)
    {
        _repository = repository;
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

        var cursor = await _repository
            .AppendAsync(authorization.UserId, deviceId, valid, UnixNow(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new PushResponse
        {
            Accepted = accepted,
            Rejected = rejected,
            Cursor = cursor
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

        var response = await _repository
            .ReadAsync(authorization.UserId, since, effectiveLimit, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
