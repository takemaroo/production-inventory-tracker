using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) The <c>/api/v1/sales-orders</c> endpoints (US-24 to US-27, design 3.13 to 3.15, 4 and 5.12): list sales
/// orders, view one, create one (T-025), ship one (T-027) and cancel one (T-028). Warehouse, Manager and Seller users may
/// read; Manager and Seller users may create and cancel; Warehouse and Manager users ship.
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>CreatedAtAction</c>. The controller is thin: every rule is in <see cref="SalesOrderService"/> and, for the ship and
/// the cancel, <see cref="ShipSalesOrderService"/> and <see cref="CancelSalesOrderService"/>. Sales orders are addressed
/// by their number, not an id (design 2.1).
/// </para>
/// <para>
/// <b>Why each action carries its own <c>[Authorize]</c></b> (decision #156, explained in full on
/// <c>CustomersController</c>): ASP.NET Core combines a class attribute and an action attribute with AND, and the
/// <c>403</c> sentence names the roles of every requirement the user failed. One attribute per action keeps the sentence
/// exact (design 7.1): a Production-only user reading gets "It needs the Warehouse, Manager or Seller role.", and a
/// Warehouse user creating gets "It needs the Manager or Seller role." (AC-24.9, AC-25.4).
/// </para>
/// <para>
/// There is no <c>PUT</c> or <c>DELETE</c>: no endpoint changes a sales order's customer, reference or lines after
/// creation, and sales orders are never deleted (AC-24.7, PRD section 5). The path exists for <c>GET</c>, so routing
/// answers those methods with <c>405</c> <c>method-not-allowed</c> by itself (architecture 4.1 item 18).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/sales-orders")]
public sealed class SalesOrdersController(
    SalesOrderService salesOrders, ShipSalesOrderService shipments, CancelSalesOrderService cancellations) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/sales-orders</c>: one page of sales order summaries, newest first, optionally filtered with
    /// <c>?status=Created</c>, <c>Shipped</c> or <c>Cancelled</c>, <c>itemId</c> and <c>customerId</c>, combined
    /// (AC-25.1, design 5.12). An unknown status is <c>400</c>; an id that matches nothing gives an empty page.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="status">Optional status filter, any letter case. Received as text so the error uses the design's wording.</param>
    /// <param name="itemId">Optional: only sales orders with a line for this item.</param>
    /// <param name="customerId">Optional: only this customer's sales orders.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <remarks>
    /// PRD 4.2, "View customers, sales orders and shipped-totals reports": Warehouse, Manager or Seller. A Production-only
    /// user gets <c>403</c> (AC-25.4), even with an unknown status, because the role check comes before the query is read
    /// (design 2.2). The id filters are bound by MVC, so <c>?itemId=abc</c> gets the framework's sentence (decision #113).
    /// </remarks>
    [HttpGet]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<SalesOrderSummaryResponse>> List(
        [FromQuery] PageQuery paging,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "itemId")] int? itemId,
        [FromQuery(Name = "customerId")] int? customerId,
        CancellationToken cancellationToken) =>
        salesOrders.ListAsync(paging, status, itemId, customerId, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/sales-orders/{number}</c>: the full sales order, with its customer's current name and active status,
    /// its lines and its status history (AC-25.2). <c>404</c> "Sales order #{number} not found." (AC-25.3).
    /// </summary>
    /// <remarks>
    /// Warehouse, Manager or Seller. A Production-only user gets <c>403</c>, not <c>404</c>, even for a number that does
    /// not exist, so a refused user cannot learn which numbers exist (design 2.2, AC-25.4).
    /// </remarks>
    [HttpGet("{number:int}")]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<SalesOrderResponse> Get(int number, CancellationToken cancellationToken) =>
        salesOrders.GetAsync(number, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/sales-orders</c>: creates a sales order for an active customer, with one or more lines of any item
    /// (AC-24.1, AC-24.8). Stock is neither checked nor moved (AC-24.2). <c>201</c> with the sales order and
    /// <c>Location: /api/v1/sales-orders/{number}</c>; <c>400</c> <c>too-many-lines</c> for more than 500 lines (AC-24.5);
    /// <c>400</c> <c>validation</c> for a missing or unknown customer, a long reference or bad lines (AC-24.3); <c>409</c>
    /// <c>customer-inactive</c> for a deactivated customer (AC-24.4).
    /// </summary>
    /// <remarks>PRD 4.2, "Create or cancel a sales order": Manager or Seller (AC-24.9).</remarks>
    [HttpPost]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`customer-inactive`: the customer is deactivated (AC-24.4).")]
    public async Task<ActionResult<SalesOrderResponse>> Create(
        CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3, AC-24.1: "created by" is the caller, taken from the checked token (its sub claim), never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var salesOrder = await salesOrders.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { number = salesOrder.Number }, salesOrder);
    }

    /// <summary>
    /// <c>POST /api/v1/sales-orders/{number}/ship</c>: ships a Created sales order in full, with an optional note
    /// (AC-26.1, design 5.12): <c>{ "note": "Delivery note 8812" }</c>, an empty object or no body at all. <c>200</c> with
    /// the sales order, now Shipped; one <c>ShippedToCustomer</c> movement per line took its quantity out of the
    /// Warehouse. <c>404</c> for an unknown number; <c>400</c> <c>validation</c> for a note over 500 characters
    /// (AC-26.8); <c>409</c> <c>already-shipped</c> or <c>sales-order-cancelled</c> (AC-26.7), or
    /// <c>insufficient-stock</c> naming every line the Warehouse cannot cover (AC-26.3).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Ship a sales order": Warehouse or Manager. A Production or Seller user gets <c>403</c> (AC-26.9) before
    /// the sales order or the body is looked at (design 2.2). <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>
    /// makes the body optional: by default <c>[ApiController]</c> refuses a missing body with <c>400</c>, but design 5.12
    /// says no body means no note. Without a body the parameter is <c>null</c>. This is the pattern of the complementary
    /// reject (<c>ComplementaryOrdersController.Reject</c>).
    /// </remarks>
    [HttpPost("{number:int}/ship")]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`already-shipped` or `sales-order-cancelled` (AC-26.7), or `insufficient-stock` (AC-26.3).")]
    public Task<SalesOrderResponse> Ship(
        int number,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ShipSalesOrderRequest? request,
        CancellationToken cancellationToken)
    {
        // AC-05.3, AC-26.1: "shipped by" is the caller, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return shipments.ShipAsync(number, request, userId, cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/sales-orders/{number}/cancel</c>: cancels a Created sales order with a reason (AC-27.1, design
    /// 5.12): <c>{ "reason": "Customer changed the order to 400 bags" }</c>. <c>200</c> with the sales order, now Cancelled,
    /// with <c>cancellationReason</c> filled and a Cancelled history entry; no stock moves. <c>404</c> for an unknown
    /// number; <c>400</c> <c>validation</c> for a missing, blank or too long reason; <c>409</c> <c>already-shipped</c> or
    /// <c>sales-order-cancelled</c> for a Shipped or already Cancelled sales order (AC-27.2).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Create or cancel a sales order": Manager or Seller, whoever created the sales order (decision #146). A
    /// Warehouse or Production user gets <c>403</c> (AC-27.4) before the sales order or the body is looked at (design 2.2).
    /// </remarks>
    [HttpPost("{number:int}/cancel")]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`already-shipped` or `sales-order-cancelled`: a Shipped or Cancelled sales order cannot be cancelled (AC-27.2).")]
    public Task<SalesOrderResponse> Cancel(int number, CancelSalesOrderRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3, AC-27.1: "cancelled by" is the caller, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return cancellations.CancelAsync(number, request, userId, cancellationToken);
    }
}
