namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// D-5 "oldest order first": shares out the free leftover in production storage among the orders waiting in
/// Created status, so each unit of leftover counts towards exactly one order.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: leftover from earlier runs stays in production storage (decision #5). Several orders can be
/// waiting at once, and a newer one may be sent before an older one (decision #10). Without a fixed rule the
/// same 50 kg could be counted by two orders, and the warehouse would send too little for both.
/// </para>
/// <para>
/// The rule, per raw material, independently of every other material:
/// </para>
/// <list type="number">
/// <item>Take the Created orders, oldest first by creation time; orders created in the same second are taken by
/// order number, lowest first (architecture 3.3).</item>
/// <item>Each order takes the smaller of its planned amount for that material and the free leftover that older
/// orders have not already taken.</item>
/// </list>
/// <para>
/// Worked example (AC-15.5): 50 kg of free Cheese. #7 (08:00, planned 30) takes 30; #8 (09:00, planned 1,000)
/// takes the remaining 20; #9 (10:00, planned 500) takes 0. If #7 is cancelled, it is no longer Created, so #8
/// takes 50 and #9 still takes 0.
/// </para>
/// <para>
/// The result is recalculated from current data every time it is needed. It is pure: no database, no clock.
/// </para>
/// </remarks>
public static class LeftoverAllocator
{
    /// <summary>
    /// D-5: allocates the free leftover of each raw material to the Created orders, oldest first.
    /// </summary>
    /// <param name="orders">
    /// The orders to consider. Their <see cref="ProductionOrder.Lines"/> <b>must be loaded</b> (with EF Core,
    /// <c>Include(o =&gt; o.Lines)</c>). An order whose lines were not loaded has an empty list, so it would
    /// silently take no leftover, and younger orders would get its share. Orders in any status other than Created
    /// are ignored, so passing a cancelled or running order cannot take leftover by mistake.
    /// </param>
    /// <remarks>
    /// A negative free-leftover value is treated as 0 (D-4).
    /// </remarks>
    /// <param name="freeLeftoverByMaterial">
    /// Free leftover per raw material id (D-4, from <see cref="ProductionCalculations.FreeLeftover"/>). A material
    /// that is missing has no free leftover.
    /// </param>
    /// <returns>The allocated amount for every (order, material) pair; see <see cref="LeftoverAllocation.For"/>.</returns>
    public static LeftoverAllocation Allocate(
        IEnumerable<ProductionOrder> orders, IReadOnlyDictionary<int, decimal> freeLeftoverByMaterial)
    {
        // D-5: only Created orders wait for leftover, and the oldest is served first. The number breaks ties
        // because creation times are stored to the whole second, so two orders can share one.
        var waitingOrders = orders
            .Where(order => order.Status == OrderStatus.Created)
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Number);

        // What is still unallocated per material. It starts at the free leftover and shrinks as orders take
        // their share, which is what makes each unit count for one order only.
        var notYetAllocated = new Dictionary<int, decimal>(freeLeftoverByMaterial);
        var amounts = new Dictionary<(int OrderNumber, int RawMaterialId), decimal>();

        foreach (var order in waitingOrders)
        {
            foreach (var line in order.Lines)
            {
                // D-4 says free leftover is never below zero. The guard keeps a wrong negative input from giving
                // an order a negative allocation (which would raise its nominal send above the planned amount).
                var available = Math.Max(0m, notYetAllocated.GetValueOrDefault(line.RawMaterialId, 0m));

                // D-5: an order never takes more than it plans to use, so the rest stays for younger orders.
                var taken = Math.Min(line.PlannedAmount, available);

                amounts[(order.Number, line.RawMaterialId)] = taken;
                notYetAllocated[line.RawMaterialId] = available - taken;
            }
        }

        return new LeftoverAllocation(amounts);
    }
}
