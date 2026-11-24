using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Reports;

/// <summary>
/// (v1.1) The <c>/api/v1/reports</c> endpoints (US-32, US-33, design 3.16, 4 and 5.13): the shipped totals per item and
/// per customer. Warehouse, Manager and Seller users may read them; a Production-only user gets <c>403</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow and model binding. The controller is
/// thin: every rule is in <see cref="ShippedTotalsService"/>. The reports are computed lists, not records, so they have
/// no id and no detail URL (design 2.1), and only <c>GET</c>: routing answers any other method with <c>405</c>.
/// </para>
/// <para>
/// <b>Roles.</b> PRD 4.2, "View customers, sales orders and shipped-totals reports": Warehouse, Manager or Seller
/// (AC-32.6, AC-33.2, decision #147). Each action carries its own <c>[Authorize(Roles = ...)]</c>, the house rule of
/// decision #156 explained on <c>CustomersController</c>. The role check runs before model binding, so a Production
/// user's <c>?from=yesterday</c> is <c>403</c>, not <c>400</c> (design 2.2).
/// </para>
/// <para>
/// <b>Query values.</b> The dates and the item type arrive as text and are parsed by the service with the design 7.2 list
/// messages (architecture 4.1 item 19). The id filters are <c>int?</c>, so model binding refuses <c>?customerId=abc</c>
/// with its own sentence, "The value 'abc' is not valid." (decision #113), before the action runs. An id that names
/// nothing is not an error: it matches nothing, and the page is empty (AC-32.5).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController(ShippedTotalsService totals) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/reports/shipped-by-item</c>: one page of shipped totals per item, A to Z by item name (US-32,
    /// AC-32.1, design 5.13). Optional filters, combined: <c>from</c> and <c>to</c> (whole UTC days, both included, on
    /// the shipment time), <c>itemType</c> and <c>customerId</c>. Only Shipped sales orders count (AC-32.2). A bad date or
    /// item type, or <c>from</c> after <c>to</c>, is <c>400</c>; no match is an empty page (AC-32.5).
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="from">Optional first day included, <c>yyyy-MM-dd</c>.</param>
    /// <param name="to">Optional last day included, <c>yyyy-MM-dd</c>.</param>
    /// <param name="itemType">Optional: <c>RawMaterial</c> or <c>FinishedProduct</c>, any letter case.</param>
    /// <param name="customerId">Optional: only this customer's sales orders count.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet("shipped-by-item")]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<ShippedByItemResponse>> ShippedByItem(
        [FromQuery] PageQuery paging,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "itemType")] string? itemType,
        [FromQuery(Name = "customerId")] int? customerId,
        CancellationToken cancellationToken) =>
        totals.ByItemAsync(paging, from, to, itemType, customerId, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/reports/shipped-by-customer</c>: one page of shipped totals per customer and item, A to Z by
    /// customer name, then item name (US-33, AC-33.1, design 5.13). Optional filters, combined: <c>from</c> and
    /// <c>to</c>, <c>customerId</c> and <c>itemId</c>. The customer is shown with its current name and active status
    /// (AC-29.3, AC-33.4). The query rules are those of <c>shipped-by-item</c> (AC-33.2).
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="from">Optional first day included, <c>yyyy-MM-dd</c>.</param>
    /// <param name="to">Optional last day included, <c>yyyy-MM-dd</c>.</param>
    /// <param name="customerId">Optional: only this customer's rows.</param>
    /// <param name="itemId">Optional: only this item's rows.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet("shipped-by-customer")]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<ShippedByCustomerResponse>> ShippedByCustomer(
        [FromQuery] PageQuery paging,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "customerId")] int? customerId,
        [FromQuery(Name = "itemId")] int? itemId,
        CancellationToken cancellationToken) =>
        totals.ByCustomerAsync(paging, from, to, customerId, itemId, cancellationToken);
}
