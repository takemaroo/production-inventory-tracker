using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Body of <c>POST /api/v1/production-orders</c> (design 5.7): which finished product to make, and how much of it.
/// </summary>
/// <param name="FinishedProductId">The id of an existing finished product that has a recipe (AC-13.3).</param>
/// <param name="OrderedQuantity">
/// How much finished product to make, in the product's own unit: greater than 0, at most 3 decimal places, at most
/// 999,999,999.999 (AC-13.3, decision #80). Sent as a JSON number (design 2.4).
/// </param>
/// <remarks>
/// Both fields are nullable so that a missing value reaches <see cref="ProductionOrderService"/>, which answers with the
/// design's own message under the field's JSON path rather than a framework error (architecture 6, Validation).
/// </remarks>
public sealed record ProductionOrderRequest(int? FinishedProductId, decimal? OrderedQuantity);

/// <summary>
/// Body of <c>POST /api/v1/production-orders/{number}/send</c> (design 5.7, AC-15.1): one quantity for every line of the
/// order, for example <c>{ "lines": [ { "itemId": 3, "quantity": 995 }, { "itemId": 4, "quantity": 15 } ] }</c>.
/// </summary>
/// <param name="Lines">
/// Every order line exactly once, in any order (design 5.7). A missing list counts as an empty one, so every order line
/// is reported as missing (AC-15.8).
/// </param>
/// <remarks>
/// The list and its elements are nullable so that a missing or <c>null</c> value reaches <see cref="SendOrderService"/>,
/// which answers with the design's own messages under each field's JSON path (architecture 6, Validation).
/// </remarks>
public sealed record SendOrderRequest(IReadOnlyList<SendOrderLineRequest?>? Lines);

/// <summary>One line of a send: which raw material, and how much of it the Warehouse sends (design 5.7).</summary>
/// <param name="ItemId">The raw material; it must be on the order (AC-15.8).</param>
/// <param name="Quantity">
/// The quantity taken from the Warehouse for this line: 0 or more, at most 3 decimal places, at most 999,999,999.999
/// (AC-15.8, decision #80), and inside the line's send range (D-7, AC-15.6). Zero is allowed where the range starts at
/// zero (AC-15.4).
/// </param>
public sealed record SendOrderLineRequest(int? ItemId, decimal? Quantity);

/// <summary>
/// Body of <c>POST /api/v1/production-orders/{number}/complete</c> (design 5.7, AC-19.1): what was made, what is left in
/// production storage, and why, for example
/// <c>{ "producedQuantity": 1001, "lines": [ { "itemId": 3, "leftover": 4 } ], "reason": "Last bag line jammed" }</c>.
/// </summary>
/// <param name="ProducedQuantity">
/// Finished product actually made, in the product's unit: greater than 0 (PRD section 6 row 7), at most 3 decimal places,
/// at most 999,999,999.999 (decision #80).
/// </param>
/// <param name="Lines">
/// The leftover of every order line exactly once, in any order (AC-19.1). A missing list counts as an empty one, so every
/// order line is reported as missing (AC-19.4).
/// </param>
/// <param name="Reason">
/// Why there is leftover: 1 to 500 characters after trimming, required when any leftover is above zero (AC-19.1, AC-19.4).
/// It may be omitted or <c>null</c> when every leftover is zero (design 5.7); one reason covers all materials (PRD
/// section 6 row 8).
/// </param>
/// <remarks>
/// Every field is nullable so that a missing value reaches <see cref="CompleteOrderService"/>, which answers with the
/// design's own messages under each field's JSON path (architecture 6, Validation).
/// <c>Reason</c> is required only when some leftover is above zero, so the schema cannot require it; its default,
/// <c>= null</c>, marks it optional in the OpenAPI description and changes nothing when JSON is read (decision #110).
/// </remarks>
public sealed record CompleteOrderRequest(
    decimal? ProducedQuantity, IReadOnlyList<CompleteOrderLineRequest?>? Lines, string? Reason = null);

/// <summary>One line of a completion: which raw material, and how much of it is physically left (design 5.7).</summary>
/// <param name="ItemId">The raw material; it must be on the order (AC-19.4).</param>
/// <param name="Leftover">
/// The <b>total</b> quantity of this material physically in production storage now, carried leftover included (PRD section
/// 6 row 1, decision #9): 0 or more, at most 3 decimal places, at most 999,999,999.999 (decision #80), and no more than
/// production storage holds (AC-19.4).
/// </param>
public sealed record CompleteOrderLineRequest(int? ItemId, decimal? Leftover);

/// <summary>
/// Body of <c>POST /api/v1/production-orders/{number}/cancel</c> (design 5.7, AC-21.1): why the order will not be
/// produced, for example <c>{ "reason": "Customer order withdrawn" }</c>.
/// </summary>
/// <param name="Reason">
/// Required: 1 to 500 characters after trimming (AC-21.1, AC-21.5, PRD assumption 9, design 2.6). Stored trimmed as the
/// order's cancellation reason, shown as the note of the Cancelled history entry, and quoted in the
/// OrderCancelledWithMaterial notification (design 7.3).
/// </param>
/// <remarks>
/// Nullable so that a missing value reaches <see cref="CancelOrderService"/>, which answers with the design's own message
/// under <c>reason</c> rather than a framework error (architecture 6, Validation).
/// </remarks>
public sealed record CancelOrderRequest(string? Reason);

/// <summary>
/// The full order as the API returns it (design 5.7, AC-14.2): from <c>GET /api/v1/production-orders/{number}</c>, from
/// <c>POST /api/v1/production-orders</c>, from <c>POST /api/v1/production-orders/{number}/send</c> (T-010), from
/// <c>POST /api/v1/production-orders/{number}/complete</c> (T-011), and from <c>POST /api/v1/production-orders/{number}/cancel</c>
/// (T-016).
/// </summary>
/// <remarks>
/// Every field is always present; a value that is not known yet is <c>null</c> (design 2.3, AC-14.2 "values not yet
/// known are empty"). Displayed percentages are the stored 6-place values rounded to 2 places (decision #58).
/// </remarks>
/// <param name="Number">The order number people say aloud, "order #12" (design 2.1).</param>
/// <param name="FinishedProductId">The finished product being made.</param>
/// <param name="FinishedProductName">Its name.</param>
/// <param name="Unit">Its unit, for example "Bag": the unit of the ordered and produced quantities and the output variance.</param>
/// <param name="OrderedQuantity">How much was ordered; never changes after creation (AC-13.6).</param>
/// <param name="Status">Created, InProduction, Completed or Cancelled (design 3.1).</param>
/// <param name="CreatedAt">When the order was created, UTC (AC-05.3).</param>
/// <param name="CreatedBy">The Manager who created it (AC-05.3).</param>
/// <param name="ProducedQuantity">Finished product actually made, set at completion (US-19).</param>
/// <param name="OutputVariance">D-9: produced − ordered, in <paramref name="Unit"/>.</param>
/// <param name="OutputVariancePercent">D-9: output variance as a percentage of the ordered quantity, 2 places.</param>
/// <param name="ToleranceAtCompletionPercent">AC-20.10: the tolerance in force when the order was completed.</param>
/// <param name="CompletionReason">The reason entered at completion, if any.</param>
/// <param name="CancellationReason">The reason entered at cancellation, if any.</param>
/// <param name="Lines">One line per raw material, in recipe line order.</param>
/// <param name="ComplementaryOrders">The order's complementary orders with their status (AC-14.2), oldest first.</param>
/// <param name="History">Who changed the status to what, and when (AC-05.3, AC-14.2), oldest first.</param>
public sealed record ProductionOrderResponse(
    int Number,
    int FinishedProductId,
    string FinishedProductName,
    string Unit,
    decimal OrderedQuantity,
    OrderStatus Status,
    DateTime CreatedAt,
    PersonResponse CreatedBy,
    decimal? ProducedQuantity,
    decimal? OutputVariance,
    decimal? OutputVariancePercent,
    decimal? ToleranceAtCompletionPercent,
    string? CompletionReason,
    string? CancellationReason,
    IReadOnlyList<ProductionOrderLineResponse> Lines,
    IReadOnlyList<LinkedComplementaryOrderResponse> ComplementaryOrders,
    IReadOnlyList<OrderHistoryEntryResponse> History);

/// <summary>
/// One raw material of an order (design 5.7 "lines", AC-14.2), with the figures fixed at creation, send and completion.
/// </summary>
/// <remarks>
/// The fields after <paramref name="Estimate"/> are <c>null</c> until the moment that fixes them (design 5.7 "Rules for
/// the line fields"): the send fixes leftover applied, warehouse send, complementary sends (starting at 0) and carried
/// leftover; completion fixes the rest.
/// </remarks>
/// <param name="ItemId">The raw material's id.</param>
/// <param name="ItemName">Its name.</param>
/// <param name="Unit">Its unit, for example "kg": the unit of every quantity on this line (design 2.4).</param>
/// <param name="AmountPerUnit">The recipe amount copied when the order was created (AC-13.1, AC-08.4).</param>
/// <param name="PlannedAmount">round3(amount per unit × ordered quantity), fixed at creation (AC-13.1).</param>
/// <param name="Estimate">AC-13.2: the labelled estimate while the order is Created; <c>null</c> in every other status.</param>
/// <param name="LeftoverApplied">D-6, fixed at send.</param>
/// <param name="WarehouseSend">The quantity sent from the Warehouse, fixed at send (may be 0).</param>
/// <param name="ComplementarySends">D-3: total sent by complementary orders; 0 at send, then growing.</param>
/// <param name="CarriedLeftover">D-8, fixed at send.</param>
/// <param name="LeftoverEntered">The physical total of leftover entered at completion (PRD section 6 row 1).</param>
/// <param name="LeftoverRemaining">D-9: leftover entered − carried leftover.</param>
/// <param name="ActualUsage">D-9.</param>
/// <param name="RequiredForActualOutput">D-9: amount per unit × produced quantity.</param>
/// <param name="UsageVariance">D-9: actual usage − required.</param>
/// <param name="UsageVariancePercent">D-9, 2 places; <c>null</c> when the required amount is 0 (decision #49).</param>
/// <param name="UsageVarianceAboveTolerance">AC-20.4: decided by the server from the unrounded percentage.</param>
public sealed record ProductionOrderLineResponse(
    int ItemId,
    string ItemName,
    string Unit,
    decimal AmountPerUnit,
    decimal PlannedAmount,
    LineEstimateResponse? Estimate,
    decimal? LeftoverApplied,
    decimal? WarehouseSend,
    decimal? ComplementarySends,
    decimal? CarriedLeftover,
    decimal? LeftoverEntered,
    decimal? LeftoverRemaining,
    decimal? ActualUsage,
    decimal? RequiredForActualOutput,
    decimal? UsageVariance,
    decimal? UsageVariancePercent,
    bool? UsageVarianceAboveTolerance);

/// <summary>
/// AC-13.2, design 5.7: the only block labelled as an estimate. It shows what the warehouse would send if the order were
/// sent now, and is recalculated on every read while the order is Created.
/// </summary>
/// <param name="LeftoverApplied">D-5: the free leftover the order would use, oldest Created order first.</param>
/// <param name="NominalSend">D-7: planned − estimated leftover applied; the amount to weigh out.</param>
/// <param name="SendRangeMin">D-7: the lowest send that would be accepted, rounded to 3 places.</param>
/// <param name="SendRangeMax">D-7: the highest send that would be accepted, rounded to 3 places.</param>
/// <param name="TolerancePercent">
/// D-1: the tolerance t the range was calculated with (2 means 2%), shown rounded to 2 places like every displayed
/// percentage (decision #94). The range limits use the configured value itself.
/// </param>
public sealed record LineEstimateResponse(
    decimal LeftoverApplied,
    decimal NominalSend,
    decimal SendRangeMin,
    decimal SendRangeMax,
    decimal TolerancePercent);

/// <summary>One complementary order of a production order, as listed in the order detail (AC-14.2, design 5.7).</summary>
/// <param name="Number">The complementary order's own number (design 2.1).</param>
/// <param name="Status">PendingApproval, Approved, Rejected, Sent or Cancelled.</param>
/// <param name="RequestedAt">When it was requested, UTC.</param>
public sealed record LinkedComplementaryOrderResponse(int Number, ComplementaryOrderStatus Status, DateTime RequestedAt);

/// <summary>
/// One status the order reached, with who and when (AC-05.3, design 5.7 "history"). Built from the order's timestamp and
/// user pairs; there is no history table, because each status is reached at most once (architecture 3.2).
/// </summary>
/// <param name="Status">The status reached.</param>
/// <param name="At">When, UTC.</param>
/// <param name="By">Who.</param>
/// <param name="Note">The completion or cancellation reason for those two statuses; otherwise <c>null</c>.</param>
public sealed record OrderHistoryEntryResponse(OrderStatus Status, DateTime At, PersonResponse By, string? Note);

/// <summary>
/// One row of <c>GET /api/v1/production-orders</c> (design 5.7, AC-14.1): the order without its lines.
/// </summary>
/// <param name="Number">The order number.</param>
/// <param name="FinishedProductId">The finished product being made.</param>
/// <param name="FinishedProductName">Its name.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="OrderedQuantity">How much was ordered.</param>
/// <param name="Status">The current status.</param>
/// <param name="CreatedAt">When the order was created; the list is newest first by this time (design 2.5).</param>
/// <param name="StatusChangedAt">When the status last changed (the creation time while Created).</param>
/// <param name="ProducedQuantity">Finished product made, once completed; otherwise <c>null</c>.</param>
public sealed record ProductionOrderSummaryResponse(
    int Number,
    int FinishedProductId,
    string FinishedProductName,
    string Unit,
    decimal OrderedQuantity,
    OrderStatus Status,
    DateTime CreatedAt,
    DateTime StatusChangedAt,
    decimal? ProducedQuantity);
