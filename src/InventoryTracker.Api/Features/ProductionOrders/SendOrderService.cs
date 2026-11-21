using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Sending the materials for a production order (US-15): the one moment stock moves from the Warehouse to Production
/// storage for an order, and the order becomes InProduction.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a send does, in plain words.</b> The warehouse user weighs out each material and submits one quantity per
/// order line. Some of each material may already be waiting in production storage as leftover from earlier runs
/// (decision #5). The send works out how much of that leftover this order may use (its "leftover applied", D-6), checks
/// that each quantity sent tops it up to the planned amount within the tolerance (D-7), and, if every line is fine and
/// the Warehouse has the stock, moves the quantities and fixes the figures on the order lines for completion to use
/// later (D-8, D-9).
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). Every read, check and write below runs inside one
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call. On SQLite that transaction takes the database's
/// single write lock at its start, so two sends never interleave: the second one waits, then sees the first one's
/// result. That one rule is what makes these guarantees hold under simultaneous requests:
/// </para>
/// <list type="bullet">
/// <item>One send per order (AC-15.9, decision #4): the second of two sends of the same order sees InProduction and gets
/// <c>already-sent</c>.</item>
/// <item>One order in production (AC-15.10, decision #6): the second of two sends of different orders sees the first one
/// InProduction and gets <c>another-order-in-production</c>. The unique filtered index on InProduction is the backstop
/// (architecture 3.2); if it ever fires, the client gets <c>409</c> <c>concurrent-change</c> (decision #46).</item>
/// <item>Leftover counted once (D-5, D-6): the allocation is calculated from the stock as it is inside the lock, and
/// fixed on the line before the lock is released.</item>
/// <item>Warehouse stock never below zero (AC-15.7): checked here for every line, and again by the ledger.</item>
/// </list>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1): the order in the URL (<c>404</c>), the body's shape (<c>400</c>
/// <c>validation</c>), the order's state (<c>409</c>: status, another order in production, a Pending return), the send
/// ranges (<c>400</c> <c>send-out-of-range</c>), the Warehouse stock (<c>409</c> <c>insufficient-stock</c>), then the
/// writes. The first failing step decides the response, and nothing is saved (PRD 4.1, "rejected").
/// </para>
/// <para>
/// This class takes other services through its constructor, the same dependency injection as everywhere else
/// (decision #77): <see cref="StockLedger"/> to write the movements, <see cref="StockQueries"/> for the stock figures,
/// and <see cref="ProductionOrderService"/> for the response, so the send returns exactly the detail
/// <c>GET /production-orders/{number}</c> shows (decision #92). All of them are scoped, so they share this request's
/// <c>AppDbContext</c> and therefore its transaction.
/// </para>
/// </remarks>
public sealed class SendOrderService(
    AppDbContext db,
    StockLedger ledger,
    StockQueries stock,
    ProductionOrderService orders,
    Clock clock,
    IOptions<InventoryOptions> options,
    ILogger<SendOrderService> logger)
{
    /// <summary>Design 7.2: the code for a send of an order that is InProduction or Completed (AC-15.9).</summary>
    public const string AlreadySentCode = "already-sent";

    /// <summary>Design 7.2: the code for a send while another order is InProduction (AC-15.10).</summary>
    public const string AnotherOrderInProductionCode = "another-order-in-production";

    /// <summary>Design 7.2: the code for a send while a Pending return of one of the order's materials exists (AC-15.11).</summary>
    public const string PendingReturnCode = "pending-return";

    /// <summary>Design 7.2: the code for a send with one or more lines outside their range (AC-15.6).</summary>
    public const string SendOutOfRangeCode = "send-out-of-range";

    /// <summary>
    /// AC-15.1: sends the materials for Created order <paramref name="number"/>: fixes each line's leftover applied
    /// (D-6), warehouse send, carried leftover (D-8) and complementary sends (0), writes one SentToProduction movement per
    /// line above zero, and makes the order InProduction with who sent it and when. Returns the order detail
    /// (design 5.7), now InProduction.
    /// </summary>
    /// <param name="number">The order number from the URL.</param>
    /// <param name="request">One quantity per order line.</param>
    /// <param name="userId">The signed-in Warehouse or Manager user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no order has this number.</exception>
    /// <exception cref="ValidationException">
    /// <c>400</c> <c>validation</c> for a bad body (AC-15.8), <c>send-out-of-range</c> (AC-15.6), or
    /// <c>too-many-lines</c> for more than <see cref="RequestLimits.MaxLines"/> lines (SEC-01, checked before the
    /// <c>404</c>).
    /// </exception>
    /// <exception cref="ConflictException">
    /// <c>409</c>: the order is not Created (AC-15.9), another order is InProduction (AC-15.10), a return of one of its
    /// materials is Pending (AC-15.11), or the Warehouse is short (AC-15.7).
    /// </exception>
    public async Task<ProductionOrderResponse> SendAsync(
        int number, SendOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        // SEC-01: refused before the write transaction; why, and what stays inside: RequestLimits.CheckLineCount.
        RequestLimits.CheckLineCount(request.Lines);

        var detail = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the record named in the URL. Its lines are loaded with their materials and units for
            // the messages, and tracked, because the send writes its figures onto them.
            var order = await db.ProductionOrders
                .Include(candidate => candidate.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw new NotFoundException($"Order #{number} not found.");

            // Step 4: the body's shape (AC-15.8).
            var sends = Validate(order, request);

            // Step 5: the order's state (AC-15.9, AC-15.10, AC-15.11).
            await CheckStateAsync(order, cancellationToken);

            // The stock figures of this order's materials, read once, inside the lock (architecture 5.1: filtered by
            // item). Steps 6 and 7 and the writes all use these same figures.
            var materialIds = order.Lines.Select(line => line.RawMaterialId).ToList();
            var figures = await stock.GetFiguresAsync(materialIds, cancellationToken);

            // Step 6: the send ranges (AC-15.6), with the leftover this order may use right now (D-5, D-6).
            var leftoverApplied = await AllocateLeftoverAsync(order, figures, cancellationToken);
            CheckRanges(order, sends, leftoverApplied);

            // Step 7: Warehouse stock for every line (AC-15.7).
            CheckWarehouseStock(sends, figures);

            await WriteSendAsync(order, sends, leftoverApplied, figures, userId, cancellationToken);

            // ReadDetailAsync reads the database, not the tracked objects, so the changes must be saved first. Saved
            // inside the open transaction: visible to this request's queries, still invisible to everyone else until
            // the commit (decision #92).
            await db.SaveChangesAsync(cancellationToken);
            return await orders.ReadDetailAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change is logged with the order number and the user. Logged only after the
        // commit, so the log never reports a send that was rolled back.
        logger.LogInformation("Order {OrderNumber} sent to production by user {UserId}.", number, userId);
        return detail;
    }

    /// <summary>
    /// Design 2.2 step 4 for a send body (AC-15.8): every order line appears exactly once, every item is on the order,
    /// and every quantity is given, not negative, has at most 3 decimal places and is at most 999,999,999.999 (decision
    /// #80). Every problem is reported in one <c>400</c> <c>validation</c>, keyed by the field's JSON path.
    /// </summary>
    /// <remarks>
    /// The rules and messages are the shared ones of <see cref="OrderLineMatcher"/> (decision #100), with the field
    /// <c>quantity</c> labelled "Quantity". Whether a zero send is enough for its line is the range check's job, later.
    /// </remarks>
    /// <returns>The valid sends, in the order the client listed them.</returns>
    private static List<MatchedOrderLine> Validate(ProductionOrder order, SendOrderRequest request)
    {
        var errors = new ValidationErrors();
        var submitted = (request.Lines ?? [])
            .Select(line => line is null ? null : new SubmittedOrderLine(line.ItemId, line.Quantity))
            .ToList();
        var sends = OrderLineMatcher.Match(order, submitted, "quantity", "Quantity", errors);
        errors.ThrowIfAny();
        return sends;
    }

    /// <summary>
    /// Design 2.2 step 5, the order's state: it must be Created (AC-15.9), no other order may be InProduction (AC-15.10),
    /// and none of its materials may have a Pending return (AC-15.11, decision #11).
    /// </summary>
    private async Task CheckStateAsync(ProductionOrder order, CancellationToken cancellationToken)
    {
        // AC-15.9, decision #4: one send per order. Extra material for a running order only comes through a
        // complementary order, which needs a reason and a Manager's approval (US-16, US-17).
        switch (order.Status)
        {
            case OrderStatus.InProduction or OrderStatus.Completed:
                throw new ConflictException(AlreadySentCode,
                    "Materials have already been sent for this order. Raise a complementary order for more material.");
            case OrderStatus.Cancelled:
                throw new ConflictException(ErrorCodes.OrderCancelled, "This order is cancelled.");
        }

        // AC-15.10, decision #6: only one order in production at a time. The reason: at completion, the leftover the
        // production user counts in production storage is attributed to the running order (D-9). With two orders
        // running on the same material, nobody could tell which order the leftover belonged to. This query runs inside
        // the write lock, so two simultaneous sends of different orders cannot both find "none".
        var running = await db.ProductionOrders
            .Where(candidate => candidate.Status == OrderStatus.InProduction)
            .Select(candidate => (int?)candidate.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (running is { } runningNumber)
        {
            throw new ConflictException(AnotherOrderInProductionCode,
                $"Order #{runningNumber} is in production. Only one order can be in production at a time.");
        }

        // AC-15.11, decision #11: material in a Pending return is still physically in production storage but no longer
        // free leftover (D-4). If the order were sent now, the material leaving later would happen while the order runs
        // and distort its usage figures (D-9), so the warehouse records the return first. Every blocking return is
        // named, in recipe line order (decision #96).
        var materialIds = order.Lines.Select(line => line.RawMaterialId).ToList();
        var pendingReturns = await db.Returns
            .Where(leftoverReturn => leftoverReturn.Status == ReturnStatus.Pending && materialIds.Contains(leftoverReturn.ItemId))
            .Select(leftoverReturn => new { leftoverReturn.Number, leftoverReturn.ItemId })
            .ToListAsync(cancellationToken);
        if (pendingReturns.Count > 0)
        {
            var linesByMaterial = order.Lines.ToDictionary(line => line.RawMaterialId);
            var messages = pendingReturns
                .OrderBy(pending => linesByMaterial[pending.ItemId].LineNo)
                .ThenBy(pending => pending.Number)
                .Select(pending =>
                    $"Return #{pending.Number} of {linesByMaterial[pending.ItemId].RawMaterial.Name} is waiting to be received by the warehouse. Record it first.");
            throw new ConflictException(PendingReturnCode, string.Join(" ", messages));
        }
    }

    /// <summary>
    /// D-5, D-6: the leftover of each material this order may use, calculated now, inside the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Oldest order first, in plain words.</b> Free leftover in production storage (D-4) is shared among the orders
    /// waiting in Created status, oldest first: the oldest takes as much as it plans to use, the next takes what is
    /// left, and so on, for each material separately (D-5, <see cref="LeftoverAllocator"/>). A newer order may be sent
    /// before an older one, but it only gets what is left after every older waiting order has taken its share (decision
    /// #10). AC-15.5: with 50 kg of free Cheese, #7 (older, planned 30 kg) is owed 30 kg, so #8 sent first applies only
    /// the remaining 20 kg, and the 30 kg stays free for #7.
    /// </para>
    /// <para>
    /// This order's share becomes its leftover applied, fixed on the line by the send (D-6). From then on the material
    /// is committed to this InProduction order (D-3) and no longer free (D-4), so no other order can be given the same
    /// kilogram: each unit of leftover is applied once.
    /// </para>
    /// <para>
    /// The queue and the allocation come from <see cref="WaitingOrderQueue"/>, the same method the order detail's estimate
    /// uses (decision #97), so the send applies exactly the leftover the estimate showed a moment before (unless something
    /// changed in between, such as an older order being cancelled). No order is InProduction at this point (checked just
    /// before), so free leftover here is production storage less Pending returns, of which there are none for these
    /// materials either.
    /// </para>
    /// </remarks>
    private Task<LeftoverAllocation> AllocateLeftoverAsync(
        ProductionOrder order, IReadOnlyDictionary<int, StockFigures> figures, CancellationToken cancellationToken)
    {
        var freeLeftover = figures.ToDictionary(pair => pair.Key, pair => pair.Value.FreeLeftover);
        return WaitingOrderQueue.AllocateAsync(db, order, freeLeftover, cancellationToken);
    }

    /// <summary>
    /// Design 2.2 step 6, AC-15.6: every quantity must lie inside its line's send range (D-7). Every failing line is
    /// named, with its range, in one <c>400</c> <c>send-out-of-range</c>, under its own <c>lines[i].quantity</c>.
    /// </summary>
    /// <remarks>
    /// <b>The send range, in plain words.</b> The scales are not perfectly accurate, so the warehouse may send a little
    /// more or less than planned: what ends up in production storage for the order (leftover applied L plus send S) must
    /// be within t of the planned amount P, limits included: P × (1 − t) ≤ L + S ≤ P × (1 + t). The window's width is
    /// based on the full planned amount, not on what is left to send (decision #3). AC-15.2: P = 1,000 kg, no leftover,
    /// t = 2%: 980 to 1,020 kg. AC-15.3: 5 kg of leftover moves the window down to 975 to 1,015 kg (nominal 995). AC-15.4:
    /// 30 kg of leftover against P = 30 kg gives 0 to 0.6 kg, so sending nothing is right. The limits are rounded to 3
    /// places, and a quantity is compared with exactly the rounded limits the order detail shows (decision #24).
    /// </remarks>
    private void CheckRanges(ProductionOrder order, IReadOnlyList<MatchedOrderLine> sends, LeftoverAllocation leftoverApplied)
    {
        var tolerance = options.Value.TolerancePercent;
        var errors = new ValidationErrors();

        foreach (var send in sends)
        {
            var line = send.Line;
            var applied = leftoverApplied.For(order.Number, line.RawMaterialId);
            var range = ProductionCalculations.SendRange(line.PlannedAmount, applied, tolerance);
            if (ProductionCalculations.IsWithin(send.Quantity, range))
            {
                continue;
            }

            var unit = line.RawMaterial.Unit.Name;
            errors.Add($"lines[{send.Index}].quantity",
                $"{line.RawMaterial.Name}: send must be between {MessageFormat.Number(range.Min)} and " +
                $"{MessageFormat.Number(range.Max)} {unit} (planned {MessageFormat.Number(line.PlannedAmount)} {unit}, " +
                $"leftover applied {MessageFormat.Number(applied)} {unit}).");
        }

        errors.ThrowIfAny(SendOutOfRangeCode);
    }

    /// <summary>
    /// Design 2.2 step 7, AC-15.7: the Warehouse must hold at least the quantity of every line. Every short line is
    /// named, with what the Warehouse holds, in one <c>409</c> <c>insufficient-stock</c>.
    /// </summary>
    /// <remarks>
    /// Checked here for all lines at once because <see cref="StockLedger.AddAsync"/> stops at the first short movement
    /// and names only that one (architecture 5.1). The ledger still checks each movement again as it is added, as the
    /// last line of defence. A line sending 0 needs no stock (AC-15.4).
    /// </remarks>
    private static void CheckWarehouseStock(IReadOnlyList<MatchedOrderLine> sends, IReadOnlyDictionary<int, StockFigures> figures)
    {
        var shortLines = sends
            .Where(send => send.Quantity > figures[send.Line.RawMaterialId].Warehouse)
            .Select(send => StockLedger.InsufficientStockMessage(
                send.Line.RawMaterial.Name, Location.Warehouse, figures[send.Line.RawMaterialId].Warehouse,
                send.Line.RawMaterial.Unit.Name))
            .ToList();

        if (shortLines.Count > 0)
        {
            throw new ConflictException(StockLedger.InsufficientStockCode, string.Join(" ", shortLines));
        }
    }

    /// <summary>
    /// AC-15.1: fixes the send figures on every line, moves each quantity above zero from the Warehouse to Production
    /// storage, and makes the order InProduction with who sent it and when (AC-05.3).
    /// </summary>
    private async Task WriteSendAsync(
        ProductionOrder order,
        IReadOnlyList<MatchedOrderLine> sends,
        LeftoverAllocation leftoverApplied,
        IReadOnlyDictionary<int, StockFigures> figures,
        string userId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        foreach (var send in sends)
        {
            var line = send.Line;
            var applied = leftoverApplied.For(order.Number, line.RawMaterialId);

            // D-6: the leftover applied is fixed now and never recalculated, unlike the estimate shown while Created.
            line.LeftoverApplied = applied;
            line.WarehouseSend = send.Quantity;

            // D-8: whatever sat in production storage just before the send and was not applied to this order, because
            // it was more than this order needed (AC-15.4: 50 kg there, 30 kg applied, 20 kg carried) or was owed to an
            // older waiting order (AC-15.5: 50 kg there, 20 kg applied, 30 kg carried). It stays where it is and remains
            // free leftover. Completion needs it, because the production user then counts everything physically there,
            // carried leftover included (decision #9, D-9).
            line.CarriedLeftover = ProductionCalculations.CarriedLeftover(figures[line.RawMaterialId].ProductionStorage, applied);

            // D-3: complementary sends start at 0 and grow with each complementary send (design 5.7, T-018).
            line.ComplementarySends = 0m;
        }

        // AC-15.1: the order becomes InProduction, recording who sent it and when (AC-05.3, design 5.7 history).
        order.Status = OrderStatus.InProduction;
        order.SentAt = now;
        order.SentById = userId;
        order.StatusChangedAt = now;

        // AC-15.1: one SentToProduction movement per line with a quantity above zero, Warehouse −S and Production storage
        // +S, linked to the order and the user (D-2). A zero send creates no movement (AC-15.4, PRD section 6 row 17).
        // Every movement goes through the ledger (architecture 5.1). Its save also saves the line and status changes
        // above, still inside this transaction.
        foreach (var send in sends.Where(send => send.Quantity > 0m))
        {
            await ledger.AddAsync(new Movement
            {
                Type = MovementType.SentToProduction,
                ItemId = send.Line.RawMaterialId,
                WarehouseChange = -send.Quantity,
                ProductionStorageChange = send.Quantity,
                OccurredAt = now,
                UserId = userId,
                ProductionOrderNumber = order.Number,
            }, cancellationToken);
        }
    }
}
