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
/// <param name="OpenDemand">
/// (v1.1) AC-10.5, AC-31.1, D-12: the item's quantity on Created sales orders, in <paramref name="Unit"/>. A number for
/// every item type, 0 (never null) when no Created sales order has the item. Information only: it may exceed
/// <paramref name="Warehouse"/> and changes no other field (AC-31.3). Placed right after the stock figures (design 5.5).
/// </param>
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
    decimal OpenDemand,
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
/// <c>= null</c> on an optional field changes nothing when JSON is read (a missing field is null either way); it
/// tells the OpenAPI description that the field may be left out (decision #110).
/// </remarks>
public sealed record ReceiptRequest(int? ItemId, decimal? Quantity, string? ReceiptDate, string? Note = null);

/// <summary>Body of <c>POST /api/v1/adjustments</c> (design 5.6, AC-12.1).</summary>
/// <param name="ItemId">The item whose stock is corrected. Required; must exist. Raw material or finished product.</param>
/// <param name="Location">
/// <c>Warehouse</c> or <c>ProductionStorage</c>, any letter case (design 2.3). Finished products only in the Warehouse
/// (AC-12.6).
/// </param>
/// <param name="Quantity">
/// The signed change: positive adds stock, negative removes it (a write-off). Not zero, at most 3 decimal places, from
/// -999,999,999.999 to 999,999,999.999 (AC-12.2, decision #80).
/// </param>
/// <param name="Reason">Required; trimmed, then 1 to 500 characters (AC-12.1, AC-12.2). Stored on the movement.</param>
/// <remarks>
/// As for receipts, every field is nullable and the location is text, so a missing or unknown value reaches
/// <see cref="AdjustmentService"/>, which answers with the design's own messages (architecture 4.1 item 3).
/// </remarks>
public sealed record AdjustmentRequest(int? ItemId, string? Location, decimal? Quantity, string? Reason);
