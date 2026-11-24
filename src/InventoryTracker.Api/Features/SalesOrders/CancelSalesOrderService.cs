using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) Cancelling a sales order (US-27): a Seller or Manager stops a Created sales order that will not be shipped,
/// with a reason, so the Warehouse no longer sees it as work to do and it no longer counts as open demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a cancellation does, in plain words.</b> It changes one record and never stock: no movement is written
/// (AC-27.1). The sales order becomes Cancelled with who cancelled it, when and why. Everything else follows from the
/// status alone, because nothing about a sales order is stored anywhere else: open demand is summed from the Created
/// sales orders every time it is read (D-12, architecture 3.5), so a Cancelled one drops out of it at once. Cancelled is
/// final (D-10): a cancelled sales order never ships (AC-27.3), and a Shipped one cannot be cancelled (decision #142,
/// the goods have gone; a customer return is a Manager's Warehouse adjustment instead).
/// </para>
/// <para>
/// <b>Who may cancel</b> (decision #146): any Seller or Manager, whoever created the sales order (AC-27.1). The role
/// check is the controller's <c>[Authorize]</c>; this class does not look at the creator at all. Nor does it look at the
/// customer's active status: a deactivated customer's Created sales order can still be cancelled (AC-29.4).
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). The read, the checks and the write run inside one
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>, which on SQLite takes the database's single write lock at
/// its start. A cancel and a ship of the same sales order therefore never interleave: whichever takes the lock first
/// finishes and commits, and the other then reads the status it left (AC-26.10, third bullet). A cancel that loses sees
/// Shipped and gets <c>already-shipped</c>; a ship that loses sees Cancelled and gets <c>sales-order-cancelled</c>,
/// with no movement. Two cancels at once work the same way: the second sees Cancelled.
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1 v1.1): the sales order in the URL (<c>404</c>), the reason
/// (<c>400</c>), the status (<c>409</c>), then the write. There is no line cap (the body carries no lines), and no range
/// or stock to check, since nothing moves. The first failing step decides the answer, and nothing is saved.
/// </para>
/// <para>
/// It takes its helpers through its constructor (dependency injection, decision #77), like
/// <see cref="ShipSalesOrderService"/>: <see cref="SalesOrderService"/> for the response, so the cancel answers with
/// exactly the detail <c>GET /sales-orders/{number}</c> shows (decision #161), and the <see cref="Clock"/> for the time.
/// It needs no <c>StockLedger</c> or <c>StockQueries</c>, because it neither writes nor reads stock.
/// </para>
/// </remarks>
public sealed class CancelSalesOrderService(
    AppDbContext db,
    SalesOrderService salesOrders,
    Clock clock,
    ILogger<CancelSalesOrderService> logger)
{
    /// <summary>
    /// AC-27.1: cancels Created sales order <paramref name="number"/> with a reason, recording who cancelled it and when.
    /// Moves no stock. Returns the sales order detail (design 5.12), now Cancelled, with <c>cancellationReason</c> and a
    /// Cancelled <c>history</c> entry whose note is the reason.
    /// </summary>
    /// <param name="number">The sales order number from the URL.</param>
    /// <param name="request">The body, with the reason.</param>
    /// <param name="userId">The signed-in Seller or Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c> "Sales order #{number} not found.".</exception>
    /// <exception cref="ValidationException"><c>400</c>: the reason is missing, blank or over 500 characters (AC-27.2).</exception>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>already-shipped</c> or <c>sales-order-cancelled</c>: the sales order is Shipped or already Cancelled
    /// (AC-27.2).
    /// </exception>
    public async Task<SalesOrderResponse> CancelAsync(
        int number, CancelSalesOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        var detail = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the record named in the URL. Tracked (no AsNoTracking), because the cancellation writes
            // its status onto it. Its lines are not needed: nothing about them changes and no stock is read.
            var salesOrder = await db.SalesOrders
                .SingleOrDefaultAsync(order => order.Number == number, cancellationToken)
                ?? throw new NotFoundException($"Sales order #{number} not found.");

            // Step 4: the body, before the state (AC-27.2), so a blank reason on a Shipped sales order is 400, not 409.
            var reason = ValidateReason(request);

            // Step 5: the state (AC-27.2).
            CheckCreated(salesOrder);

            // AC-27.1, AC-05.3: Cancelled, recording who, when and why. One clock read, so CancelledAt and
            // StatusChangedAt (the list's statusChangedAt, design 5.12) are the same instant. No movement is written and
            // the lines stay as they are: the sales order is simply no longer Created, which is all open demand looks at
            // (D-12).
            var now = clock.UtcNow;
            salesOrder.Status = SalesOrderStatus.Cancelled;
            salesOrder.CancelledAt = now;
            salesOrder.CancelledById = userId;
            salesOrder.CancellationReason = reason;
            salesOrder.StatusChangedAt = now;

            // ReadDetailAsync reads the database, not the tracked object, so the change is saved first. Saved inside the
            // open transaction: visible to this request's query, invisible to everyone else until the commit.
            await db.SaveChangesAsync(cancellationToken);
            return await salesOrders.ReadDetailAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6 Logging, decision #163: logged only after the commit, so the log never reports a cancellation
        // that was rolled back. Only the number and the user id; the reason is free text and stays out of the log.
        logger.LogInformation("Sales order {SalesOrderNumber} cancelled by user {UserId}.", number, userId);
        return detail;
    }

    /// <summary>
    /// Design 2.2 step 4, AC-27.1, AC-27.2, design 2.6: a reason is required, 1 to 500 characters after trimming.
    /// Missing, <c>null</c>, empty or only spaces gives <c>400</c> <c>validation</c> "Reason is required."; longer than
    /// 500 gives "Reason must be 500 characters or fewer." (design 7.2), both under <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// The reason is required because a cancellation ends the sales order for good: whoever reads its history later needs
    /// to know why it was never shipped (US-27, "the reason is on record").
    /// </remarks>
    /// <returns>The trimmed reason, as it is stored and shown.</returns>
    private static string ValidateReason(CancelSalesOrderRequest request)
    {
        var errors = new ValidationErrors();

        // Text.Clean trims and turns empty or all-spaces into null (design 2.6), so "   " counts as missing.
        var reason = Text.Clean(request.Reason);
        if (reason is null)
        {
            errors.Add("reason", "Reason is required.");
        }
        // Decision #116: counted in Unicode code points, so an emoji is one character (Text.Length).
        else if (Text.Length(reason) > ColumnRules.TextLength)
        {
            errors.Add("reason", $"Reason must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();

        // ThrowIfAny has thrown if the reason was missing, so it is not null here; "!" tells the compiler so.
        return reason!;
    }

    /// <summary>
    /// Design 2.2 step 5, AC-27.2: only a Created sales order can be cancelled. Shipped and Cancelled are final (D-10).
    /// </summary>
    /// <remarks>
    /// The codes are the ship's (<see cref="ShipSalesOrderService.AlreadyShippedCode"/>,
    /// <see cref="ShipSalesOrderService.SalesOrderCancelledCode"/>), because a code names the sales order's state, but the
    /// sentences fit this action (design 7.2: "Codes name the state, messages fit the action"). A program checks the
    /// code; a person reads the sentence.
    /// </remarks>
    private static void CheckCreated(SalesOrder salesOrder)
    {
        switch (salesOrder.Status)
        {
            case SalesOrderStatus.Shipped:
                // AC-27.2, decision #142: the goods have gone. Also what a cancel gets when a ship of the same sales order
                // won the race (AC-26.10, third bullet), because it reads the status only after the ship has committed.
                throw new ConflictException(ShipSalesOrderService.AlreadyShippedCode,
                    "This sales order has been shipped and cannot be cancelled.");
            case SalesOrderStatus.Cancelled:
                // AC-27.2: also what the loser of two simultaneous cancels gets, for the same reason.
                throw new ConflictException(ShipSalesOrderService.SalesOrderCancelledCode,
                    "This sales order is already cancelled.");
        }
    }
}
