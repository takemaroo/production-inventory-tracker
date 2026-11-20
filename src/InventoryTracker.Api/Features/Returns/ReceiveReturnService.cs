using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// The warehouse records receiving a Pending leftover return (AC-22.4): the material has been carried back, so one
/// ReturnedToWarehouse movement takes the quantity out of production storage and puts it into the Warehouse, and the
/// return becomes Received.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once only, even under simultaneous requests</b> (architecture 5.2, AC-22.5). The status check, the movement and the
/// status change run inside one <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the
/// database's single write lock from the first read to the commit. A second receive, or a cancel, arriving at the same
/// moment waits, then finds Received and gets <c>409</c> <c>not-pending</c>; the material moves exactly once.
/// </para>
/// <para>
/// <b>When production storage holds less than the return</b> (decision #18, design section 9 question 2). Adjustments are
/// not blocked by Pending returns (AC-12.3 is unchanged), so a write-off after the instruction can leave production storage
/// with less than the return's quantity. Receiving it would then take production storage below zero, so it is refused
/// with <c>409</c> <c>insufficient-stock</c> and the design's longer message, which tells the Manager what to do: cancel it
/// and instruct a new one for what is really there, or adjust the stock. A write-off and a receive arriving together are
/// serialized by the same lock, so whichever is second sees the first one's result.
/// </para>
/// <para>
/// <b>No in-use check here.</b> The material of a Pending return cannot be on the InProduction order: the instruction was
/// refused if it was (AC-22.3), and while the return is Pending no order using the material can be sent (AC-15.11).
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2): the return in the URL (<c>404</c>), its state (<c>409</c> <c>not-pending</c>), then
/// the stock (<c>409</c> <c>insufficient-stock</c>). There is no body.
/// </para>
/// </remarks>
public sealed class ReceiveReturnService(
    AppDbContext db,
    StockLedger ledger,
    StockQueries stock,
    ReturnService returns,
    Clock clock,
    ILogger<ReceiveReturnService> logger)
{
    /// <summary>
    /// AC-22.4: receives Pending return <paramref name="number"/>: a ReturnedToWarehouse movement (Production storage −Q,
    /// Warehouse +Q) linked to the return and, if it has one, to its order (decision #107); the return Received, with who
    /// and when (AC-05.3). Returns it (design 5.9).
    /// </summary>
    /// <param name="number">The return number from the URL.</param>
    /// <param name="userId">The signed-in Warehouse or Manager user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no return has this number.</exception>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>not-pending</c> (AC-22.5), or <c>insufficient-stock</c> (decision #18).
    /// </exception>
    public async Task<ReturnResponse> ReceiveAsync(int number, string userId, CancellationToken cancellationToken)
    {
        var response = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the return named in the URL, with its material and unit for the message. Tracked,
            // because the receipt is written onto it.
            var leftoverReturn = await db.Returns
                .Include(candidate => candidate.Item).ThenInclude(item => item.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw ReturnService.NotFound(number);

            // Step 5: the state (AC-22.5). Step 7: the stock (decision #18).
            ReturnService.CheckPending(leftoverReturn);
            await RefuseIfStorageIsShortAsync(leftoverReturn, cancellationToken);

            var now = clock.UtcNow;

            // D-2, AC-22.4: one transfer, the same quantity out of production storage and into the Warehouse. Decision
            // #87: ReturnNumber is required on this type; decision #107: the linked order is copied, so
            // GET /movements?orderNumber= shows the material coming back. The ledger checks the stock again.
            await ledger.AddAsync(new Movement
            {
                Type = MovementType.ReturnedToWarehouse,
                ItemId = leftoverReturn.ItemId,
                WarehouseChange = leftoverReturn.Quantity,
                ProductionStorageChange = -leftoverReturn.Quantity,
                OccurredAt = now,
                UserId = userId,
                ReturnNumber = leftoverReturn.Number,
                ProductionOrderNumber = leftoverReturn.ProductionOrderNumber,
            }, cancellationToken);

            // AC-22.4, AC-05.3: Received, with who and when (the movement's time).
            leftoverReturn.Status = ReturnStatus.Received;
            leftoverReturn.ReceivedAt = now;
            leftoverReturn.ReceivedById = userId;

            await db.SaveChangesAsync(cancellationToken);
            return await returns.ReadAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change, only after the commit.
        logger.LogInformation("Return {ReturnNumber} received in the Warehouse by user {UserId}.", number, userId);
        return response;
    }

    /// <summary>
    /// Decision #18: refuses the receipt when production storage holds less than the return's quantity, with <c>409</c>
    /// <c>insufficient-stock</c> "Not enough stock: {item} in Production storage has {available} {unit}. Cancel this
    /// return or adjust the stock first." (design 7.2). Exactly enough (the storage reaching 0) is allowed (AC-22.4).
    /// </summary>
    /// <remarks>
    /// The ledger would refuse the movement too, but with its shorter sentence; architecture 5.1 has callers that need
    /// other wording check first with <see cref="StockQueries"/> and <see cref="StockLedger.InsufficientStockMessage"/>.
    /// </remarks>
    private async Task RefuseIfStorageIsShortAsync(LeftoverReturn leftoverReturn, CancellationToken cancellationToken)
    {
        var available = await stock.GetStockAsync(leftoverReturn.ItemId, Location.ProductionStorage, cancellationToken);
        if (available < leftoverReturn.Quantity)
        {
            var item = leftoverReturn.Item;
            throw new ConflictException(StockLedger.InsufficientStockCode,
                StockLedger.InsufficientStockMessage(item.Name, Location.ProductionStorage, available, item.Unit.Name)
                + " Cancel this return or adjust the stock first.");
        }
    }
}
