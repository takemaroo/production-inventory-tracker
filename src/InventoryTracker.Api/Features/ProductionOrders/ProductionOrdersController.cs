using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// The <c>/api/v1/production-orders</c> endpoints (US-13, US-14, design 4 and 5.7). Any signed-in user may list and view
/// orders (PRD 4.2, AC-14.1); only a Manager may create one (AC-13.7); a Warehouse user or a Manager sends the
/// materials (AC-15.1, AC-15.12). The complete and cancel actions are added to this controller by T-011 and T-016.
/// </summary>
/// <remarks>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>CreatedAtAction</c>. Orders are addressed by their number, not an id (design 2.1). There is no <c>PUT</c> or
/// <c>DELETE</c>: an order's product, quantity and planned amounts never change after creation (AC-13.6), so those
/// methods get the framework's <c>405</c>. All rules are in <see cref="ProductionOrderService"/> and, for the send,
/// <see cref="SendOrderService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/production-orders")]
public sealed class ProductionOrdersController(ProductionOrderService orders, SendOrderService sends) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/production-orders</c>: one page of order summaries, newest first, optionally filtered with
    /// <c>?status=Created</c>, <c>InProduction</c>, <c>Completed</c> or <c>Cancelled</c> (AC-14.1, design 5.7). An unknown
    /// status is <c>400</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="status">Optional status filter, any letter case. Received as text so the error uses the design's wording.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    public Task<PagedResult<ProductionOrderSummaryResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery(Name = "status")] string? status, CancellationToken cancellationToken) =>
        orders.ListAsync(paging, status, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/production-orders/{number}</c>: the full order (AC-14.2), with a fresh leftover estimate per line
    /// while it is Created (AC-13.2). <c>404</c> "Order #{number} not found." (AC-14.3).
    /// </summary>
    [HttpGet("{number:int}")]
    public Task<ProductionOrderResponse> Get(int number, CancellationToken cancellationToken) =>
        orders.GetAsync(number, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/production-orders</c>: creates an order from the product's recipe (AC-13.1). <c>201</c> with the
    /// order and <c>Location: /api/v1/production-orders/{number}</c>; <c>400</c> <c>validation</c> for a missing or unknown
    /// product or a bad ordered quantity; <c>400</c> <c>order-not-finished-product</c> or <c>no-recipe</c> (AC-13.3).
    /// </summary>
    /// <remarks>PRD 4.2, "Create or cancel an order": Manager only (AC-13.7).</remarks>
    [HttpPost]
    [Authorize(Roles = Roles.Manager)]
    public async Task<ActionResult<ProductionOrderResponse>> Create(
        ProductionOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3: the order records who created it, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var order = await orders.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { number = order.Number }, order);
    }

    /// <summary>
    /// <c>POST /api/v1/production-orders/{number}/send</c>: sends the materials for a Created order in one go, one
    /// quantity per line (AC-15.1, design 5.7). <c>200</c> with the order detail, now InProduction, with leftover
    /// applied, warehouse send and carried leftover fixed per line. <c>404</c> for an unknown number; <c>400</c>
    /// <c>validation</c> for a bad body (AC-15.8); <c>409</c> when the order is not Created (AC-15.9), another order is
    /// InProduction (AC-15.10) or a return of one of its materials is Pending (AC-15.11); <c>400</c>
    /// <c>send-out-of-range</c> (AC-15.6); <c>409</c> <c>insufficient-stock</c> (AC-15.7).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Send materials for an order": Warehouse or Manager. A Production-only user gets <c>403</c> (AC-15.12)
    /// before the order or the body is looked at (design 2.2).
    /// </remarks>
    [HttpPost("{number:int}/send")]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    public Task<ProductionOrderResponse> Send(int number, SendOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3, AC-15.1: the send records who sent it, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return sends.SendAsync(number, request, userId, cancellationToken);
    }
}
