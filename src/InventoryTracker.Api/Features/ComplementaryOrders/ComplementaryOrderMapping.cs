using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;

namespace InventoryTracker.Api.Features.ComplementaryOrders;

/// <summary>
/// Turns a stored complementary order into the response shape of design 5.8. One mapping for every status and every
/// endpoint, so the request, the reads, approve and reject (and T-018's send) all return exactly the same shape.
/// </summary>
/// <remarks>
/// It calculates only two display rules: the send range of each line (AC-18.1), through the one implementation in
/// <see cref="ProductionCalculations.ComplementaryRange"/>, and the status history, built from the timestamp pairs.
/// </remarks>
public static class ComplementaryOrderMapping
{
    /// <summary>The response of design 5.8.</summary>
    /// <param name="order">
    /// The complementary order with <c>RequestedBy</c>, <c>ApprovedBy</c>, <c>RejectedBy</c>, <c>SentBy</c>,
    /// <c>CancelledBy</c> and <c>Lines</c> with each <c>RawMaterial.Unit</c> loaded (see <see cref="ComplementaryOrderService"/>).
    /// </param>
    /// <param name="tolerancePercent">The current tolerance t (D-1), for the send ranges.</param>
    public static ComplementaryOrderResponse ToResponse(ComplementaryOrder order, decimal tolerancePercent)
    {
        // Design 5.8: the range is what the warehouse would have to send (AC-18.1). It is shown only while a send is
        // still possible: once Rejected, Sent or Cancelled the order can never be sent, so a range would mislead. It uses
        // the tolerance in force now, because that is the tolerance the send will be checked against.
        var showRange = order.Status is ComplementaryOrderStatus.PendingApproval or ComplementaryOrderStatus.Approved;

        var lines = order.Lines
            .OrderBy(line => line.LineNo)
            .Select(line =>
            {
                var range = showRange ? ProductionCalculations.ComplementaryRange(line.RequestedAmount, tolerancePercent) : null;
                return new ComplementaryOrderLineResponse(
                    line.RawMaterialId,
                    line.RawMaterial.Name,
                    line.RawMaterial.Unit.Name,
                    line.RequestedAmount,
                    range?.Min,
                    range?.Max,
                    line.SentQuantity);
            })
            .ToList();

        return new ComplementaryOrderResponse(
            order.Number,
            order.ParentOrderNumber,
            order.Status,
            order.Reason,
            order.RequestedAt,
            Person(order.RequestedBy),
            order.DecisionNote,
            order.CancellationNote,
            lines,
            History(order));
    }

    /// <summary>
    /// AC-05.3, design 5.8: the status history, oldest first, from the five timestamp and user pairs (architecture 3.2).
    /// </summary>
    /// <remarks>
    /// Each status is reached at most once, along one of the paths of design 3.1: PendingApproval, then Approved or
    /// Rejected, then (from Approved) Sent; or Cancelled from PendingApproval or Approved. So listing the filled pairs in
    /// that order is already oldest first. The note is what the user gave with the step: the rejection note, or the
    /// automatic cancellation's "Parent order completed" / "Parent order cancelled".
    /// </remarks>
    private static List<ComplementaryOrderHistoryEntryResponse> History(ComplementaryOrder order)
    {
        var history = new List<ComplementaryOrderHistoryEntryResponse>
        {
            new(ComplementaryOrderStatus.PendingApproval, order.RequestedAt, Person(order.RequestedBy), Note: null),
        };

        if (order is { ApprovedAt: { } approvedAt, ApprovedBy: { } approvedBy })
        {
            history.Add(new(ComplementaryOrderStatus.Approved, approvedAt, Person(approvedBy), Note: null));
        }

        if (order is { RejectedAt: { } rejectedAt, RejectedBy: { } rejectedBy })
        {
            history.Add(new(ComplementaryOrderStatus.Rejected, rejectedAt, Person(rejectedBy), order.DecisionNote));
        }

        if (order is { SentAt: { } sentAt, SentBy: { } sentBy })
        {
            history.Add(new(ComplementaryOrderStatus.Sent, sentAt, Person(sentBy), Note: null));
        }

        if (order is { CancelledAt: { } cancelledAt, CancelledBy: { } cancelledBy })
        {
            history.Add(new(ComplementaryOrderStatus.Cancelled, cancelledAt, Person(cancelledBy), order.CancellationNote));
        }

        return history;
    }

    /// <summary>Design 2.3 "People": a user as <c>{ id, email }</c>. Identity's Email is nullable in its type, never in this app.</summary>
    private static PersonResponse Person(AppUser user) => new(user.Id, user.Email!);
}
