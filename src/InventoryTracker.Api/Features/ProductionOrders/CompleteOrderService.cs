using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.Notifications;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Completing a production order (US-19, US-20): the one moment the output is registered, the material used is taken
/// out of production storage, the usage and output are compared with the plan, and the Managers are told about any
/// difference.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a completion does, in plain words.</b> At the end of the run the production user counts what was made
/// (the produced quantity) and weighs what is physically left of each material in production storage (the leftover),
/// and gives a reason if anything is left. From that the system works out, per material, how much this order really
/// used and how far that is from what the actual output needed (D-9), and how far the output is from the order (D-9).
/// It then moves the stock (AC-19.2), stores every figure on the order (AC-20.1, AC-20.3) and raises the notifications
/// (AC-19.7, AC-20.2, AC-20.4).
/// </para>
/// <para>
/// <b>Actual usage and carried leftover, with worked example D (AC-20.8).</b> Production storage held 50 kg of free
/// Cheese. Order #8 (1,000 kg planned) was sent before the older order #7, so it could apply only the 20 kg not owed to
/// #7 (D-5); the other 30 kg stayed there as <em>carried leftover</em> (D-8), still owed to #7. The warehouse sent 980 kg,
/// so production storage held 50 + 980 = 1,030 kg during the run. At the end the user weighs 30 kg left. That 30 kg is
/// the physical total, carried leftover included (decision #9): the user cannot tell whose kilogram is whose, so the
/// system subtracts the carried leftover itself. LeftoverRemaining = 30 − 30 = 0 kg is what is left of this order's own
/// material, and ActualUsage = leftover applied + warehouse send + complementary sends − LeftoverRemaining =
/// 20 + 980 + 0 − 0 = 1,000 kg. The ConsumedInProduction movement takes exactly that out of production storage
/// (1,030 − 30 = 1,000), leaving the 30 kg for #7 again (AC-19.3).
/// </para>
/// <para>
/// <b>The two variances and the tolerance check, with worked examples A to C (AC-20.5 to AC-20.7).</b>
/// </para>
/// <list type="bullet">
/// <item><b>Usage variance</b> (per material) = ActualUsage − RequiredForActualOutput, where RequiredForActualOutput =
/// amount per unit × produced quantity (rounded to 3 places). It is measured against the <em>actual</em> output, not
/// the order, so making more bags with proportionally more cheese is not waste. Example A: 1,001 bags need 1,001 kg;
/// 1,016 kg was used: +15 kg, +1.4985...%, shown +1.50%.</item>
/// <item><b>The tolerance check</b> (AC-20.4): a UsageVariance notification is raised when the size of that percentage,
/// unrounded, is greater than the tolerance t (2% by default). Both directions count (decision #12): example B's +2.90%
/// and example C's −2.50% both notify, example A's +1.50% does not. Exactly 2% does not notify (limit included, PRD
/// section 6 row 5), and 2.0001% does, although it is shown as 2.00%. That is why the comparison uses the unrounded
/// value and the response carries the server's verdict, <c>usageVarianceAboveTolerance</c> (design 2.4).</item>
/// <item><b>Output variance</b> (per order) = produced − ordered, and its percentage of the ordered quantity. It has no
/// tolerance (D-1): any difference notifies (AC-20.2). Example A: 1,001 − 1,000 = +1 bag, +0.10%: one OutputVariance
/// notification. Example C: produced = ordered, no notification.</item>
/// </list>
/// <para>
/// When the required amount rounds to 0 kg (a tiny recipe amount times a small output), a percentage would divide by
/// zero. Decision #49: the percentage is stored as null, and the line counts as above tolerance whenever any of the
/// material was used; the notification then says "percentage not applicable" (decision #57).
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). As for the send, every read, check and write runs
/// inside one <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the database's single
/// write lock until the commit. So output is registered once (AC-19.5): the second of two simultaneous completions waits,
/// then sees the order Completed and gets <c>already-completed</c>. The movements, the figures, the notifications and the
/// cancelled complementary orders are committed together or not at all ("Notifications match events").
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1): the order in the URL (<c>404</c>); the body's shape (<c>400</c>
/// <c>validation</c>, then <c>reason-required</c>, which needs valid leftovers to know whether any is above zero); the
/// order's state (<c>409</c>: it must be InProduction); the leftover against production storage (<c>400</c>
/// <c>leftover-exceeds-stock</c>, design 2.2 step 6 "leftover larger than stock"); then the writes, where the ledger checks
/// stock once more as the last line of defence.
/// </para>
/// <para>
/// Like <see cref="SendOrderService"/>, it takes its collaborators through the constructor (dependency injection,
/// decision #77): <see cref="StockLedger"/> for the movements, <see cref="StockQueries"/> for production storage, and
/// <see cref="ProductionOrderService"/> for the response, so it returns exactly what <c>GET /production-orders/{number}</c>
/// shows (decision #92).
/// </para>
/// </remarks>
public sealed class CompleteOrderService(
    AppDbContext db,
    StockLedger ledger,
    StockQueries stock,
    ProductionOrderService orders,
    Clock clock,
    IOptions<InventoryOptions> options,
    ILogger<CompleteOrderService> logger)
{
    /// <summary>Design 7.2: the code for completing an order that is still Created (AC-19.5).</summary>
    public const string OrderNotSentCode = "order-not-sent";

    /// <summary>Design 7.2: the code for a second completion of the same order (AC-19.5).</summary>
    public const string AlreadyCompletedCode = "already-completed";

    /// <summary>Design 7.2: the code for leftover above zero without a reason (AC-19.4).</summary>
    public const string ReasonRequiredCode = "reason-required";

    /// <summary>Design 7.2: the code for a leftover larger than production storage holds (AC-19.4).</summary>
    public const string LeftoverExceedsStockCode = "leftover-exceeds-stock";

    /// <summary>
    /// AC-19.1, AC-19.2: completes InProduction order <paramref name="number"/>. Writes the ConsumedInProduction movement
    /// of each line (none for zero) and the ProducedOutput movement, stores every completion figure (D-9) and the
    /// tolerance in force (AC-20.10), creates the notifications (AC-19.7, AC-20.2, AC-20.4), cancels the order's
    /// unsent complementary orders (AC-19.6), and makes the order Completed with who and when. Returns the order
    /// detail (design 5.7), now Completed.
    /// </summary>
    /// <param name="number">The order number from the URL.</param>
    /// <param name="request">Produced quantity, leftover per line, and the reason.</param>
    /// <param name="userId">The signed-in Production or Manager user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no order has this number.</exception>
    /// <exception cref="ValidationException">
    /// <c>400</c> <c>validation</c> for a bad body, <c>reason-required</c> or <c>leftover-exceeds-stock</c> (AC-19.4), or
    /// <c>too-many-lines</c> for more than <see cref="RequestLimits.MaxLines"/> lines (SEC-01, checked before the
    /// <c>404</c>).
    /// </exception>
    /// <exception cref="ConflictException"><c>409</c>: the order is not InProduction (AC-19.5).</exception>
    public async Task<ProductionOrderResponse> CompleteAsync(
        int number, CompleteOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        // SEC-01: refused before the write transaction; why, and what stays inside: RequestLimits.CheckLineCount.
        RequestLimits.CheckLineCount(request.Lines);

        var (detail, mismatches) = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the record named in the URL, tracked because completion writes onto it and its lines.
            // The product and the materials come with their units, for the messages and notifications.
            var order = await db.ProductionOrders
                .Include(candidate => candidate.FinishedProduct).ThenInclude(product => product.Unit)
                .Include(candidate => candidate.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw new NotFoundException($"Order #{number} not found.");

            // Step 4: the body's shape (AC-19.4), then the reason rule, which needs the leftovers to be valid first.
            var input = Validate(order, request);
            RequireReasonForLeftover(input);

            // Step 5: the order's state (AC-19.5).
            CheckState(order);

            // Step 6: no leftover may exceed what production storage holds (AC-19.4). Read once, inside the lock, so
            // the figures cannot change before the writes below use them (architecture 5.1: filtered by item).
            var materialIds = order.Lines.Select(line => line.RawMaterialId).ToList();
            var figures = await stock.GetFiguresAsync(materialIds, cancellationToken);
            CheckLeftoverAgainstStock(input, figures);

            var usageMismatches = await WriteCompletionAsync(order, input, figures, userId, cancellationToken);

            // ReadDetailAsync reads the database, not the tracked objects, so everything is saved first, still inside
            // the open transaction (decision #92).
            await db.SaveChangesAsync(cancellationToken);
            return (await orders.ReadDetailAsync(number, cancellationToken), usageMismatches);
        }, cancellationToken);

        // Architecture 6, Logging: everything about this completion is logged only after the commit, so the log never
        // reports a completion, or a figure of one, that was rolled back. Decision #98 (f): a mismatch between the stock
        // consumed and the actual usage is a Warning for someone to investigate.
        foreach (var mismatch in mismatches)
        {
            logger.LogWarning(
                "Order {OrderNumber}, item {ItemId}: consumed {Consumed} differs from actual usage {ActualUsage}.",
                number, mismatch.ItemId, mismatch.Consumed, mismatch.ActualUsage);
        }

        logger.LogInformation("Order {OrderNumber} completed by user {UserId}.", number, userId);
        return detail;
    }

    /// <summary>
    /// Design 2.2 step 4 for a completion body (AC-19.1, AC-19.4): the produced quantity is given, above 0, with at most 3
    /// decimal places and at most 999,999,999.999; every order line appears exactly once with a leftover that is given,
    /// not negative, with at most 3 decimal places and at most 999,999,999.999; the reason, if given, is at most 500
    /// characters. Every problem is reported in one <c>400</c> <c>validation</c>, keyed by the field's JSON path, in body
    /// order.
    /// </summary>
    private static CompletionInput Validate(ProductionOrder order, CompleteOrderRequest request)
    {
        var errors = new ValidationErrors();

        // Architecture 3.1 order of rules on a quantity, one message per field (decision #75): required, sign, decimal
        // places, maximum. Decimal-places and maximum messages name the field (decisions #80, #95).
        if (request.ProducedQuantity is not { } produced)
        {
            errors.Add("producedQuantity", "Produced quantity is required.");
        }
        else if (produced <= 0m)
        {
            // AC-19.4, PRD section 6 row 7: a run with no usable output is cancelled instead (US-21), and the material
            // written off, so a completion always registers some output.
            errors.Add("producedQuantity", "Produced quantity must be greater than 0.");
        }
        else if (!Quantity.HasAtMostDecimals(produced, Quantity.MaxQuantityDecimals))
        {
            errors.Add("producedQuantity", $"Produced quantity can have at most {Quantity.MaxQuantityDecimals} decimal places.");
        }
        else if (!Quantity.IsWithinMax(produced))
        {
            errors.Add("producedQuantity", Quantity.AboveMaxMessage("Produced quantity"));
        }

        // The lines: the shared order-line rules and messages of the send (decision #100), with the field "leftover"
        // labelled "Leftover": every order line exactly once, each leftover given, not negative, at most 3 decimal places,
        // at most 999,999,999.999 (AC-19.4, decision #80).
        var submitted = (request.Lines ?? [])
            .Select(line => line is null ? null : new SubmittedOrderLine(line.ItemId, line.Leftover))
            .ToList();
        var leftovers = OrderLineMatcher.Match(order, submitted, "leftover", "Leftover", errors);

        // Design 2.6: trimmed first; blank counts as not given. AC-19.1: at most 500 characters.
        var reason = Text.Clean(request.Reason);
        // Decision #116: counted in Unicode code points, so an emoji is one character (Text.Length).
        if (Text.Length(reason) > ColumnRules.TextLength)
        {
            errors.Add("reason", $"Reason must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();
        return new CompletionInput(request.ProducedQuantity!.Value, leftovers, reason);
    }

    /// <summary>
    /// AC-19.1, AC-19.4: when any leftover entered is above zero, a reason is required. <c>400</c> <c>reason-required</c>
    /// "A reason is required when leftover is recorded." under <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// Leftover is where waste hides (decision #4), so a Manager reading the LeftoverAtCompletion notification must be
    /// told why it is there. With every leftover at zero there is nothing to explain, and the reason may be left out.
    /// </remarks>
    private static void RequireReasonForLeftover(CompletionInput input)
    {
        if (input.Reason is null && input.Leftovers.Any(leftover => leftover.Quantity > 0m))
        {
            var errors = new ValidationErrors();
            errors.Add("reason", "A reason is required when leftover is recorded.");
            errors.ThrowIfAny(ReasonRequiredCode);
        }
    }

    /// <summary>
    /// Design 2.2 step 5, AC-19.5: only an InProduction order can be completed. Output is registered once: a Completed
    /// order gets <c>already-completed</c>, and so does the loser of two simultaneous completions, because the status is
    /// checked and changed inside the same write lock.
    /// </summary>
    private static void CheckState(ProductionOrder order)
    {
        switch (order.Status)
        {
            case OrderStatus.Created:
                throw new ConflictException(OrderNotSentCode,
                    $"Order #{order.Number} has not been sent yet. Send its materials before completing it.");
            case OrderStatus.Completed:
                throw new ConflictException(AlreadyCompletedCode,
                    $"Output for order #{order.Number} has already been registered.");
            case OrderStatus.Cancelled:
                throw new ConflictException(ErrorCodes.OrderCancelled, "This order is cancelled.");
        }
    }

    /// <summary>
    /// Design 2.2 step 6, AC-19.4: the leftover entered for a material cannot be more than production storage holds, since
    /// it is a count of what is physically there. Every failing line is named in one <c>400</c>
    /// <c>leftover-exceeds-stock</c>, under its own <c>lines[i].leftover</c>, in body order: "Leftover for Cheese cannot
    /// exceed the 1,020 kg in production storage."
    /// </summary>
    /// <remarks>
    /// This also keeps the ConsumedInProduction movement from ever being negative: it is storage − leftover (AC-19.2).
    /// </remarks>
    private static void CheckLeftoverAgainstStock(CompletionInput input, IReadOnlyDictionary<int, StockFigures> figures)
    {
        var errors = new ValidationErrors();
        foreach (var leftover in input.Leftovers)
        {
            var material = leftover.Line.RawMaterial;
            var inStorage = figures[material.Id].ProductionStorage;
            if (leftover.Quantity > inStorage)
            {
                errors.Add($"lines[{leftover.Index}].leftover",
                    $"Leftover for {material.Name} cannot exceed the {MessageFormat.Number(inStorage)} {material.Unit.Name} in production storage.");
            }
        }

        errors.ThrowIfAny(LeftoverExceedsStockCode);
    }

    /// <summary>
    /// AC-19.2, AC-20.1 to AC-20.4, AC-19.6, AC-19.7: every write of a completion, inside the caller's transaction.
    /// </summary>
    /// <returns>The lines whose consumed stock differs from their actual usage, for the caller to log after the commit.</returns>
    private async Task<IReadOnlyList<UsageMismatch>> WriteCompletionAsync(
        ProductionOrder order,
        CompletionInput input,
        IReadOnlyDictionary<int, StockFigures> figures,
        string userId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // AC-20.10: the tolerance in force now is stored on the order, and every figure below is judged against it. A
        // later change of the setting therefore changes nothing already recorded, including the notifications.
        var tolerance = options.Value.TolerancePercent;

        var completions = new List<(MatchedOrderLine Leftover, LineCompletion Figures)>();
        foreach (var leftover in input.Leftovers.OrderBy(leftover => leftover.Line.LineNo))
        {
            var line = leftover.Line;

            // D-9 through the one implementation in Domain/Calculations (T-004); see the class remarks for the formulas.
            var figuresOfLine = ProductionCalculations.CompleteLine(line, input.ProducedQuantity, leftover.Quantity, tolerance);
            completions.Add((leftover, figuresOfLine));

            // AC-20.3: every D-9 figure is recorded on the line. Decision #48: a negative LeftoverRemaining (the user
            // weighed less than the carried leftover, so this order also used some of it) is stored as calculated, not
            // clamped, which keeps ActualUsage equal to what was really consumed.
            line.LeftoverEntered = leftover.Quantity;
            line.LeftoverRemaining = figuresOfLine.LeftoverRemaining;
            line.ActualUsage = figuresOfLine.ActualUsage;
            line.RequiredForActualOutput = figuresOfLine.RequiredForActualOutput;
            line.UsageVariance = figuresOfLine.UsageVariance;

            // Decisions #58 and #99: stored rounded to 6 places through the one shared rule, which leaves a null (decision
            // #49) null. SQLite would otherwise keep every digit of the division. AC-20.4: the flag was decided on the
            // unrounded value, before this rounding.
            line.UsageVariancePercent = Quantity.StoredPercent(figuresOfLine.UsageVariancePercent);
            line.UsageVarianceAboveTolerance = figuresOfLine.AboveTolerance;
        }

        // AC-20.1: the output variance and its percentage are recorded on every completed order, even when zero.
        var output = ProductionCalculations.OutputVariance(order.OrderedQuantity, input.ProducedQuantity);
        order.ProducedQuantity = input.ProducedQuantity;
        order.OutputVariance = output.Variance;
        order.OutputVariancePercent = Quantity.StoredPercent(output.Percent);
        order.ToleranceAtCompletionPercent = Quantity.StoredPercent(tolerance);
        order.CompletionReason = input.Reason;

        // AC-19.2, AC-05.3: the order becomes Completed, recording who and when.
        order.Status = OrderStatus.Completed;
        order.CompletedAt = now;
        order.CompletedById = userId;
        order.StatusChangedAt = now;

        var mismatches = await WriteMovementsAsync(order, completions, figures, input.ProducedQuantity, now, userId, cancellationToken);

        // AC-19.6, PRD section 6 row 6: complementary orders exist only to top up a running order, so once it is
        // completed the unsent ones (PendingApproval, Approved) can never be used and become Cancelled with "Parent order
        // completed". The same shared code as the cancellation's (AC-21.4), so the two cannot drift apart.
        await UnsentComplementaryOrders.CancelAsync(
            db, order.Number, UnsentComplementaryOrders.ParentCompletedNote, now, userId, cancellationToken);
        AddNotifications(order, input, completions, now);
        return mismatches;
    }

    /// <summary>
    /// AC-19.2, AC-19.3, AC-20.9: the only stock changes of a completion. For each line, a ConsumedInProduction movement
    /// takes (production storage just before completion − leftover entered) out of production storage, so what remains
    /// there is exactly the leftover entered, free for later orders; no movement when that is zero (PRD section 6 row
    /// 17). Then one ProducedOutput movement puts the produced quantity of the finished product into the Warehouse. No
    /// movement corrects a variance: a variance is reported, never booked.
    /// </summary>
    /// <returns>The lines whose consumed stock differs from their actual usage (normally none).</returns>
    private async Task<IReadOnlyList<UsageMismatch>> WriteMovementsAsync(
        ProductionOrder order,
        IReadOnlyList<(MatchedOrderLine Leftover, LineCompletion Figures)> completions,
        IReadOnlyDictionary<int, StockFigures> figures,
        decimal producedQuantity,
        DateTime now,
        string userId,
        CancellationToken cancellationToken)
    {
        var mismatches = new List<UsageMismatch>();
        foreach (var (leftover, lineFigures) in completions)
        {
            var materialId = leftover.Line.RawMaterialId;
            var consumed = figures[materialId].ProductionStorage - leftover.Quantity;

            // The two ways of counting the use agree by construction (see ProductionCalculations.CompleteLine): while an
            // order runs, nothing else can change its materials in production storage (AC-12.5, AC-22.3, AC-15.10), so
            // storage before completion = carried leftover + leftover applied + warehouse send + complementary sends.
            // If they ever differ, some other path changed that stock during the run; the movement still follows
            // AC-19.2 so that AC-19.3 holds, and the difference is returned to be logged after the commit, for someone to
            // investigate (decision #98 f).
            if (consumed != lineFigures.ActualUsage)
            {
                mismatches.Add(new UsageMismatch(materialId, consumed, lineFigures.ActualUsage));
            }

            if (consumed == 0m)
            {
                continue;
            }

            // Every movement goes through the ledger (architecture 5.1); its save also saves the order and line changes
            // made so far, still inside this transaction.
            await ledger.AddAsync(new Movement
            {
                Type = MovementType.ConsumedInProduction,
                ItemId = materialId,
                ProductionStorageChange = -consumed,
                OccurredAt = now,
                UserId = userId,
                ProductionOrderNumber = order.Number,
            }, cancellationToken);
        }

        // AC-19.2: produced is always above 0 (PRD section 6 row 7), so there is always exactly one ProducedOutput.
        await ledger.AddAsync(new Movement
        {
            Type = MovementType.ProducedOutput,
            ItemId = order.FinishedProductId,
            WarehouseChange = producedQuantity,
            OccurredAt = now,
            UserId = userId,
            ProductionOrderNumber = order.Number,
        }, cancellationToken);

        return mismatches;
    }

    /// <summary>
    /// AC-23.2, the first three types, through <see cref="NotificationComposer"/> (T-005), which writes the design 7.3
    /// message and the design 5.10 data. This method decides <em>whether</em> each is raised; the composer refuses a call
    /// that would raise a wrong one. In the order of the AC-23.2 table: one UsageVariance per material above the tolerance
    /// (AC-20.4), in recipe line order; one OutputVariance when the output differs at all (AC-20.2); one
    /// LeftoverAtCompletion when any leftover is above zero (AC-19.7, PRD section 6 row 8).
    /// </summary>
    private void AddNotifications(
        ProductionOrder order,
        CompletionInput input,
        IReadOnlyList<(MatchedOrderLine Leftover, LineCompletion Figures)> completions,
        DateTime now)
    {
        foreach (var (leftover, lineFigures) in completions.Where(completion => completion.Figures.AboveTolerance))
        {
            var material = leftover.Line.RawMaterial;
            db.Notifications.Add(NotificationComposer.UsageVariance(
                order.Number, new NotificationItem(material.Id, material.Name, material.Unit.Name), lineFigures, now));
        }

        if (order.OutputVariance != 0m)
        {
            var product = order.FinishedProduct;
            db.Notifications.Add(NotificationComposer.OutputVariance(
                order.Number, new NotificationItem(product.Id, product.Name, product.Unit.Name),
                order.OrderedQuantity, input.ProducedQuantity, now));
        }

        if (input.Leftovers.Any(leftover => leftover.Quantity > 0m))
        {
            // RequireReasonForLeftover has made sure a reason exists whenever a leftover is above zero.
            var materials = input.Leftovers.Select(leftover => new NotificationMaterial(
                leftover.Line.LineNo, leftover.Line.RawMaterialId, leftover.Line.RawMaterial.Name,
                leftover.Line.RawMaterial.Unit.Name, leftover.Quantity));
            db.Notifications.Add(NotificationComposer.LeftoverAtCompletion(order.Number, materials, input.Reason!, now));
        }
    }

    /// <summary>
    /// Decision #98 (f): a line whose ConsumedInProduction quantity differs from its actual usage (D-9), collected inside the
    /// transaction and logged only after the commit.
    /// </summary>
    private sealed record UsageMismatch(int ItemId, decimal Consumed, decimal ActualUsage);

    /// <summary>
    /// The valid body: produced quantity, one leftover per order line (its <see cref="MatchedOrderLine.Quantity"/> is the
    /// leftover entered), and the cleaned reason (or null).
    /// </summary>
    private sealed record CompletionInput(decimal ProducedQuantity, IReadOnlyList<MatchedOrderLine> Leftovers, string? Reason);
}
