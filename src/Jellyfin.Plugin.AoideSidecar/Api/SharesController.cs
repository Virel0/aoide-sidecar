using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AoideSidecar.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AoideSidecar.Api;

/// <summary>
/// A request to share a playlist with another user on the server.
/// </summary>
public class ShareRequest
{
    /// <summary>Gets or sets the playlist to share.</summary>
    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    /// <summary>Gets or sets the Jellyfin user to share it with.</summary>
    [JsonPropertyName("granteeUserId")]
    public Guid GranteeUserId { get; set; }

    /// <summary>Gets or sets a value indicating whether they may edit it. Defaults to true.</summary>
    [JsonPropertyName("canEdit")]
    public bool? CanEdit { get; set; }
}

/// <summary>
/// A share, as seen from one side or the other.
/// </summary>
public class ShareDto
{
    /// <summary>Gets or sets the shared playlist.</summary>
    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    /// <summary>Gets or sets the owner.</summary>
    [JsonPropertyName("ownerUserId")]
    public string? OwnerUserId { get; set; }

    /// <summary>Gets or sets the owner's display name, when the server still knows it.</summary>
    [JsonPropertyName("ownerName")]
    public string? OwnerName { get; set; }

    /// <summary>Gets or sets the user it is shared with.</summary>
    [JsonPropertyName("granteeUserId")]
    public string? GranteeUserId { get; set; }

    /// <summary>Gets or sets the grantee's display name, when the server still knows it.</summary>
    [JsonPropertyName("granteeName")]
    public string? GranteeName { get; set; }

    /// <summary>Gets or sets a value indicating whether the grantee may edit.</summary>
    [JsonPropertyName("canEdit")]
    public bool CanEdit { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller is the owner.</summary>
    [JsonPropertyName("isOwner")]
    public bool IsOwner { get; set; }

    /// <summary>Gets or sets when the share was created.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }
}

/// <summary>
/// Collaborative playlists: inviting another Jellyfin user to one, and revoking it.
/// </summary>
/// <remarks>
/// <para>
/// A playlist belongs to whoever first wrote it, and only that owner may share or revoke
/// it. Once shared for editing, both accounts push ops for it and both receive the
/// other's through their normal pull — no second sync loop, no separate cursor.
/// </para>
/// <para>
/// Ops keep the authorship they were written with rather than being rewritten into the
/// owner's name, so <c>authorUserId</c> on a pulled op says who actually made the change.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("aoide/shares")]
[Produces(MediaTypeNames.Application.Json)]
public class SharesController : ControllerBase
{
    private readonly SharingRepository _sharing;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<SharesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharesController"/> class.
    /// </summary>
    /// <param name="sharing">Playlist ownership and shares.</param>
    /// <param name="userManager">Jellyfin's user manager, for display names.</param>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="logger">Logger.</param>
    public SharesController(
        SharingRepository sharing,
        IUserManager userManager,
        IAuthorizationContext authorizationContext,
        ILogger<SharesController> logger)
    {
        _sharing = sharing;
        _userManager = userManager;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    /// <summary>
    /// Lists every share the caller is party to, as owner or as collaborator.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shares.</returns>
    /// <response code="200">The shares.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ShareDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<ShareDto>>> List(CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var shares = await _sharing.ListSharesAsync(authorization.UserId, cancellationToken).ConfigureAwait(false);

        return Ok(shares.Select(share => new ShareDto
        {
            PlaylistId = share.PlaylistId,
            OwnerUserId = share.OwnerUserId.ToString("N", CultureInfo.InvariantCulture),
            OwnerName = NameOf(share.OwnerUserId),
            GranteeUserId = share.GranteeUserId.ToString("N", CultureInfo.InvariantCulture),
            GranteeName = NameOf(share.GranteeUserId),
            CanEdit = share.CanEdit,
            IsOwner = share.OwnerUserId == authorization.UserId,
            CreatedAt = share.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// Shares one of the caller's playlists with another user.
    /// </summary>
    /// <param name="request">The playlist and who to share it with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">Shared, or an existing share updated.</response>
    /// <response code="400">The request was incomplete, or named a user that does not exist.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="403">The caller does not own the playlist.</response>
    /// <response code="404">Nobody has written that playlist yet.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Share([FromBody] ShareRequest request, CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.PlaylistId))
        {
            return Problem("Missing playlist", "playlistId is required.", StatusCodes.Status400BadRequest);
        }

        if (request.GranteeUserId == Guid.Empty || _userManager.GetUserById(request.GranteeUserId) is null)
        {
            return Problem(
                "Unknown user",
                "granteeUserId must be an existing Jellyfin user on this server.",
                StatusCodes.Status400BadRequest);
        }

        if (request.GranteeUserId == authorization.UserId)
        {
            return Problem(
                "Cannot share with yourself",
                "You already own this playlist.",
                StatusCodes.Status400BadRequest);
        }

        var owner = await _sharing.GetOwnerAsync(request.PlaylistId, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            // Ownership is established by the first push, so a playlist the server has
            // never received cannot be shared — there is nothing yet to share.
            return Problem(
                "Unknown playlist",
                "That playlist has not been synced to the server yet.",
                StatusCodes.Status404NotFound);
        }

        if (owner != authorization.UserId)
        {
            return Problem(
                "Not your playlist",
                "Only the owner can share a playlist.",
                StatusCodes.Status403Forbidden);
        }

        await _sharing
            .ShareAsync(
                request.PlaylistId,
                request.GranteeUserId,
                request.CanEdit ?? true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Playlist {Playlist} shared by {Owner} with {Grantee}",
            request.PlaylistId,
            authorization.UserId,
            request.GranteeUserId);

        return NoContent();
    }

    /// <summary>
    /// Revokes a share.
    /// </summary>
    /// <remarks>
    /// Either side may end it: the owner withdraws access, and the collaborator can walk
    /// away from a playlist they no longer want in their library.
    /// </remarks>
    /// <param name="playlistId">The shared playlist.</param>
    /// <param name="granteeUserId">Whose access to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">Revoked.</response>
    /// <response code="401">The request carried no valid Jellyfin token.</response>
    /// <response code="403">The caller is neither the owner nor the grantee.</response>
    /// <response code="404">No such share.</response>
    [HttpDelete("{playlistId}/{granteeUserId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Revoke(
        string playlistId,
        Guid granteeUserId,
        CancellationToken cancellationToken)
    {
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (authorization.UserId == Guid.Empty)
        {
            return Unauthorized();
        }

        var owner = await _sharing.GetOwnerAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return NotFound();
        }

        if (owner != authorization.UserId && granteeUserId != authorization.UserId)
        {
            return Problem(
                "Not your share",
                "Only the owner or the collaborator can end a share.",
                StatusCodes.Status403Forbidden);
        }

        var removed = await _sharing.RevokeAsync(playlistId, granteeUserId, cancellationToken).ConfigureAwait(false);
        return removed ? NoContent() : NotFound();
    }

    private string? NameOf(Guid userId) => _userManager.GetUserById(userId)?.Username;

    private ObjectResult Problem(string title, string detail, int status) =>
        StatusCode(status, new ProblemDetails { Title = title, Detail = detail, Status = status });
}
