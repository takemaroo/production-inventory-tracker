using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// Body of <c>POST /api/v1/returns</c> (design 5.9, AC-22.1): a Manager's instruction to bring leftover raw material back
/// from production storage to the Warehouse, for example
/// <c>{ "itemId": 3, "quantity": 20, "reason": "Order #9 cancelled, cheese not needed this week", "orderNumber": 9 }</c>.
/// </summary>
/// <param name="ItemId">Required: the raw material to return (AC-22.1).</param>
/// <param name="Quantity">
/// Required: how much, in the material's unit. Greater than 0, at most 3 decimal places, at most 999,999,999.999
/// (AC-22.1, PRD 4.1, decision #80), and at most the material's free leftover (AC-22.2).
/// </param>
/// <param name="Reason">Required: 1 to 500 characters after trimming (AC-22.1, AC-22.2, design 2.6).</param>
/// <param name="OrderNumber">
/// Optional: a production order the return relates to, in any status (AC-22.1, design 5.9), for example a cancelled one.
/// It must exist. When the return is received, its movement carries this order number (decision #107).
/// </param>
/// <remarks>
/// Every field is nullable so that a missing value reaches <see cref="InstructReturnService"/>, which answers with the
/// design's own messages under each field's JSON path rather than a framework error (architecture 6, Validation).
/// </remarks>
public sealed record ReturnRequest(int? ItemId, decimal? Quantity, string? Reason, int? OrderNumber);

/// <summary>
/// A leftover return as the API returns it (design 5.9): from the instruction (<c>201</c>), from <c>GET</c> (one, or each
/// row of the list), and from receive and cancel (<c>200</c>).
/// </summary>
/// <remarks>Every field is always present; a value that does not apply is <c>null</c> (design 2.3).</remarks>
/// <param name="Number">The return's own number, "return #5" (design 2.1).</param>
/// <param name="ItemId">The raw material's id.</param>
/// <param name="ItemName">Its name.</param>
/// <param name="Unit">Its unit, for example "kg" (design 2.4: the unit travels with the quantity).</param>
/// <param name="Quantity">The quantity to return, or returned once Received.</param>
/// <param name="Reason">Why it is returned, as entered (trimmed).</param>
/// <param name="OrderNumber">The production order it is linked to, or <c>null</c>.</param>
/// <param name="Status">Pending, Received or Cancelled (design 3.1).</param>
/// <param name="History">Each status reached, with who and when (AC-05.3), oldest first.</param>
public sealed record ReturnResponse(
    int Number,
    int ItemId,
    string ItemName,
    string Unit,
    decimal Quantity,
    string Reason,
    int? OrderNumber,
    ReturnStatus Status,
    IReadOnlyList<ReturnHistoryEntryResponse> History);

/// <summary>
/// One status a return reached, with who and when (AC-05.3, design 5.9 "history"). Built from the timestamp and user pairs
/// on the return; each status is reached at most once (architecture 3.2), so no history table is needed.
/// </summary>
/// <param name="Status">The status reached.</param>
/// <param name="At">When, UTC.</param>
/// <param name="By">Who: the Manager who instructed or cancelled it, or the user who recorded receiving it.</param>
/// <param name="Note">
/// Always <c>null</c> for a return: no step of a return takes a note (receive and cancel have no body, design 5.9). The
/// field is kept so every history in the API has the same shape (design 5.7, 5.8, 5.9).
/// </param>
public sealed record ReturnHistoryEntryResponse(ReturnStatus Status, DateTime At, PersonResponse By, string? Note);
