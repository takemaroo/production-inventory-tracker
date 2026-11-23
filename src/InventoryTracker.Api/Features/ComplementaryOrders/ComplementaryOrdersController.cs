using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace InventoryTracker.Api.Features.ComplementaryOrders;

/// <summary>
/// The complementary order endpoints (US-16, US-17, US-18, design 3.7, 4 and 5.8): request one for a running order, list
/// and view them, approve or reject them, and send an approved one. Warehouse, Production and Manager may list and view
/// them (PRD 4.2; (v1.1) a Seller may not, AC-30.6); a Production user or a Manager requests one (AC-16.1, AC-16.6);
/// only a Manager approves or rejects (AC-17.1, AC-17.2, AC-17.4); a Warehouse user or a Manager sends (AC-18.1,
/// AC-18.5).
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>CreatedAtAction</c>. All rules are in <see cref="RequestComplementaryOrderService"/>,
/// <see cref="ComplementaryOrderService"/> and <see cref="ComplementarySendService"/>.
/// </para>
/// <para>
/// (v1.1) Each action names its own roles and the class names none (decision #156). Several <c>[Authorize]</c>
/// attributes on one endpoint combine with AND, so a class-level "Warehouse, Production or Manager" plus approve's
/// "Manager" would still admit only Managers to approve, but a Seller's <c>403</c> there would name the roles of both
/// failed requirements instead of design 7.1's "It needs the Manager role."
/// </para>
/// <para>
/// <b>Two URL shapes</b> (design 2.1). A complementary order cannot exist without its parent, so it is created under it:
/// <c>POST /api/v1/production-orders/12/complementary-orders</c>. Once created it has its own number and is read and acted
/// on at its own URL, <c>/api/v1/complementary-orders/3</c>. The controller's <c>[Route]</c> is that second prefix; the
/// create action's route starts with <c>/</c>, which makes it absolute, so it ignores the controller's prefix.
/// </para>
/// <para>
/// There is no <c>PUT</c>, <c>DELETE</c> or manual cancel: complementary orders are never edited or deleted (PRD section
/// 5), and they are cancelled only automatically, when their parent ends (design 3.1).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/complementary-orders")]
public sealed class ComplementaryOrdersController(
    RequestComplementaryOrderService requests,
    ComplementaryOrderService complementaryOrders,
    ComplementarySendService sends) : ControllerBase
{
    /// <summary>
    /// <c>POST /api/v1/production-orders/{number}/complementary-orders</c>: requests extra material for InProduction order
    /// <c>number</c>, with a reason (AC-16.1, design 5.8). <c>201</c> with the new complementary order, PendingApproval,
    /// and <c>Location: /api/v1/complementary-orders/{n}</c>; a ComplementaryAwaitingApproval notification is created.
    /// <c>404</c> for an unknown order; <c>400</c> <c>validation</c> for a bad reason or bad lines (AC-16.3); <c>409</c>
    /// <c>parent-not-in-production</c> (AC-16.2).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Request a complementary order": Production or Manager. A Warehouse-only user gets <c>403</c> (AC-16.6)
    /// before the order or the body is looked at (design 2.2).
    /// </remarks>
    [HttpPost("/api/v1/production-orders/{number:int}/complementary-orders")]
    [Authorize(Roles = Roles.ProductionOrManager)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`parent-not-in-production`: complementary orders are only for an order in production (AC-16.2).")]
    public async Task<ActionResult<ComplementaryOrderResponse>> Create(
        int number, ComplementaryOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-16.1, AC-05.3: the request records who asked, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var created = await requests.RequestAsync(number, request, userId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { number = created.Number }, created);
    }

    /// <summary>
    /// <c>GET /api/v1/complementary-orders</c>: one page of complementary orders, newest first, optionally filtered with
    /// <c>?status=PendingApproval</c> (any status, any letter case) and <c>?orderNumber=12</c> (design 5.8). An unknown
    /// status, or an order number that is not a whole number, is <c>400</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="status">Optional status filter. Received as text so the error uses the design's wording.</param>
    /// <param name="orderNumber">Optional parent order filter; a number that matches nothing gives an empty page (design 2.5).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <remarks>(v1.1) PRD 4.2, "View ... complementary orders": Warehouse, Production or Manager (AC-30.6).</remarks>
    [HttpGet]
    [Authorize(Roles = Roles.WarehouseProductionOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<ComplementaryOrderResponse>> List(
        [FromQuery] PageQuery paging,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "orderNumber")] int? orderNumber,
        CancellationToken cancellationToken) =>
        complementaryOrders.ListAsync(paging, status, orderNumber, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/complementary-orders/{number}</c>: one complementary order (design 5.8), or <c>404</c>
    /// "Complementary order #{number} not found.".
    /// </summary>
    /// <remarks>(v1.1) Warehouse, Production or Manager, like the list (AC-30.6).</remarks>
    [HttpGet("{number:int}")]
    [Authorize(Roles = Roles.WarehouseProductionOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ComplementaryOrderResponse> Get(int number, CancellationToken cancellationToken) =>
        complementaryOrders.GetAsync(number, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/complementary-orders/{number}/approve</c>: a Manager approves a PendingApproval request (AC-17.1).
    /// No body. <c>200</c> with it, now Approved; <c>404</c> for an unknown number; <c>409</c> <c>not-pending-approval</c>
    /// for any other status (AC-17.3).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Approve or reject a complementary order": Manager only (AC-17.4). A Manager may approve a request they
    /// raised themselves (decision #14).
    /// </remarks>
    [HttpPost("{number:int}/approve")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`not-pending-approval`: only a PendingApproval complementary order can be approved (AC-17.3).")]
    public Task<ComplementaryOrderResponse> Approve(int number, CancellationToken cancellationToken)
    {
        // AC-17.1, AC-05.3: the approval records who approved, taken from the checked token.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return complementaryOrders.ApproveAsync(number, userId, cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/complementary-orders/{number}/reject</c>: a Manager rejects a PendingApproval request, with an
    /// optional note (AC-17.2): <c>{ "note": "Use the spare from order #11" }</c>, an empty object or no body at all.
    /// <c>200</c> with it, now Rejected; <c>404</c> for an unknown number; <c>400</c> <c>validation</c> for a note over 500
    /// characters; <c>409</c> <c>not-pending-approval</c> for any other status (AC-17.3).
    /// </summary>
    /// <remarks>
    /// PRD 4.2: Manager only (AC-17.4). <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> makes the body
    /// optional: by default <c>[ApiController]</c> refuses a missing body with <c>400</c>, but design 5.8 marks the note,
    /// and so the whole body, as optional. Without a body the parameter is <c>null</c>.
    /// </remarks>
    [HttpPost("{number:int}/reject")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`not-pending-approval`: only a PendingApproval complementary order can be rejected (AC-17.3).")]
    public Task<ComplementaryOrderResponse> Reject(
        int number,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RejectComplementaryOrderRequest? request,
        CancellationToken cancellationToken)
    {
        // AC-17.2, AC-05.3: the rejection records who rejected, taken from the checked token.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return complementaryOrders.RejectAsync(number, request, userId, cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/complementary-orders/{number}/send</c>: sends the material of an Approved complementary order, one
    /// quantity per line (AC-18.1, design 5.8): <c>{ "lines": [ { "itemId": 3, "quantity": 20 } ] }</c>. <c>200</c> with
    /// it, now Sent; the parent order line's <c>complementarySends</c> has grown. <c>404</c> for an unknown number;
    /// <c>400</c> <c>validation</c> for a bad body; <c>409</c> <c>not-approved</c> for any other status (AC-18.3);
    /// <c>400</c> <c>send-out-of-range</c> (AC-18.2); <c>409</c> <c>insufficient-stock</c> (AC-18.4).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Send materials for an order or an approved complementary order": Warehouse or Manager. A Production-only
    /// user gets <c>403</c> (AC-18.5) before the complementary order or the body is looked at (design 2.2). All rules are
    /// in <see cref="ComplementarySendService"/>.
    /// </remarks>
    [HttpPost("{number:int}/send")]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`not-approved`: only an Approved complementary order can be sent (AC-18.3); `insufficient-stock` (AC-18.4).")]
    public Task<ComplementaryOrderResponse> Send(
        int number, SendComplementaryOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-18.1, AC-05.3: the send records who sent it, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return sends.SendAsync(number, request, userId, cancellationToken);
    }
}
