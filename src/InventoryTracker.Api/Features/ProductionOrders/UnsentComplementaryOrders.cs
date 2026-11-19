using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// The automatic cancellation of a production order's unsent complementary orders when the order ends (PRD section 6
/// row 6): on completion (AC-19.6, <see cref="CompleteOrderService"/>) and on cancellation (AC-21.4,
/// <see cref="CancelOrderService"/>). One implementation for both, so the two can never disagree on which statuses count
/// as unsent or on what is recorded (the same reasoning as decisions #93, #97 and #100).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why they are cancelled.</b> A complementary order exists only to top up the material of a running order (US-16).
/// Once that order is completed or cancelled there is no run left to top up, so a request still waiting for approval
/// (PendingApproval) or approved but not yet sent (Approved) could never be used, and sending it would put material into
/// production storage for nothing. Rejected, Sent and Cancelled ones are already final and stay exactly as they are.
/// </para>
/// <para>
/// A <c>static</c> class with the request's <c>AppDbContext</c> passed in, like <see cref="WaitingOrderQueue"/>: it holds
/// no state, and passing the DbContext makes it plain that the changes belong to the caller's write transaction. It only
/// changes tracked objects; the caller's next <c>SaveChangesAsync</c> writes them, together with the parent order's own
/// change, so both are committed or neither is (architecture 5.2).
/// </para>
/// </remarks>
public static class UnsentComplementaryOrders
{
    /// <summary>AC-19.6: the note on each complementary order cancelled because its parent was completed.</summary>
    public const string ParentCompletedNote = "Parent order completed";

    /// <summary>AC-21.4: the note on each complementary order cancelled because its parent was cancelled.</summary>
    public const string ParentCancelledNote = "Parent order cancelled";

    /// <summary>
    /// Makes every PendingApproval or Approved complementary order of production order <paramref name="orderNumber"/>
    /// Cancelled, with <paramref name="note"/>, recording the user who ended the parent and when (AC-05.3, architecture
    /// 3.2 "CancelledAt / CancelledById: the user who completed or cancelled the parent").
    /// </summary>
    /// <param name="db">The request's DbContext, inside its open write transaction.</param>
    /// <param name="orderNumber">The parent order being completed or cancelled.</param>
    /// <param name="note"><see cref="ParentCompletedNote"/> or <see cref="ParentCancelledNote"/>.</param>
    /// <param name="now">The time of the completion or cancellation, from <c>Clock</c>, so both records show the same moment.</param>
    /// <param name="userId">The user who completed or cancelled the parent.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public static async Task CancelAsync(
        AppDbContext db, int orderNumber, string note, DateTime now, string userId, CancellationToken cancellationToken)
    {
        // Loaded tracked (no AsNoTracking), because they are changed below and saved by the caller.
        var unsent = await db.ComplementaryOrders
            .Where(complementary => complementary.ParentOrderNumber == orderNumber
                && (complementary.Status == ComplementaryOrderStatus.PendingApproval
                    || complementary.Status == ComplementaryOrderStatus.Approved))
            .ToListAsync(cancellationToken);

        foreach (var complementary in unsent)
        {
            complementary.Status = ComplementaryOrderStatus.Cancelled;
            complementary.CancelledAt = now;
            complementary.CancelledById = userId;
            complementary.CancellationNote = note;
        }
    }
}
