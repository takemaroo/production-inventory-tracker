using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// The current stock of one item (design 5.5): the body of <c>GET /api/v1/stock/{itemId}</c> and one row of
/// <c>GET /api/v1/stock</c>. Every figure is calculated from movements and orders when asked, never stored (D-2).
/// </summary>
/// <param name="ItemId">The item's id.</param>
/// <param name="ItemName">The item's name.</param>
/// <param name="ItemType"><c>RawMaterial</c> or <c>FinishedProduct</c>.</param>
/// <param name="Unit">The item's unit name (AC-10.1: "with the unit").</param>
/// <param name="Warehouse">AC-10.1, D-2: the sum of the item's Warehouse changes; 0 with no movements (AC-10.2).</param>
/// <param name="ProductionStorage">AC-10.1, D-2: the sum of the item's Production storage changes.</param>
/// <param name="CommittedToOrder">
/// AC-10.3, D-3: committed to the InProduction order; 0 when no running order uses the material. Null for a finished
/// product.
/// </param>
/// <param name="CommittedOrderNumber">The InProduction order the material is committed to, or null.</param>
/// <param name="PendingReturns">AC-10.3: quantity in Pending returns. Null for a finished product.</param>
/// <param name="FreeLeftover">AC-10.3, D-4: free leftover in Production storage. Null for a finished product.</param>
public sealed record StockResponse(
    int ItemId,
    string ItemName,
    ItemType ItemType,
    string Unit,
    decimal Warehouse,
    decimal ProductionStorage,
    decimal? CommittedToOrder,
    int? CommittedOrderNumber,
    decimal? PendingReturns,
    decimal? FreeLeftover);

/// <summary>Body of <c>POST /api/v1/receipts</c> (design 5.6, AC-09.1).</summary>
/// <param name="ItemId">The raw material received. Required; must exist.</param>
/// <param name="Quantity">Greater than 0, at most 3 decimal places (AC-09.2).</param>
/// <param name="ReceiptDate">The delivery date, <c>yyyy-MM-dd</c>, not later than today in UTC (AC-09.2).</param>
/// <param name="Note">Optional, trimmed, up to 500 characters; blank is stored as no note.</param>
/// <remarks>
/// Every field is nullable, and the date is text, so a missing or malformed value reaches <see cref="ReceiptService"/>,
/// which answers with the design's own messages rather than a framework one (architecture 6, Validation). A value of the
/// wrong JSON type (a quantity sent as a string) is still refused by the framework first (architecture 4.1 item 2).
/// </remarks>
public sealed record ReceiptRequest(int? ItemId, decimal? Quantity, string? ReceiptDate, string? Note);
