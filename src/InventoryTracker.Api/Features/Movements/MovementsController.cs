using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Features.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Movements;

/// <summary>
/// The <c>/api/v1/movements</c> endpoints (US-11, design 4 and 5.6): the history of every stock change, as a filtered
/// list or one movement at a time.
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow. (v1.1) Both reads are for Warehouse,
/// Production and Manager (PRD 4.2, "View ... movement history"); a Seller-only user gets <c>403</c> (AC-11.1,
/// AC-28.7, AC-30.6, decision #147). Each action carries its own <c>[Authorize(Roles = ...)]</c>, the house rule of
/// decision #156: a class-level attribute would combine with any action's own attribute with AND.
/// </para>
/// <para>
/// The role check runs in the authorization middleware, before model binding and before the action, so a Seller's
/// <c>GET /movements?type=Nope</c> is <c>403</c>, not <c>400</c>, and a Seller's <c>GET /movements/99999</c> is
/// <c>403</c>, not <c>404</c> (design 2.2): a refused caller learns nothing about the data.
/// </para>
/// <para>
/// AC-11.3: there are only two <c>GET</c> actions. With no <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c> or <c>POST</c>
/// action on these paths, routing answers those methods with the framework's <c>405</c> (design 4), so no request can
/// edit, delete or add a movement here. Movements are added only by the commands that cause them (receipts,
/// adjustments, sends, completions, returns and (v1.1) shipments), through <see cref="StockLedger"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/movements")]
public sealed class MovementsController(MovementService movements) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/movements</c>: one page of movements, newest first, filtered by any of <c>itemId</c>,
    /// <c>location</c>, <c>type</c>, <c>orderNumber</c>, <c>complementaryOrderNumber</c>, (v1.1)
    /// <c>salesOrderNumber</c>, <c>from</c> and <c>to</c> (AC-11.1, AC-11.2, AC-28.2, design 5.6). Filters combine with
    /// AND; no match is <c>200</c> with an empty list; a value in the wrong format, or <c>from</c> after <c>to</c>, is
    /// <c>400</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="filter">
    /// The filter query parameters. Like <paramref name="paging"/>, its name must not equal a query key; each property
    /// names its own key.
    /// </param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    [Authorize(Roles = Roles.WarehouseProductionOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<MovementResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery] MovementFilter filter, CancellationToken cancellationToken) =>
        movements.ListAsync(paging, filter, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/movements/{id}</c>: one movement (AC-11.1, design 5.6), or <c>404</c> "Movement {id} not found.".
    /// A receipt's or adjustment's <c>Location</c> header points here.
    /// </summary>
    /// <remarks>(v1.1) Warehouse, Production or Manager, like the list (AC-30.6).</remarks>
    [HttpGet("{id:int}")]
    [Authorize(Roles = Roles.WarehouseProductionOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<MovementResponse> Get(int id, CancellationToken cancellationToken) =>
        movements.GetAsync(id, cancellationToken);
}
