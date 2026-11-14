using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Notifications;

/// <summary>
/// The <c>/api/v1/notifications</c> endpoints (US-23, design 3.11, 4 and 5.10): list notifications, read one, mark one
/// read, and mark all read. Read status belongs to the calling Manager (AC-23.3).
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow and <c>[Authorize]</c>.
/// </para>
/// <para>
/// <c>[Authorize(Roles = Roles.Manager)]</c> is on the class, so it applies to every action in it: PRD 4.2 and
/// assumption 14 let only Managers read notifications, so a Warehouse-only or Production-only user gets <c>403</c> on
/// all four endpoints (AC-23.6), and a caller without a token gets <c>401</c>. Both happen before the action runs, so a
/// user without the role cannot learn which ids exist (design 2.2).
/// </para>
/// <para>
/// AC-23.5: notifications are never deleted or edited. There is no <c>DELETE</c>, <c>PUT</c> or <c>PATCH</c> action,
/// so routing answers those methods with the framework's <c>405</c> (design 4).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/notifications")]
[Authorize(Roles = Roles.Manager)]
public sealed class NotificationsController(NotificationService notifications) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/notifications</c>: one page of notifications, newest first, each with <c>isRead</c> for the
    /// caller (AC-23.1, design 5.10). <c>?unreadOnly=true</c> lists only the ones the caller has not read; its
    /// <c>totalCount</c> is the caller's unread count (design 3.11). <c>400</c> for bad paging or an
    /// <c>unreadOnly</c> other than <c>true</c> or <c>false</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="unreadOnly">Optional, <c>true</c> or <c>false</c> (default). Received as text so the error uses the API's own wording.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    public Task<PagedResult<NotificationResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery(Name = "unreadOnly")] string? unreadOnly, CancellationToken cancellationToken) =>
        notifications.ListAsync(paging, unreadOnly, CallerId, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/notifications/{id}</c>: one notification with <c>isRead</c> for the caller (AC-23.1), or
    /// <c>404</c> "Notification {id} not found." (AC-23.5).
    /// </summary>
    [HttpGet("{id:int}")]
    public Task<NotificationResponse> Get(int id, CancellationToken cancellationToken) =>
        notifications.GetAsync(id, CallerId, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/notifications/{id}/read</c>: marks one notification read for the caller only (AC-23.3).
    /// <c>204</c> with no body, also when it was already read (design 7.2); <c>404</c> for an unknown id.
    /// </summary>
    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken cancellationToken)
    {
        await notifications.MarkReadAsync(id, CallerId, cancellationToken);

        // Design 2.2 and 4: marking read succeeds with 204 No Content; there is nothing to return.
        return NoContent();
    }

    /// <summary>
    /// <c>POST /api/v1/notifications/read-all</c>: marks every current notification read for the caller only (AC-23.3).
    /// <c>204</c> with no body, also when there was nothing unread.
    /// </summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await notifications.MarkAllReadAsync(CallerId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// AC-23.3: read status is the caller's, so the user id always comes from the checked token (its <c>sub</c>
    /// claim), never from the request.
    /// </summary>
    private string CallerId => User.FindFirstValue(JwtTokenService.UserIdClaim)!;
}
