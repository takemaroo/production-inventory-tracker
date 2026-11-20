using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// Turns a stored leftover return into the response shape of design 5.9. One mapping for every status and every
/// endpoint, so the instruction, the reads, receive and cancel all return exactly the same shape.
/// </summary>
public static class ReturnMapping
{
    /// <summary>The response of design 5.9.</summary>
    /// <param name="leftoverReturn">
    /// The return with <c>Item.Unit</c>, <c>CreatedBy</c>, <c>ReceivedBy</c> and <c>CancelledBy</c> loaded (see
    /// <see cref="ReturnService"/>).
    /// </param>
    public static ReturnResponse ToResponse(LeftoverReturn leftoverReturn) => new(
        leftoverReturn.Number,
        leftoverReturn.ItemId,
        leftoverReturn.Item.Name,
        leftoverReturn.Item.Unit.Name,
        leftoverReturn.Quantity,
        leftoverReturn.Reason,
        leftoverReturn.ProductionOrderNumber,
        leftoverReturn.Status,
        History(leftoverReturn));

    /// <summary>
    /// AC-05.3, design 5.9: the status history, oldest first, from the three timestamp and user pairs (architecture 3.2).
    /// </summary>
    /// <remarks>
    /// A return goes Pending, then either Received or Cancelled, never both (design 3.1), so listing the filled pairs in
    /// that order is already oldest first.
    /// </remarks>
    private static List<ReturnHistoryEntryResponse> History(LeftoverReturn leftoverReturn)
    {
        var history = new List<ReturnHistoryEntryResponse>
        {
            new(ReturnStatus.Pending, leftoverReturn.CreatedAt, Person(leftoverReturn.CreatedBy), Note: null),
        };

        if (leftoverReturn is { ReceivedAt: { } receivedAt, ReceivedBy: { } receivedBy })
        {
            history.Add(new(ReturnStatus.Received, receivedAt, Person(receivedBy), Note: null));
        }

        if (leftoverReturn is { CancelledAt: { } cancelledAt, CancelledBy: { } cancelledBy })
        {
            history.Add(new(ReturnStatus.Cancelled, cancelledAt, Person(cancelledBy), Note: null));
        }

        return history;
    }

    /// <summary>Design 2.3 "People": a user as <c>{ id, email }</c>. Identity's Email is nullable in its type, never in this app.</summary>
    private static PersonResponse Person(AppUser user) => new(user.Id, user.Email!);
}
