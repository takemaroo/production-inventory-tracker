using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// The <c>/api/v1/production-orders</c> endpoints (US-13, US-14, design 4 and 5.7). Any signed-in user may list and view
/// orders (PRD 4.2, AC-14.1); only a Manager may create one (AC-13.7). The send, complete and cancel actions are added
/// to this controller by T-010, T-011 and T-016.
/// </summary>
/// <remarks>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>CreatedAtAction</c>. Orders are addressed by their number, not an id (design 2.1). There is no <c>PUT</c> or
/// <c>DELETE</c>: an order's product, quantity and planned amounts never change after creation (AC-13.6), so those
/// methods get the framework's <c>405</c>. All rules are in <see cref="ProductionOrderService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/production-orders")]
public sealed class ProductionOrdersController(ProductionOrderService orders) : ControllerBase
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
}
