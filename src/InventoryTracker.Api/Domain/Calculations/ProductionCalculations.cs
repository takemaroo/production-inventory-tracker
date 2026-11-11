using InventoryTracker.Api.Common.Quantities;

namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// The production arithmetic of PRD section 4.3 (D-3, D-4, D-7, D-8, D-9), as pure functions.
/// </summary>
/// <remarks>
/// <para>
/// "Pure" means each method works only from its arguments and returns a result: no database, no clock, no
/// configuration, and nothing it receives is changed. The services (order creation, send, completion, stock)
/// load the numbers, call these methods, and store or return the results (architecture 3.3). Keeping the
/// arithmetic here, in one place, means it is written once and every worked example in the PRD can be checked
/// by a fast unit test without starting the application.
/// </para>
/// <para>
/// Tolerance <c>t</c> is always passed as a percentage, as configured (2 means 2%), so the formulas below
/// divide it by 100. Inputs are quantities with at most 3 decimal places and recipe amounts with at most 6,
/// already validated by the services (PRD 4.1).
/// </para>
/// <para>
/// Rounding (PRD 4.1, PRD section 6 row 12): the values the PRD calls "computed" (planned amount, range limits,
/// required amount) are rounded to 3 places, half away from zero, with <see cref="Quantity.Round3"/>. Sums and
/// differences of 3-place quantities are already exact, so they are not rounded again. Percentages are returned
/// unrounded, because threshold comparisons must use the unrounded value (AC-20.4).
/// </para>
/// <para>
/// D-5 (oldest order first) and D-6 (leftover applied) live in <see cref="LeftoverAllocator"/>.
/// </para>
/// </remarks>
public static class ProductionCalculations
{
    /// <summary>
    /// AC-13.1: the planned amount of one order line, P = amount per unit × ordered quantity, rounded to 3 places.
    /// </summary>
    /// <remarks>
    /// Example (AC-08.3): 1 kg of Cheese per bag × 1,000 bags = 1,000 kg. With 0.015 kg of starch per bag,
    /// 1,000 bags plan 15 kg. The result is fixed on the line at creation and never recalculated (AC-08.4).
    /// </remarks>
    /// <param name="amountPerUnit">Recipe amount of the raw material per 1 unit of finished product (up to 6 places).</param>
    /// <param name="orderedQuantity">Ordered quantity of finished product.</param>
    public static decimal PlannedAmount(decimal amountPerUnit, decimal orderedQuantity)
        => Quantity.Round3(amountPerUnit * orderedQuantity);

    /// <summary>
    /// D-7: the accepted send range for an order line with planned amount P and leftover applied L.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule: a send S is accepted when S ≥ 0 and P × (1 − t) ≤ L + S ≤ P × (1 + t). Rearranged for S:
    /// S runs from max(0, P × (1 − t) − L) to P × (1 + t) − L. The nominal send is P − L.
    /// </para>
    /// <para>
    /// The window is always as wide as t applied to the <b>full</b> planned amount P, not to the net amount
    /// P − L, because the scales weigh the whole quantity that ends up in production (decision #3).
    /// </para>
    /// <para>
    /// Worked example (AC-15.3): P = 1,000 kg, L = 5 kg, t = 2. Lower limit 1,000 × 0.98 − 5 = 975; upper limit
    /// 1,000 × 1.02 − 5 = 1,015; nominal 995. When leftover covers the need (AC-15.4: P = 30, L = 30), the lower
    /// limit 29.4 − 30 is negative, so it becomes 0 and the range is 0 to 0.6.
    /// </para>
    /// <para>
    /// The limits are rounded to 3 places (architecture 4.1 item 4) so that the range the API shows is exactly
    /// the range it accepts. With t = 0 the range is exactly the nominal send.
    /// </para>
    /// </remarks>
    /// <param name="plannedAmount">P, the line's planned amount.</param>
    /// <param name="leftoverApplied">L, the free leftover allocated to this order (D-5, D-6). At most P.</param>
    /// <param name="tolerancePercent">t as a percentage (2 means 2%).</param>
    public static AcceptedRange SendRange(decimal plannedAmount, decimal leftoverApplied, decimal tolerancePercent)
    {
        var lowerFactor = 1m - tolerancePercent / 100m;
        var upperFactor = 1m + tolerancePercent / 100m;

        // D-7 "S ≥ 0": when the leftover alone already reaches the lower limit, nothing has to be sent.
        var min = Quantity.Round3(Math.Max(0m, plannedAmount * lowerFactor - leftoverApplied));
        var max = Quantity.Round3(plannedAmount * upperFactor - leftoverApplied);
        var nominal = plannedAmount - leftoverApplied;

        return new AcceptedRange(min, max, nominal);
    }

    /// <summary>
    /// AC-18.1: the accepted send range for a complementary order line with requested amount R: from
    /// R × (1 − t) to R × (1 + t), limits included and rounded to 3 places. No leftover is netted.
    /// </summary>
    /// <remarks>
    /// Worked example (AC-18.2): R = 20 kg, t = 2 gives 19.6 to 20.4 kg. The nominal is R itself.
    /// </remarks>
    /// <param name="requestedAmount">R, the amount requested on the complementary order line.</param>
    /// <param name="tolerancePercent">t as a percentage (2 means 2%).</param>
    public static AcceptedRange ComplementaryRange(decimal requestedAmount, decimal tolerancePercent)
    {
        var min = Quantity.Round3(requestedAmount * (1m - tolerancePercent / 100m));
        var max = Quantity.Round3(requestedAmount * (1m + tolerancePercent / 100m));
        return new AcceptedRange(min, max, requestedAmount);
    }

    /// <summary>
    /// D-7, AC-18.1: true when <paramref name="quantity"/> lies inside <paramref name="range"/>, both limits
    /// included. Exactly 2% over or under the plan is accepted (PRD section 6 row 5).
    /// </summary>
    /// <remarks>
    /// Example (AC-15.2, range 980 to 1,020): 980 and 1,020 are accepted, 979.999 and 1,020.001 are not.
    /// A negative quantity is always outside, because <see cref="AcceptedRange.Min"/> is never below 0.
    /// </remarks>
    /// <param name="quantity">The quantity the warehouse user wants to send.</param>
    /// <param name="range">The rounded range from <see cref="SendRange"/> or <see cref="ComplementaryRange"/>.</param>
    public static bool IsWithin(decimal quantity, AcceptedRange range)
        => quantity >= range.Min && quantity <= range.Max;

    /// <summary>
    /// D-3: the quantity of one material committed to the order that is InProduction:
    /// leftover applied + warehouse send + complementary sends.
    /// </summary>
    /// <remarks>
    /// Worked example A (AC-20.5): 5 + 995 + 20 = 1,020 kg, which is exactly what production storage holds for
    /// that order. Committed material is not free leftover (D-4), so no other order can claim it.
    /// </remarks>
    /// <param name="leftoverApplied">The line's leftover applied (D-6).</param>
    /// <param name="warehouseSend">The line's warehouse send.</param>
    /// <param name="complementarySends">Total sent for this material through complementary orders so far.</param>
    public static decimal Committed(decimal leftoverApplied, decimal warehouseSend, decimal complementarySends)
        => leftoverApplied + warehouseSend + complementarySends;

    /// <summary>
    /// D-4: free leftover of a raw material = production storage stock − material committed to the InProduction
    /// order − quantity in Pending returns, and never less than zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Free leftover is what later orders may use (D-5). Material in a Pending return is no longer free, even
    /// though it has not physically moved yet (AC-22.1).
    /// </para>
    /// <para>
    /// Example (AC-15.5, after order #8 is sent first): storage 50 + 980 = 1,030 kg, committed to #8
    /// 20 + 980 + 0 = 1,000 kg, no Pending returns, so free leftover is 1,030 − 1,000 − 0 = 30 kg.
    /// </para>
    /// </remarks>
    /// <param name="productionStorageStock">The material's current stock in production storage (D-2).</param>
    /// <param name="committedToInProductionOrder">D-3 for the InProduction order, or 0 when no running order uses the material.</param>
    /// <param name="pendingReturns">Total quantity of this material in Pending returns.</param>
    public static decimal FreeLeftover(decimal productionStorageStock, decimal committedToInProductionOrder, decimal pendingReturns)
        => Math.Max(0m, productionStorageStock - committedToInProductionOrder - pendingReturns);

    /// <summary>
    /// D-8: carried leftover = production storage stock just before the send − leftover applied. It is leftover
    /// that was already in production storage but did not count towards this order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is zero in the normal case. It is above zero when the leftover was more than this order needed, or
    /// when part of it was set aside for an older Created order (D-5).
    /// </para>
    /// <para>
    /// Examples: AC-15.4, storage 50 kg, applied 30 kg, carried 20 kg. Worked example D (AC-20.8), storage
    /// 50 kg, applied 20 kg, carried 30 kg. It is stored on the line so completion can tell this order's
    /// remaining material apart from the carried leftover (D-9, PRD section 6 row 1).
    /// </para>
    /// </remarks>
    /// <param name="productionStorageBeforeSend">The material's production storage stock just before the send.</param>
    /// <param name="leftoverApplied">The leftover applied fixed on the line at this send (D-6).</param>
    public static decimal CarriedLeftover(decimal productionStorageBeforeSend, decimal leftoverApplied)
        => productionStorageBeforeSend - leftoverApplied;

    /// <summary>
    /// D-9: the usage figures of one order line at completion, from the figures fixed at send and the leftover
    /// the production user enters.
    /// </summary>
    /// <remarks>
    /// <para>The steps, with worked example A (AC-20.5) alongside:</para>
    /// <list type="number">
    /// <item>RequiredForActualOutput = amount per unit × produced quantity, rounded to 3 places: 1 × 1,001 = 1,001 kg.</item>
    /// <item>LeftoverRemaining = leftover entered − carried leftover: 4 − 0 = 4 kg. The user enters the physical
    /// total in production storage (decision #9), so the carried leftover that never belonged to this order is
    /// taken off first.</item>
    /// <item>ActualUsage = leftover applied + warehouse send + complementary sends − LeftoverRemaining:
    /// 5 + 995 + 20 − 4 = 1,016 kg. This always equals the ConsumedInProduction quantity of AC-19.2
    /// (production storage before completion − leftover entered), because while the order runs nothing else can
    /// change that material in production storage. Storage before completion is therefore exactly carried
    /// leftover + leftover applied + warehouse send + complementary sends. Three rules make sure of it: no
    /// production-storage adjustment of the running order's materials (AC-12.5), no return of them (AC-22.3), and
    /// no second order in production (AC-15.10).</item>
    /// <item>Usage variance = ActualUsage − RequiredForActualOutput: 1,016 − 1,001 = +15 kg.</item>
    /// <item>Usage variance % = usage variance ÷ RequiredForActualOutput × 100: 15 ÷ 1,001 × 100 = 1.4985...%,
    /// shown as +1.50%.</item>
    /// <item>Above tolerance when |usage variance %| &gt; t: 1.4985 is not above 2, so no notification. Both
    /// directions count (decision #12): −2.50% is above a 2% tolerance (AC-20.7).</item>
    /// </list>
    /// <para>
    /// LeftoverRemaining is negative when the user enters less than the carried leftover, which means the order
    /// also used some of the carried material. The formula is applied as the PRD states it; ActualUsage then
    /// still equals the quantity actually consumed.
    /// </para>
    /// <para>
    /// When RequiredForActualOutput rounds to 0 (decision #49), the percentage cannot be calculated, so
    /// <see cref="LineCompletion.UsageVariancePercent"/> is null. The line is above tolerance whenever any material
    /// was used, and not above tolerance when nothing was used.
    /// </para>
    /// </remarks>
    /// <param name="line">The order line, with the send figures (leftover applied, warehouse send, complementary
    /// sends, carried leftover) already fixed. It is read, not changed.</param>
    /// <param name="producedQuantity">Finished product actually made (greater than 0).</param>
    /// <param name="leftoverEntered">Physical total of this material left in production storage (0 or more).</param>
    /// <param name="tolerancePercent">t as a percentage, the value in force at completion (AC-20.10).</param>
    /// <exception cref="InvalidOperationException">The line has not been sent, so its send figures are missing.</exception>
    public static LineCompletion CompleteLine(
        ProductionOrderLine line, decimal producedQuantity, decimal leftoverEntered, decimal tolerancePercent)
    {
        var leftoverApplied = SentValue(line.LeftoverApplied, nameof(line.LeftoverApplied), line);
        var warehouseSend = SentValue(line.WarehouseSend, nameof(line.WarehouseSend), line);
        var complementarySends = SentValue(line.ComplementarySends, nameof(line.ComplementarySends), line);
        var carriedLeftover = SentValue(line.CarriedLeftover, nameof(line.CarriedLeftover), line);

        // D-9 and PRD 4.1: the required amount is a computed quantity, so it is rounded to 3 places. It uses the
        // amount per unit copied onto the line at creation, not the current recipe (AC-08.4).
        var required = Quantity.Round3(line.AmountPerUnit * producedQuantity);

        // D-9 and PRD section 6 row 1: only leftover above the carried amount is this order's own leftover.
        var leftoverRemaining = leftoverEntered - carriedLeftover;

        var actualUsage = Committed(leftoverApplied, warehouseSend, complementarySends) - leftoverRemaining;
        var usageVariance = actualUsage - required;

        if (required == 0m)
        {
            // Decision #49 (owner, option b): a tiny recipe amount times a tiny output, for example
            // 0.0004 kg × 1 bag, rounds to 0 kg required. The percentage would then divide by zero, so it has no
            // value and is returned as null; the PRD defines no number to put there. Any usage at all is
            // infinitely far above "nothing required", so it is above every tolerance and notifies. Zero usage
            // against zero required is no variance, so it does not notify. The other figures are normal.
            return new LineCompletion(required, leftoverRemaining, actualUsage, usageVariance,
                UsageVariancePercent: null, AboveTolerance: actualUsage != 0m);
        }

        // Multiplying before dividing keeps every digit until the one division, so the result is as exact as
        // decimal allows. It is the same value as variance ÷ required × 100.
        var usageVariancePercent = usageVariance * 100m / required;

        // AC-20.4: compare the unrounded percentage, limit excluded: exactly 2% does not notify, 2.0001% does.
        // The comparison is done as |variance| × 100 > t × required, which is |percent| > t with both sides
        // multiplied by the (positive) required amount. That needs no division, so no rounding can ever tip a
        // value that sits exactly on the limit to one side or the other.
        var aboveTolerance = Math.Abs(usageVariance) * 100m > tolerancePercent * required;

        return new LineCompletion(required, leftoverRemaining, actualUsage, usageVariance, usageVariancePercent, aboveTolerance);
    }

    /// <summary>
    /// D-9: output variance = produced quantity − ordered quantity, and its percentage of the ordered quantity.
    /// </summary>
    /// <remarks>
    /// Worked example A (AC-20.5): ordered 1,000 bags, produced 1,001: variance +1 bag, 1 ÷ 1,000 × 100 = +0.10%.
    /// Example C (AC-20.7): ordered and produced 1,000: variance 0, and no notification. Output variance has no
    /// tolerance (D-1); see <see cref="OutputVarianceResult.RaisesNotification"/>.
    /// </remarks>
    /// <param name="orderedQuantity">The order's ordered quantity (greater than 0, validated at creation).</param>
    /// <param name="producedQuantity">The produced quantity entered at completion.</param>
    public static OutputVarianceResult OutputVariance(decimal orderedQuantity, decimal producedQuantity)
    {
        var variance = producedQuantity - orderedQuantity;
        return new OutputVarianceResult(variance, variance * 100m / orderedQuantity);
    }

    /// <summary>
    /// Reads a send figure of the line, failing loudly when it is still empty. Completion is only possible for
    /// an InProduction order, whose lines always have these figures, so an empty one is a programming error.
    /// </summary>
    private static decimal SentValue(decimal? value, string field, ProductionOrderLine line)
        => value ?? throw new InvalidOperationException(
            $"Order line for raw material {line.RawMaterialId} has no {field}; it must be sent before it can be completed.");
}
