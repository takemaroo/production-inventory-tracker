using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// The current-stock reads (US-10): one item's stock, and a page of every item's stock.
/// </summary>
/// <remarks>
/// Read-only, so no write transaction (architecture 5.2 applies to changes). The figures themselves come from
/// <see cref="StockQueries"/>; this class finds the items, pages them and shapes the response (design 5.5).
/// </remarks>
public sealed class StockService(AppDbContext db, StockQueries stock)
{
    /// <summary>
    /// AC-10.1, AC-10.4: the stock of one item, or <c>404</c> "Item {id} not found." when it does not exist.
    /// </summary>
    public async Task<StockResponse> GetAsync(int itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items
            .Where(candidate => candidate.Id == itemId)
            .Select(candidate => new StockItem(candidate.Id, candidate.Name, candidate.Type, candidate.Unit.Name))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Item {itemId} not found.");

        var figures = await stock.GetFiguresAsync([item.Id], cancellationToken);
        return ToResponse(item, figures[item.Id]);
    }

    /// <summary>
    /// AC-10.1: one page of every item's stock, ordered like the items (newest first by creation time, then id,
    /// architecture 4.1 item 10), optionally only one type (<c>?itemType=RawMaterial</c>, design 5.5).
    /// </summary>
    public async Task<PagedResult<StockResponse>> ListAsync(PageQuery page, string? itemType, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        page.Validate(errors);
        var typeFilter = EnumParser.ParseOptional<ItemType>(itemType, "itemType", "itemType", errors);
        errors.ThrowIfAny();

        var query = db.Items.AsQueryable();
        if (typeFilter is { } wanted)
        {
            query = query.Where(item => item.Type == wanted);
        }

        // Page the items first, then sum only the movements of this page's items (architecture 5.1): at most 100
        // items, however many movements the database holds.
        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new StockItem(item.Id, item.Name, item.Type, item.Unit.Name))
            .ToPagedResultAsync(page, cancellationToken);

        var figures = await stock.GetFiguresAsync(items.Items.Select(item => item.Id).ToList(), cancellationToken);
        var rows = items.Items.Select(item => ToResponse(item, figures[item.Id])).ToList();
        return new PagedResult<StockResponse>(rows, items.Page, items.PageSize, items.TotalCount);
    }

    /// <summary>
    /// Design 5.5: finished products show <c>null</c> for the four leftover figures, because committed material, returns
    /// and free leftover are about raw materials in Production storage (AC-10.3 "for each raw material").
    /// </summary>
    private static StockResponse ToResponse(StockItem item, StockFigures figures)
    {
        var isRawMaterial = item.Type == ItemType.RawMaterial;
        return new StockResponse(
            item.Id,
            item.Name,
            item.Type,
            item.Unit,
            figures.Warehouse,
            figures.ProductionStorage,

            // (v1.1) AC-31.1, design 5.5: unlike the four leftover figures, open demand is about selling, which any item
            // type can be (decision #144), so every row shows it as a number.
            figures.OpenDemand,
            isRawMaterial ? figures.CommittedToOrder : null,
            isRawMaterial ? figures.CommittedOrderNumber : null,
            isRawMaterial ? figures.PendingReturns : null,
            isRawMaterial ? figures.FreeLeftover : null);
    }

    /// <summary>The item fields a stock row shows.</summary>
    private sealed record StockItem(int Id, string Name, ItemType Type, string Unit);
}
