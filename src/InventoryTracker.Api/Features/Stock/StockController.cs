using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// The <c>/api/v1/stock</c> endpoints (US-10, design 4 and 5.5): current stock of every item, or of one.
/// </summary>
/// <remarks>
/// Built the same way as <c>UnitsController</c>, which explains the request flow. There is no
/// <c>[Authorize(Roles = ...)]</c>: every signed-in user may view stock (PRD 4.2, AC-10.1), and the fallback policy
/// still answers <c>401</c> without a token. All rules are in <see cref="StockService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/stock")]
public sealed class StockController(StockService stock) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/stock</c>: one page of every item's stock, newest item first, optionally filtered with
    /// <c>?itemType=RawMaterial</c> or <c>?itemType=FinishedProduct</c> (AC-10.1, design 5.5). An unknown type is <c>400</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="itemType">Optional type filter, any letter case.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    public Task<PagedResult<StockResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery(Name = "itemType")] string? itemType, CancellationToken cancellationToken) =>
        stock.ListAsync(paging, itemType, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/stock/{itemId}</c>: one item's Warehouse and Production storage stock with its unit, and for a raw
    /// material the committed, pending-return and free-leftover figures (AC-10.1 to AC-10.3). <c>404</c> "Item {id} not
    /// found." for an unknown item (AC-10.4).
    /// </summary>
    [HttpGet("{itemId:int}")]
    public Task<StockResponse> Get(int itemId, CancellationToken cancellationToken) =>
        stock.GetAsync(itemId, cancellationToken);
}
