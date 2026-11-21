using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.ProductionOrders;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Features.ComplementaryOrders;

/// <summary>
/// Sending the material of an approved complementary order (US-18): the extra material a Manager approved moves from the
/// Warehouse to Production storage for the running order, and the complementary order becomes Sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a complementary send does, in plain words.</b> A production user asked for more of some materials during a
/// run (US-16) and a Manager approved it (US-17). The warehouse user now weighs out each requested material and submits
/// one quantity per line. Each quantity must be within the tolerance of the amount requested (AC-18.1); unlike the
/// order's own send, no leftover is netted, because the order's leftover was already applied when it was sent (D-6). If
/// every line is in range and the Warehouse has the stock, the material moves, each quantity is added to the parent order
/// line's complementary sends, and the complementary order is Sent, once (AC-18.3).
/// </para>
/// <para>
/// <b>Why the parent's line changes.</b> The parent order line keeps a running total of complementary sends (D-3). It is
/// part of the material committed to the running order, so it is not free leftover for anyone else (D-4, <c>GET
/// /stock</c>'s <c>committedToOrder</c>), and completion counts it in the actual usage (D-9: leftover applied + warehouse
/// send + complementary sends − leftover remaining). Worked example A (AC-20.5): 5 + 995 + 20 − 4 = 1,016 kg.
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). Every read, check and write runs inside one
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the database's single write lock
/// from the first read to the commit. So:
/// </para>
/// <list type="bullet">
/// <item>One send per complementary order (AC-18.3): the second of two simultaneous sends waits, then finds Sent and gets
/// <c>409</c> <c>not-approved</c>.</item>
/// <item>A send and the parent's completion or cancellation never interleave. If the send is first, the completion that
/// follows counts it in the usage (or the cancellation leaves it in production storage as leftover). If the completion
/// or cancellation is first, it has already made this unsent complementary order Cancelled (AC-19.6, AC-21.4), so the
/// send finds Cancelled and gets <c>409</c>. The parent is therefore always InProduction when a send passes the status
/// check.</item>
/// <item>A send and a Manager's rejection cannot both succeed: a rejection needs PendingApproval (AC-17.3), a send needs
/// Approved.</item>
/// <item>Warehouse stock never goes below zero (AC-18.4): checked here for every line, and again by the ledger.</item>
/// </list>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1): the complementary order in the URL (<c>404</c>), the body's shape
/// (<c>400</c> <c>validation</c>), the state (<c>409</c> <c>not-approved</c>), the ranges (<c>400</c>
/// <c>send-out-of-range</c>), the Warehouse stock (<c>409</c> <c>insufficient-stock</c>), then the writes.
/// </para>
/// <para>
/// <b>No Pending-return check.</b> AC-15.11 blocks the order's own send while a return of one of its materials is
/// Pending (decision #11). It is not repeated here: neither US-18 nor design 3.7 and 7.2 list it for a complementary send,
/// and it cannot arise. A Pending return of the parent's materials would have blocked the parent's send, and while the
/// parent is InProduction no return of its materials can be instructed (AC-22.3, PRD section 6 row 3).
/// </para>
/// </remarks>
public sealed class ComplementarySendService(
    AppDbContext db,
    StockLedger ledger,
    StockQueries stock,
    ComplementaryOrderService complementaryOrders,
    Clock clock,
    IOptions<InventoryOptions> options,
    ILogger<ComplementarySendService> logger)
{
    /// <summary>Design 7.2: the code for a send of a complementary order that is not Approved (AC-18.3).</summary>
    public const string NotApprovedCode = "not-approved";

    /// <summary>
    /// AC-18.1: sends Approved complementary order <paramref name="number"/>: one SentToProduction movement per line above
    /// zero, linked to the complementary order and its parent; each quantity recorded on its line and added to the parent
    /// line's complementary sends (D-3); the complementary order Sent, with who sent it and when (AC-05.3). Returns it
    /// (design 5.8).
    /// </summary>
    /// <param name="number">The complementary order number from the URL.</param>
    /// <param name="request">One quantity per line of the complementary order.</param>
    /// <param name="userId">The signed-in Warehouse or Manager user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no complementary order has this number.</exception>
    /// <exception cref="ValidationException">
    /// <c>400</c> <c>validation</c> for a bad body, <c>send-out-of-range</c> (AC-18.1, AC-18.2), or
    /// <c>too-many-lines</c> for more than <see cref="RequestLimits.MaxLines"/> lines (SEC-01, checked before the
    /// <c>404</c>).
    /// </exception>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>not-approved</c> (AC-18.3), or <c>insufficient-stock</c> (AC-18.4).
    /// </exception>
    public async Task<ComplementaryOrderResponse> SendAsync(
        int number, SendComplementaryOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        // SEC-01: refused before the write transaction; why, and what stays inside: RequestLimits.CheckLineCount.
        RequestLimits.CheckLineCount(request.Lines);

        var (response, parentNumber) = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the record named in the URL, and its parent's lines (materials and units for the
            // messages). Both tracked, because the send writes onto them.
            var complementary = await db.ComplementaryOrders
                .Include(candidate => candidate.Lines)
                .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw ComplementaryOrderService.NotFound(number);
            var parent = await db.ProductionOrders
                .Include(order => order.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit)
                .SingleAsync(order => order.Number == complementary.ParentOrderNumber, cancellationToken);
            var parentLines = ParentLinesOf(complementary, parent);

            // Step 4: the body's shape. Step 5: the state (AC-18.3).
            var sends = Validate(complementary, parentLines, request);
            CheckState(complementary, parent);

            // Step 6: the ranges (AC-18.1, AC-18.2). Step 7: the Warehouse stock (AC-18.4).
            var requested = complementary.Lines.ToDictionary(line => line.RawMaterialId);
            CheckRanges(sends, requested);
            await CheckWarehouseStockAsync(sends, cancellationToken);

            await WriteSendAsync(complementary, sends, requested, userId, cancellationToken);

            // ReadAsync reads the database, not the tracked objects, so save first. Saved inside the open transaction:
            // visible to this request's query, invisible to everyone else until the commit.
            await db.SaveChangesAsync(cancellationToken);
            return (await complementaryOrders.ReadAsync(number, cancellationToken), parent.Number);
        }, cancellationToken);

        // Architecture 6, Logging: each state change, with the numbers and the user, only after the commit, so the log
        // never reports a send that was rolled back.
        logger.LogInformation(
            "Complementary order {ComplementaryOrderNumber} for order {OrderNumber} sent to production by user {UserId}.",
            number, parentNumber, userId);
        return response;
    }

    /// <summary>
    /// The parent order's line for each material of the complementary order, in the parent's recipe line order. These are
    /// the lines the body must name, and the ones whose complementary sends grow.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A complementary line's material is not on the parent. The request (AC-16.3) never allows this, so it means the
    /// stored data is broken; refusing it (<c>500</c>) is safer than sending material the parent cannot account for.
    /// </exception>
    private static List<ProductionOrderLine> ParentLinesOf(ComplementaryOrder complementary, ProductionOrder parent)
    {
        var materials = complementary.Lines.Select(line => line.RawMaterialId).ToHashSet();
        var parentLines = parent.Lines.Where(line => materials.Contains(line.RawMaterialId)).OrderBy(line => line.LineNo).ToList();
        if (parentLines.Count != materials.Count)
        {
            throw new InvalidOperationException(
                $"Complementary order #{complementary.Number} has a material that is not on its parent order #{parent.Number}.");
        }

        return parentLines;
    }

    /// <summary>
    /// Design 2.2 step 4 for a complementary send body (AC-18.1: "a quantity for every line"). Every problem is reported in
    /// one <c>400</c> <c>validation</c>, keyed by the field's JSON path.
    /// </summary>
    /// <remarks>
    /// The shared <see cref="OrderLineMatcher"/> (decision #100) with the send's rules: every line of the complementary
    /// order exactly once ("Every order line needs a quantity. Missing: Cheese.", design 7.2 "Missing line (send …)"), an
    /// item that is not one of them ("Item {id} is not on complementary order #{c}.", the send's wording, decision #96),
    /// no repeats; the quantity given, not negative, at most 3 decimal places (decision #95) and at most 999,999,999.999
    /// (decision #80). Zero passes this step, as for the order's send; whether it is enough is the range check's job.
    /// </remarks>
    /// <returns>The valid sends, in the order the client listed them; each names the parent's line.</returns>
    private static List<MatchedOrderLine> Validate(
        ComplementaryOrder complementary, List<ProductionOrderLine> parentLines, SendComplementaryOrderRequest request)
    {
        var errors = new ValidationErrors();
        var submitted = (request.Lines ?? [])
            .Select(line => line is null ? null : new SubmittedOrderLine(line.ItemId, line.Quantity))
            .ToList();
        var sends = OrderLineMatcher.Match(parentLines, submitted, "quantity", "Quantity", errors, new OrderLineRules
        {
            NotOnOrderMessage = itemId => $"Item {itemId} is not on complementary order #{complementary.Number}.",
        });
        errors.ThrowIfAny();
        return sends;
    }

    /// <summary>
    /// Design 2.2 step 5, AC-18.3: only an Approved complementary order can be sent. Any other status is <c>409</c>
    /// <c>not-approved</c> "Complementary order #{n} is {status}. Only an Approved complementary order can be sent."
    /// (design 7.2). This is also what the loser of two simultaneous sends gets ("… is Sent. …").
    /// </summary>
    /// <remarks>
    /// Why each other status is refused: PendingApproval has no Manager's decision yet (decision #4: no extra material
    /// leaves the warehouse without one); Rejected can never be sent (AC-17.2); Sent has moved its material already, once
    /// (AC-18.3); Cancelled belongs to a parent order that has ended (AC-19.6, AC-21.4).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Approved, but the parent is not InProduction or its line has no complementary sends figure. Completion and
    /// cancellation cancel every Approved complementary order in the same transaction that ends the parent
    /// (<c>UnsentComplementaryOrders</c>), and the parent's send sets complementary sends to 0, so this means the stored
    /// data is broken. Adding to a completed order's figures would change its usage after the fact, so nothing is written
    /// (<c>500</c>).
    /// </exception>
    private static void CheckState(ComplementaryOrder complementary, ProductionOrder parent)
    {
        if (complementary.Status != ComplementaryOrderStatus.Approved)
        {
            throw new ConflictException(NotApprovedCode,
                $"Complementary order #{complementary.Number} is {complementary.Status}. Only an Approved complementary order can be sent.");
        }

        // PRD section 6 row 6: a complementary order tops up a running order, so its parent must still be InProduction.
        if (parent.Status != OrderStatus.InProduction || parent.Lines.Any(line => line.ComplementarySends is null))
        {
            throw new InvalidOperationException(parent.Status != OrderStatus.InProduction
                ? $"Complementary order #{complementary.Number} is Approved, but its parent order #{parent.Number} is {parent.Status}."
                : $"Complementary order #{complementary.Number} is Approved, but its InProduction parent order #{parent.Number} has a line with no complementary sends figure.");
        }
    }

    /// <summary>
    /// Design 2.2 step 6, AC-18.1, AC-18.2: every quantity must lie within requested amount × (1 − t) to requested amount
    /// × (1 + t), limits included. Every failing line is named, with its range, in one <c>400</c> <c>send-out-of-range</c>,
    /// under its own <c>lines[i].quantity</c>, in body order (decision #96).
    /// </summary>
    /// <remarks>
    /// The range is <see cref="ProductionCalculations.ComplementaryRange"/>, the one implementation (T-004), also used for
    /// the <c>sendRangeMin</c>/<c>sendRangeMax</c> the complementary order shows, so the API accepts exactly what it
    /// displays (decision #24). AC-18.2: requested 20 kg at t = 2% gives 19.6 to 20.4 kg; 19.599 and 20.401 are refused
    /// with "Cheese: send must be between 19.6 and 20.4 kg (requested 20 kg)." (design 7.2). No leftover is netted:
    /// the extra is on top of what the order's own send already covered.
    /// </remarks>
    private void CheckRanges(
        IReadOnlyList<MatchedOrderLine> sends, IReadOnlyDictionary<int, ComplementaryOrderLine> requested)
    {
        var tolerance = options.Value.TolerancePercent;
        var errors = new ValidationErrors();

        foreach (var send in sends)
        {
            var amount = requested[send.Line.RawMaterialId].RequestedAmount;
            var range = ProductionCalculations.ComplementaryRange(amount, tolerance);
            if (ProductionCalculations.IsWithin(send.Quantity, range))
            {
                continue;
            }

            var unit = send.Line.RawMaterial.Unit.Name;
            errors.Add($"lines[{send.Index}].quantity",
                $"{send.Line.RawMaterial.Name}: send must be between {MessageFormat.Number(range.Min)} and " +
                $"{MessageFormat.Number(range.Max)} {unit} (requested {MessageFormat.Number(amount)} {unit}).");
        }

        errors.ThrowIfAny(SendOrderService.SendOutOfRangeCode);
    }

    /// <summary>
    /// Design 2.2 step 7, AC-18.4: the Warehouse must hold at least the quantity of every line. Every short line is named,
    /// with what the Warehouse holds, in one <c>409</c> <c>insufficient-stock</c>, in body order: "Not enough stock: Cheese
    /// in Warehouse has 12 kg." (design 7.2).
    /// </summary>
    /// <remarks>
    /// Checked here for all lines at once because <see cref="StockLedger.AddAsync"/> stops at the first short movement and
    /// names only that one (architecture 5.1). Each line is a different material (no repeats passed step 4), so each
    /// line's quantity is compared with its own material's stock. The ledger checks each movement again as it is added.
    /// </remarks>
    private async Task CheckWarehouseStockAsync(IReadOnlyList<MatchedOrderLine> sends, CancellationToken cancellationToken)
    {
        var shortLines = new List<string>();
        foreach (var send in sends.Where(send => send.Quantity > 0m))
        {
            var available = await stock.GetStockAsync(send.Line.RawMaterialId, Location.Warehouse, cancellationToken);
            if (send.Quantity > available)
            {
                shortLines.Add(StockLedger.InsufficientStockMessage(
                    send.Line.RawMaterial.Name, Location.Warehouse, available, send.Line.RawMaterial.Unit.Name));
            }
        }

        if (shortLines.Count > 0)
        {
            throw new ConflictException(StockLedger.InsufficientStockCode, string.Join(" ", shortLines));
        }
    }

    /// <summary>
    /// AC-18.1: records each quantity on its complementary line and adds it to the parent line's complementary sends (D-3),
    /// makes the complementary order Sent with who and when (AC-05.3), and moves each quantity above zero from the
    /// Warehouse to Production storage.
    /// </summary>
    private async Task WriteSendAsync(
        ComplementaryOrder complementary,
        IReadOnlyList<MatchedOrderLine> sends,
        IReadOnlyDictionary<int, ComplementaryOrderLine> requested,
        string userId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        foreach (var send in sends)
        {
            requested[send.Line.RawMaterialId].SentQuantity = send.Quantity;

            // D-3: the parent line's running total. CheckState has made sure it is not null (the parent's send set it to 0).
            send.Line.ComplementarySends = send.Line.ComplementarySends!.Value + send.Quantity;
        }

        // AC-18.1, AC-05.3: Sent, with who and when (design 5.8 history). The parent's own status does not change.
        complementary.Status = ComplementaryOrderStatus.Sent;
        complementary.SentAt = now;
        complementary.SentById = userId;

        // AC-18.1, design 5.6: one SentToProduction movement per line, Warehouse −S and Production storage +S, linked to the
        // complementary order AND its parent order (decision #87), so GET /movements?orderNumber= finds it with the
        // order's other movements. A line of 0 (possible only at a very high tolerance) moves nothing and creates no
        // movement (PRD section 6 row 17). The ledger checks that the parent really is this complementary order's parent
        // before it writes, because a movement can never be corrected. Its save also saves the changes above, still inside
        // this transaction.
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
                ProductionOrderNumber = complementary.ParentOrderNumber,
                ComplementaryOrderNumber = complementary.Number,
            }, cancellationToken);
        }
    }
}
