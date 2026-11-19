using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Notifications;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Cancelling a production order (US-21): a Manager stops an order that will not be produced, with a reason, so that it
/// no longer holds leftover or blocks the line.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a cancellation does, in plain words.</b> It changes records, never stock: no movement is written (AC-21.2,
/// AC-21.3). The order becomes Cancelled with who, when and why (AC-21.1), and every figure fixed so far stays on it
/// (design 5.7). Everything else follows from the status alone, because stock figures are worked out from the statuses
/// every time they are read (architecture 3.3):
/// </para>
/// <list type="bullet">
/// <item><b>A Created order</b> stops being one of the waiting orders that share the free leftover, oldest first (D-5).
/// Its share is therefore released to the next oldest Created order the next time anyone reads it (AC-21.2). AC-15.5:
/// with 50 kg of free Cheese, #7 (30 kg planned) was owed 30 kg and #8 got the other 20; once #7 is cancelled, #8's
/// estimate shows leftover 50, nominal send 950, range 930 to 970.</item>
/// <item><b>An InProduction order</b> stops being the running order, so the material sent for it is no longer committed
/// to it (D-3): everything in production storage becomes free leftover again (D-4) for the next order (AC-21.3), and
/// the "one order in production" place is free, so another order can be sent (AC-15.10, decision #6). Because that
/// material is now just sitting there, the Managers are told what is left and why (one OrderCancelledWithMaterial
/// notification, AC-21.3), so someone decides whether to leave it for the next order, return it (US-22) or write it off
/// (US-12) (design 3.8).</item>
/// </list>
/// <para>
/// Its PendingApproval and Approved complementary orders become Cancelled too, with "Parent order cancelled" (AC-21.4,
/// PRD section 6 row 6), through the code the completion uses (<see cref="UnsentComplementaryOrders"/>).
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). As for the send and the completion, every read, check
/// and write runs inside one <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the
/// database's single write lock until the commit. So a cancellation and a send or a completion of the same order never
/// interleave: whichever takes the lock first finishes, and the other then sees its result (a send after a cancellation
/// gets <c>order-cancelled</c>; a cancellation after a completion gets <c>order-completed</c>). The status change, the
/// complementary orders and the notification are committed together or not at all ("Notifications match events").
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1): the order in the URL (<c>404</c>), the reason (<c>400</c>
/// <c>validation</c>), then the order's state (<c>409</c>: it must be Created or InProduction). There are no ranges or
/// stock to check, since nothing moves.
/// </para>
/// <para>
/// It takes its collaborators through the constructor (dependency injection, decision #77): <see cref="StockQueries"/>
/// for the production storage figures of the notification, and <see cref="ProductionOrderService"/> for the response, so
/// it returns exactly what <c>GET /production-orders/{number}</c> shows (decision #92). It needs no
/// <see cref="StockLedger"/>, because it writes no movement.
/// </para>
/// </remarks>
public sealed class CancelOrderService(
    AppDbContext db,
    StockQueries stock,
    ProductionOrderService orders,
    Clock clock,
    ILogger<CancelOrderService> logger)
{
    /// <summary>Design 7.2: the code for cancelling a Completed order (AC-21.5).</summary>
    public const string OrderCompletedCode = "order-completed";

    /// <summary>
    /// AC-21.1: cancels Created or InProduction order <paramref name="number"/> with a reason, recording who and when.
    /// Moves no stock (AC-21.2, AC-21.3), cancels the order's unsent complementary orders (AC-21.4), and, for an
    /// InProduction order with any of its materials above zero in production storage, creates one
    /// OrderCancelledWithMaterial notification (AC-21.3). Returns the order detail (design 5.7), now Cancelled.
    /// </summary>
    /// <param name="number">The order number from the URL.</param>
    /// <param name="request">The body, with the reason.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no order has this number.</exception>
    /// <exception cref="ValidationException"><c>400</c> <c>validation</c>: the reason is missing, blank or too long (AC-21.5).</exception>
    /// <exception cref="ConflictException"><c>409</c>: the order is Completed or already Cancelled (AC-21.5).</exception>
    public async Task<ProductionOrderResponse> CancelAsync(
        int number, CancelOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        var detail = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the record named in the URL, tracked because the cancellation writes onto it. Its lines
            // come with their materials and units, for the notification.
            var order = await db.ProductionOrders
                .Include(candidate => candidate.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw new NotFoundException($"Order #{number} not found.");

            // Step 4: the body (AC-21.1, AC-21.5). Step 5: the order's state (AC-21.5).
            var reason = ValidateReason(request);
            CheckState(order);

            // Read before the status changes below: only an order that was running leaves material behind for which it
            // was responsible (AC-21.3).
            var wasInProduction = order.Status == OrderStatus.InProduction;
            var now = clock.UtcNow;

            // AC-21.1, AC-05.3: Cancelled, recording who, when and why. Nothing else on the order or its lines changes:
            // "values fixed so far stay; the rest stay null" (design 5.7), and no movement is written (AC-21.2, AC-21.3).
            order.Status = OrderStatus.Cancelled;
            order.CancelledAt = now;
            order.CancelledById = userId;
            order.CancellationReason = reason;
            order.StatusChangedAt = now;

            // AC-21.4: the order's PendingApproval and Approved complementary orders can never be used now.
            await UnsentComplementaryOrders.CancelAsync(
                db, order.Number, UnsentComplementaryOrders.ParentCancelledNote, now, userId, cancellationToken);

            if (wasInProduction)
            {
                await AddMaterialNotificationAsync(order, reason, now, cancellationToken);
            }

            // ReadDetailAsync reads the database, not the tracked objects, so everything is saved first, still inside
            // the open transaction (decision #92).
            await db.SaveChangesAsync(cancellationToken);
            return await orders.ReadDetailAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change is logged with the order number and the user, only after the
        // commit, so the log never reports a cancellation that was rolled back.
        logger.LogInformation("Order {OrderNumber} cancelled by user {UserId}.", number, userId);
        return detail;
    }

    /// <summary>
    /// Design 2.2 step 4, AC-21.1, AC-21.5, PRD assumption 9: a reason is required, 1 to 500 characters after trimming
    /// (design 2.6). Missing, <c>null</c>, empty or only spaces gives <c>400</c> <c>validation</c> "Reason is required.";
    /// longer than 500 gives "Reason must be 500 characters or fewer." (design 7.2), both under <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// The reason is required because a cancellation ends an order for good: the Managers who read the history or the
    /// OrderCancelledWithMaterial notification later need to know why the order was never produced.
    /// </remarks>
    /// <returns>The trimmed reason, as it is stored and shown.</returns>
    private static string ValidateReason(CancelOrderRequest request)
    {
        var errors = new ValidationErrors();
        var reason = Text.Clean(request.Reason);
        if (reason is null)
        {
            errors.Add("reason", "Reason is required.");
        }
        else if (reason.Length > ColumnRules.TextLength)
        {
            errors.Add("reason", $"Reason must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();

        // ThrowIfAny has thrown if the reason was missing, so it is not null here; "!" tells the compiler so.
        return reason!;
    }

    /// <summary>
    /// Design 2.2 step 5, AC-21.1, AC-21.5: only a Created or InProduction order can be cancelled. A Completed order is
    /// <c>409</c> <c>order-completed</c> "Order #{n} is completed and cannot be cancelled." (its output is registered and
    /// its stock has moved, so there is nothing left to stop); a Cancelled one is <c>409</c> <c>order-cancelled</c> "This
    /// order is cancelled.", which is also what the loser of two simultaneous cancellations gets.
    /// </summary>
    private static void CheckState(ProductionOrder order)
    {
        switch (order.Status)
        {
            case OrderStatus.Completed:
                throw new ConflictException(OrderCompletedCode,
                    $"Order #{order.Number} is completed and cannot be cancelled.");
            case OrderStatus.Cancelled:
                throw new ConflictException(ErrorCodes.OrderCancelled, "This order is cancelled.");
        }
    }

    /// <summary>
    /// AC-21.3, AC-23.2: when at least one of the order's materials has stock above zero in production storage, adds one
    /// OrderCancelledWithMaterial notification, through <see cref="NotificationComposer"/> (T-005), listing each such
    /// material with its production storage quantity and unit, in recipe line order, and the reason (design 5.10, 7.3).
    /// When every material is at zero, nothing is added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The quantity is everything of that material physically in production storage (design 3.8: "has stock above zero
    /// in production storage"), not only what was sent for this order. Leftover carried from earlier runs (D-8) sits in
    /// the same place and is just as free now; the Manager deciding what to do with the material needs the whole amount.
    /// </para>
    /// <para>
    /// Read inside this write transaction, so no other request can move the stock between this read and the commit
    /// (architecture 5.2). Production storage is a sum of movements and the cancellation writes none, so it is the same
    /// before and after the status change.
    /// </para>
    /// </remarks>
    private async Task AddMaterialNotificationAsync(
        ProductionOrder order, string reason, DateTime now, CancellationToken cancellationToken)
    {
        // Architecture 5.1: one query for all of the order's materials, filtered by item, never a loop.
        var materialIds = order.Lines.Select(line => line.RawMaterialId).ToList();
        var figures = await stock.GetFiguresAsync(materialIds, cancellationToken);

        var materials = order.Lines
            .Select(line => new NotificationMaterial(
                line.LineNo, line.RawMaterialId, line.RawMaterial.Name, line.RawMaterial.Unit.Name,
                figures[line.RawMaterialId].ProductionStorage))
            .ToList();

        // The composer decides what the notification says; this method decides whether there is one (decision #59). It
        // leaves out the materials at zero and refuses a list where nothing is above zero, so check first.
        if (materials.Any(material => material.Quantity > 0m))
        {
            db.Notifications.Add(NotificationComposer.OrderCancelledWithMaterial(order.Number, materials, reason, now));
        }
    }
}
