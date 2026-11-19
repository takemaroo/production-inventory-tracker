using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.ComplementaryOrders;

/// <summary>
/// Body of <c>POST /api/v1/production-orders/{number}/complementary-orders</c> (design 5.8, AC-16.1): why extra material is
/// needed and how much of which materials, for example
/// <c>{ "reason": "Hopper spill at start of run", "lines": [ { "itemId": 3, "requestedAmount": 20 } ] }</c>.
/// </summary>
/// <param name="Reason">Required: 1 to 500 characters after trimming (AC-16.1, AC-16.3, design 2.6).</param>
/// <param name="Lines">
/// One or more materials already on the parent order, each at most once (AC-16.3). Unlike a send, a request names only
/// the materials that ran short, not every order line. A missing list counts as an empty one.
/// </param>
/// <remarks>
/// Every field is nullable so that a missing value reaches <see cref="RequestComplementaryOrderService"/>, which answers
/// with the design's own messages under each field's JSON path rather than a framework error (architecture 6,
/// Validation).
/// </remarks>
public sealed record ComplementaryOrderRequest(string? Reason, IReadOnlyList<ComplementaryOrderLineRequest?>? Lines);

/// <summary>One requested material (design 5.8).</summary>
/// <param name="ItemId">The raw material; it must be on the parent order (AC-16.3).</param>
/// <param name="RequestedAmount">
/// How much extra is wanted, in the material's unit: greater than 0, at most 3 decimal places, at most 999,999,999.999
/// (AC-16.3, decision #80). The send range of AC-18.1 is based on it.
/// </param>
public sealed record ComplementaryOrderLineRequest(int? ItemId, decimal? RequestedAmount);

/// <summary>
/// Body of <c>POST /api/v1/complementary-orders/{number}/reject</c> (design 5.8, AC-17.2), for example
/// <c>{ "note": "Use the spare from order #11" }</c>. The body itself is optional, and so is the note.
/// </summary>
/// <param name="Note">
/// Why the request was turned down: optional, up to 500 characters after trimming (design 2.6). Blank counts as no note.
/// Stored as the complementary order's <c>decisionNote</c>.
/// </param>
public sealed record RejectComplementaryOrderRequest(string? Note);

/// <summary>
/// A complementary order as the API returns it (design 5.8): from the request (<c>201</c>), from <c>GET</c> (one or a
/// list), and from approve and reject (<c>200</c>).
/// </summary>
/// <remarks>
/// AC-16.5: a complementary order has no ordered or produced quantity, so the shape has neither; it only carries
/// requested amounts and, once Sent (T-018), the quantities sent. Every field is always present; a value not known yet is
/// <c>null</c> (design 2.3).
/// </remarks>
/// <param name="Number">The complementary order's own number, "complementary order #3" (design 2.1).</param>
/// <param name="OrderNumber">The parent production order it tops up (AC-16.1).</param>
/// <param name="Status">PendingApproval, Approved, Rejected, Sent or Cancelled (design 3.1).</param>
/// <param name="Reason">Why extra material was needed, as entered (trimmed).</param>
/// <param name="RequestedAt">When it was requested, UTC (AC-16.1, AC-05.3).</param>
/// <param name="RequestedBy">Who requested it (AC-16.1, AC-05.3).</param>
/// <param name="DecisionNote">The optional note given with a rejection (AC-17.2); otherwise <c>null</c>.</param>
/// <param name="CancellationNote">"Parent order completed" or "Parent order cancelled" once cancelled automatically (AC-19.6, AC-21.4).</param>
/// <param name="Lines">The requested materials, in the order they were requested.</param>
/// <param name="History">Each status reached, with who and when (AC-05.3), oldest first.</param>
public sealed record ComplementaryOrderResponse(
    int Number,
    int OrderNumber,
    ComplementaryOrderStatus Status,
    string Reason,
    DateTime RequestedAt,
    PersonResponse RequestedBy,
    string? DecisionNote,
    string? CancellationNote,
    IReadOnlyList<ComplementaryOrderLineResponse> Lines,
    IReadOnlyList<ComplementaryOrderHistoryEntryResponse> History);

/// <summary>One requested material of a complementary order (design 5.8 "lines").</summary>
/// <param name="ItemId">The raw material's id.</param>
/// <param name="ItemName">Its name.</param>
/// <param name="Unit">Its unit, for example "kg" (design 2.4: the unit travels with the quantity).</param>
/// <param name="RequestedAmount">The amount requested.</param>
/// <param name="SendRangeMin">
/// AC-18.1: the lowest send that will be accepted, requested × (1 − t), rounded to 3 places, at the current tolerance.
/// Shown only while the order can still be sent, PendingApproval or Approved; <c>null</c> otherwise (design 5.8).
/// </param>
/// <param name="SendRangeMax">AC-18.1: the highest send that will be accepted, requested × (1 + t); same rule.</param>
/// <param name="SentQuantity">The quantity sent, once Sent (T-018); otherwise <c>null</c>.</param>
public sealed record ComplementaryOrderLineResponse(
    int ItemId,
    string ItemName,
    string Unit,
    decimal RequestedAmount,
    decimal? SendRangeMin,
    decimal? SendRangeMax,
    decimal? SentQuantity);

/// <summary>
/// One status a complementary order reached, with who and when (AC-05.3, design 5.8 "history"). Built from the
/// timestamp and user pairs; each status is reached at most once (architecture 3.2), so no history table is needed.
/// </summary>
/// <param name="Status">The status reached.</param>
/// <param name="At">When, UTC.</param>
/// <param name="By">Who. For Cancelled, the user who completed or cancelled the parent order.</param>
/// <param name="Note">The rejection note for Rejected, the cancellation note for Cancelled; otherwise <c>null</c>.</param>
public sealed record ComplementaryOrderHistoryEntryResponse(
    ComplementaryOrderStatus Status, DateTime At, PersonResponse By, string? Note);
