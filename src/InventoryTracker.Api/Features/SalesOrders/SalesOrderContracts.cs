using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) Body of <c>POST /api/v1/sales-orders</c> (design 5.12, architecture 4.1 item 15): which customer, their own
/// reference, and what they ordered, for example
/// <c>{ "customerId": 1, "customerReference": "PO-2291", "lines": [ { "itemId": 7, "quantity": 600 } ] }</c>.
/// </summary>
/// <param name="CustomerId">An existing, active customer (AC-24.1, AC-24.3, AC-24.4).</param>
/// <param name="Lines">
/// One to 500 lines (AC-24.3, AC-24.5), each a different item of either type (decision #144) with its quantity. The lines
/// keep the order in which they are sent (AC-24.1).
/// </param>
/// <param name="CustomerReference">
/// Optional: the customer's own reference, such as their purchase order number, trimmed, up to 100 characters. Empty or
/// blank is stored as no reference (design 2.6).
/// </param>
/// <remarks>
/// Every field is nullable, and so is each line, so that a missing or <c>null</c> value reaches
/// <see cref="SalesOrderService"/>, which answers with the design's own messages under each field's JSON path rather
/// than a framework error (architecture 6, Validation). <c>CustomerReference</c> is the optional field, so it comes last
/// with <c>= null</c>: that changes nothing when JSON is read, but the OpenAPI description then shows it as not required
/// (decision #110).
/// </remarks>
public sealed record CreateSalesOrderRequest(
    int? CustomerId, IReadOnlyList<SalesOrderLineRequest?>? Lines, string? CustomerReference = null);

/// <summary>(v1.1) One line of a new sales order: which item, and how much of it (design 5.12).</summary>
/// <param name="ItemId">An existing item, a raw material or a finished product (decision #144); at most once per sales order.</param>
/// <param name="Quantity">
/// In the item's own unit: greater than 0, at most 3 decimal places, at most 999,999,999.999 (AC-24.3, decision #80).
/// Sent as a JSON number (design 2.4).
/// </param>
public sealed record SalesOrderLineRequest(int? ItemId, decimal? Quantity);

/// <summary>
/// (v1.1) Body of <c>POST /api/v1/sales-orders/{number}/ship</c> (design 5.12, architecture 4.1 item 15), for example
/// <c>{ "note": "Delivery note 8812" }</c>. There are no quantities: every line ships in full (AC-26.1, decision #141).
/// </summary>
/// <param name="Note">
/// Optional: a note about the shipment, such as the delivery note number, trimmed, up to 500 characters (AC-26.8). Empty
/// or blank is stored as no note (design 2.6).
/// </param>
/// <remarks>
/// The whole body is optional too: no body, <c>{}</c>, a <c>null</c> note and an all-spaces note all mean "no note"
/// (AC-26.8). The controller binds it with <c>EmptyBodyBehavior.Allow</c>, the complementary reject's pattern, so a
/// missing body arrives as <c>null</c>. <c>= null</c> marks the note as not required in the OpenAPI description
/// (decision #110).
/// </remarks>
public sealed record ShipSalesOrderRequest(string? Note = null);

/// <summary>
/// (v1.1) A sales order in full, as <c>GET /api/v1/sales-orders/{number}</c> and every change to a sales order return it
/// (design 5.12, AC-25.2). Built in one place, <see cref="SalesOrderService.ReadDetailAsync"/> (decision #161).
/// </summary>
/// <param name="Number">The sales order number, its own sequence from 1 (D-10): "sales order #1".</param>
/// <param name="Customer">The customer's id, current name and active status, read now, not copied (AC-29.3, AC-29.4).</param>
/// <param name="CustomerReference">The customer's own reference, or <c>null</c> when none was given.</param>
/// <param name="Status">Created, Shipped or Cancelled.</param>
/// <param name="CreatedAt">When it was created, UTC, whole seconds (AC-24.1, AC-05.3).</param>
/// <param name="CreatedBy">Who created it (AC-05.3).</param>
/// <param name="ShipmentNote">The note given at shipment; <c>null</c> until shipped, or when shipped without one (AC-25.2).</param>
/// <param name="CancellationReason">The reason given at cancellation; <c>null</c> unless cancelled (AC-25.2).</param>
/// <param name="Lines">The lines, in the order they were entered (AC-24.1).</param>
/// <param name="History">One entry per status reached, oldest first, with who and when (AC-05.3, AC-25.2).</param>
public sealed record SalesOrderResponse(
    int Number,
    CustomerSummary Customer,
    string? CustomerReference,
    SalesOrderStatus Status,
    DateTime CreatedAt,
    PersonResponse CreatedBy,
    string? ShipmentNote,
    string? CancellationReason,
    IReadOnlyList<SalesOrderLineResponse> Lines,
    IReadOnlyList<SalesOrderHistoryEntryResponse> History);

/// <summary>(v1.1) One line of a sales order detail (design 5.12, AC-24.1, AC-25.2).</summary>
/// <param name="ItemId">The item's id.</param>
/// <param name="ItemName">The item's current name.</param>
/// <param name="ItemType">RawMaterial or FinishedProduct: both can be sold (decision #144).</param>
/// <param name="Unit">The item's unit, for example "Bag": the unit of <paramref name="Quantity"/> (design 2.4).</param>
/// <param name="Quantity">The quantity ordered.</param>
public sealed record SalesOrderLineResponse(int ItemId, string ItemName, ItemType ItemType, string Unit, decimal Quantity);

/// <summary>
/// (v1.1) One status the sales order reached, with who and when (AC-05.3, design 5.12 "history"). Built from the sales
/// order's timestamp and user pairs; there is no history table, because each status is reached at most once
/// (architecture 3.2).
/// </summary>
/// <param name="Status">The status reached.</param>
/// <param name="At">When, UTC. The Shipped entry's time is the shipment time the reports use (D-13).</param>
/// <param name="By">Who.</param>
/// <param name="Note">The shipment note on Shipped, the reason on Cancelled, and <c>null</c> on Created.</param>
public sealed record SalesOrderHistoryEntryResponse(SalesOrderStatus Status, DateTime At, PersonResponse By, string? Note);

/// <summary>
/// (v1.1) One row of <c>GET /api/v1/sales-orders</c> (design 5.12, AC-25.1): the sales order without its lines, like the
/// production order list.
/// </summary>
/// <param name="Number">The sales order number.</param>
/// <param name="Customer">The customer's id, current name and active status (AC-29.3).</param>
/// <param name="CustomerReference">The customer's own reference, or <c>null</c>.</param>
/// <param name="Status">Created, Shipped or Cancelled.</param>
/// <param name="CreatedAt">When it was created, UTC. The list is newest first by it.</param>
/// <param name="StatusChangedAt">The time of the latest <c>history</c> entry (design 5.12).</param>
/// <param name="LineCount">How many lines it has; the lines themselves are in the detail.</param>
public sealed record SalesOrderSummaryResponse(
    int Number,
    CustomerSummary Customer,
    string? CustomerReference,
    SalesOrderStatus Status,
    DateTime CreatedAt,
    DateTime StatusChangedAt,
    int LineCount);
