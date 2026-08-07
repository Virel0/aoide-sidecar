using System.Net.Mime;
using Jellyfin.Plugin.AoideSidecar.Export;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// Mirrors the caller's Aoide playlists into Jellyfin's own.
/// </summary>
/// <remarks>
/// One-way and user-initiated. Nothing runs on a timer, so an export only ever happens
/// because someone asked for one.
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/export")]
[Produces(MediaTypeNames.Application.Json)]
public class ExportController : ControllerBase
{
    private readonly PlaylistExporter _exporter;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<ExportController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportController"/> class.
    /// </summary>
    /// <param name="exporter">The playlist exporter.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public ExportController(
        PlaylistExporter exporter,
        IAuthorizationContext authorizationContext,
        ILogger<ExportController> logger)
    {
        _exporter = exporter;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    /// <summary>
    /// Runs a playlist export for the calling user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    /// <response code="200">The export completed; check <c>errors</c> for per-playlist failures.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="503">The op log could not be read.</response>
    [HttpPost("playlists")]
    [ProducesResponseType(typeof(ExportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ExportReport>> ExportPlaylists(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        try
        {
            var report = await _exporter.ExportAsync(authorization.UserId, cancellationToken).ConfigureAwait(false);
            return Ok(report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Playlist export failed for {User}", authorization.UserId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Export failed",
                Detail = $"{ex.GetType().Name}: {ex.Message}",
                Status = StatusCodes.Status503ServiceUnavailable
            });
        }
    }
}
