using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using static InventoryTracker.Api.Tests.Unit.Calculations.CalculationTestData;

namespace InventoryTracker.Api.Tests.Unit.Calculations;

/// <summary>
/// D-3 (committed material), D-4 (free leftover), D-5 and D-6 (oldest order first, leftover applied) and D-8
/// (carried leftover), with the numbers of AC-15.3, AC-15.4, AC-15.5 and AC-20.8.
/// </summary>
public class LeftoverTests
{
    // ---- D-3 committed material --------------------------------------------------------------------------

    /// <summary>D-3 with worked example A (AC-20.5): 5 applied + 995 sent + 20 complementary = 1,020 kg committed.</summary>
    [Fact]
    public void D_3_committed_is_applied_plus_send_plus_complementary()
    {
        Assert.Equal(1020m, ProductionCalculations.Committed(5m, 995m, 20m));
    }

    // ---- D-4 free leftover -------------------------------------------------------------------------------

    /// <summary>D-4: storage stock minus committed minus Pending returns.</summary>
    [Fact]
    public void D_4_free_leftover_subtracts_committed_and_pending_returns()
    {
        Assert.Equal(25m, ProductionCalculations.FreeLeftover(50m, 20m, 5m));
    }

    /// <summary>
    /// AC-15.5 bullet 2: after #8 is sent first, storage is 50 + 980 = 1,030 kg and 20 + 980 = 1,000 kg is
    /// committed to #8, so 30 kg is still free leftover.
    /// </summary>
    [Fact]
    public void AC_15_5_free_leftover_after_newer_order_is_sent_is_30_kg()
    {
        var committedToOrder8 = ProductionCalculations.Committed(20m, 980m, 0m);

        Assert.Equal(30m, ProductionCalculations.FreeLeftover(1030m, committedToOrder8, 0m));
    }

    /// <summary>AC-22.1 and D-4: a Pending return of the whole 20 kg leaves 0 kg free.</summary>
    [Fact]
    public void AC_22_1_pending_return_is_not_free_leftover()
    {
        Assert.Equal(0m, ProductionCalculations.FreeLeftover(20m, 0m, 20m));
    }

    /// <summary>D-4 "never less than zero": committed plus pending above the stock gives 0, not a negative amount.</summary>
    [Fact]
    public void D_4_free_leftover_is_never_negative()
    {
        Assert.Equal(0m, ProductionCalculations.FreeLeftover(10m, 8m, 5m));
    }

    // ---- D-8 carried leftover ----------------------------------------------------------------------------

    /// <summary>
    /// D-8 with AC-15.4 (storage 50, applied 30: carried 20), worked example D (AC-20.8: storage 50, applied 20:
    /// carried 30) and worked example A (AC-20.5: storage 5, applied 5: carried 0, the normal case).
    /// </summary>
    [Theory]
    [InlineData("50", "30", "20")]
    [InlineData("50", "20", "30")]
    [InlineData("5", "5", "0")]
    public void D_8_carried_leftover_is_storage_before_send_minus_applied(string storageBefore, string applied, string expected)
    {
        Assert.Equal(D(expected), ProductionCalculations.CarriedLeftover(D(storageBefore), D(applied)));
    }

    /// <summary>
    /// AC-15.4 as a whole: 50 kg free, planned 30 kg. Leftover applied 30, carried 20, and after the zero send the
    /// 20 kg not applied is still free leftover for later orders (storage 50 − committed 30 = 20).
    /// </summary>
    [Fact]
    public void AC_15_4_unapplied_leftover_is_carried_and_stays_free()
    {
        var order = Order(1, At(8), OrderStatus.Created, (Cheese, 30m));

        var applied = LeftoverAllocator.Allocate([order], new Dictionary<int, decimal> { [Cheese] = 50m }).For(1, Cheese);
        var carried = ProductionCalculations.CarriedLeftover(50m, applied);
        var freeAfterZeroSend = ProductionCalculations.FreeLeftover(50m, ProductionCalculations.Committed(applied, 0m, 0m), 0m);

        Assert.Equal(30m, applied);
        Assert.Equal(20m, carried);
        Assert.Equal(20m, freeAfterZeroSend);
    }

    /// <summary>AC-15.3: 5 kg of free leftover and planned 1,000 kg gives leftover applied 5 kg, and a 995 kg send then fills storage to 1,000 kg.</summary>
    [Fact]
    public void AC_15_3_leftover_applied_is_5_kg_and_storage_reaches_1000_kg()
    {
        var order = Order(12, At(8), OrderStatus.Created, (Cheese, 1000m));

        var applied = LeftoverAllocator.Allocate([order], new Dictionary<int, decimal> { [Cheese] = 5m }).For(12, Cheese);

        Assert.Equal(5m, applied);
        Assert.Equal(0m, ProductionCalculations.CarriedLeftover(5m, applied));
        // Production storage after the send = what was there (5) + the send (995), all of it committed to #12.
        Assert.Equal(1000m, ProductionCalculations.Committed(applied, 995m, 0m));
    }

    // ---- D-5 oldest order first (AC-15.5) ----------------------------------------------------------------

    /// <summary>The three Created orders of AC-15.5: #7 at 08:00 (30 kg), #8 at 09:00 (1,000 kg), #9 at 10:00 (500 kg).</summary>
    private static List<ProductionOrder> Ac155Orders() =>
    [
        Order(7, At(8), OrderStatus.Created, (Cheese, 30m)),
        Order(8, At(9), OrderStatus.Created, (Cheese, 1000m)),
        Order(9, At(10), OrderStatus.Created, (Cheese, 500m)),
    ];

    private static readonly Dictionary<int, decimal> FiftyKgOfCheese = new() { [Cheese] = 50m };

    /// <summary>
    /// AC-15.5 bullet 1 (estimates): with 50 kg free, #7 leftover 30, nominal 0, range 0 to 0.6; #8 leftover 20,
    /// nominal 980, range 960 to 1,000; #9 leftover 0, nominal 500, range 490 to 510.
    /// </summary>
    [Fact]
    public void AC_15_5_estimates_allocate_leftover_to_the_oldest_order_first()
    {
        var orders = Ac155Orders();

        var allocation = LeftoverAllocator.Allocate(orders, FiftyKgOfCheese);

        Assert.Equal(30m, allocation.For(7, Cheese));
        Assert.Equal(20m, allocation.For(8, Cheese));
        Assert.Equal(0m, allocation.For(9, Cheese));
        Assert.Equal(new AcceptedRange(0m, 0.6m, 0m), ProductionCalculations.SendRange(30m, allocation.For(7, Cheese), DefaultTolerance));
        Assert.Equal(new AcceptedRange(960m, 1000m, 980m), ProductionCalculations.SendRange(1000m, allocation.For(8, Cheese), DefaultTolerance));
        Assert.Equal(new AcceptedRange(490m, 510m, 500m), ProductionCalculations.SendRange(500m, allocation.For(9, Cheese), DefaultTolerance));
    }

    /// <summary>
    /// AC-15.5 bullet 2 and D-6: #8 sent before #7 gets 20 kg (not 50), because 30 kg is allocated to the older #7.
    /// After the send, #8 is InProduction and the 30 kg still free stays allocated to #7.
    /// </summary>
    [Fact]
    public void AC_15_5_newer_order_sent_first_only_gets_leftover_not_allocated_to_older_orders()
    {
        var orders = Ac155Orders();

        // D-6: at the moment of sending #8, its leftover applied is its allocation calculated at that moment.
        var appliedToOrder8 = LeftoverAllocator.Allocate(orders, FiftyKgOfCheese).For(8, Cheese);
        Assert.Equal(20m, appliedToOrder8);

        // The send of 980 kg is accepted; #8 becomes InProduction and its 1,000 kg are committed.
        Assert.True(ProductionCalculations.IsWithin(980m, ProductionCalculations.SendRange(1000m, appliedToOrder8, DefaultTolerance)));
        orders.Single(o => o.Number == 8).Status = OrderStatus.InProduction;
        var freeAfterSend = ProductionCalculations.FreeLeftover(1030m, ProductionCalculations.Committed(appliedToOrder8, 980m, 0m), 0m);

        var allocationAfterSend = LeftoverAllocator.Allocate(orders, new Dictionary<int, decimal> { [Cheese] = freeAfterSend });

        Assert.Equal(30m, allocationAfterSend.For(7, Cheese));
        Assert.Equal(0m, allocationAfterSend.For(8, Cheese));
        Assert.Equal(0m, allocationAfterSend.For(9, Cheese));
    }

    /// <summary>
    /// AC-15.5 bullet 3 and AC-21.2: when #7 is cancelled while Created its allocation is released, so #8 shows
    /// leftover 50, nominal send 950, range 930 to 970 (and #9 still 0).
    /// </summary>
    [Fact]
    public void AC_15_5_cancelling_the_oldest_order_releases_its_allocation()
    {
        var orders = Ac155Orders();
        orders.Single(o => o.Number == 7).Status = OrderStatus.Cancelled;

        var allocation = LeftoverAllocator.Allocate(orders, FiftyKgOfCheese);

        Assert.Equal(0m, allocation.For(7, Cheese));
        Assert.Equal(50m, allocation.For(8, Cheese));
        Assert.Equal(0m, allocation.For(9, Cheese));
        Assert.Equal(new AcceptedRange(930m, 970m, 950m), ProductionCalculations.SendRange(1000m, allocation.For(8, Cheese), DefaultTolerance));
    }

    /// <summary>
    /// AC-20.8 (worked example D, last step): after #8 completes, production storage holds 30 kg and no order is
    /// InProduction, so the 30 kg is free and is again allocated to #7.
    /// </summary>
    [Fact]
    public void AC_20_8_leftover_after_completion_is_allocated_to_order_7_again()
    {
        var orders = Ac155Orders();
        orders.Single(o => o.Number == 8).Status = OrderStatus.Completed;
        var free = ProductionCalculations.FreeLeftover(30m, 0m, 0m);

        var allocation = LeftoverAllocator.Allocate(orders, new Dictionary<int, decimal> { [Cheese] = free });

        Assert.Equal(30m, allocation.For(7, Cheese));
        Assert.Equal(0m, allocation.For(9, Cheese));
    }

    /// <summary>D-5 "each unit of leftover is allocated to one order only": the order in which orders are passed in does not matter, and the total never exceeds the free leftover.</summary>
    [Fact]
    public void D_5_input_order_does_not_change_the_allocation()
    {
        var orders = Ac155Orders();
        orders.Reverse();

        var allocation = LeftoverAllocator.Allocate(orders, FiftyKgOfCheese);

        Assert.Equal(30m, allocation.For(7, Cheese));
        Assert.Equal(20m, allocation.For(8, Cheese));
        Assert.Equal(0m, allocation.For(9, Cheese));
        Assert.Equal(50m, allocation.For(7, Cheese) + allocation.For(8, Cheese) + allocation.For(9, Cheese));
    }

    /// <summary>D-5 and architecture 3.3: orders created in the same second are served by order number, lowest first.</summary>
    [Fact]
    public void D_5_same_creation_time_is_broken_by_order_number()
    {
        var orders = new[]
        {
            Order(21, At(8), OrderStatus.Created, (Cheese, 40m)),
            Order(20, At(8), OrderStatus.Created, (Cheese, 40m)),
        };

        var allocation = LeftoverAllocator.Allocate(orders, FiftyKgOfCheese);

        Assert.Equal(40m, allocation.For(20, Cheese));
        Assert.Equal(10m, allocation.For(21, Cheese));
    }

    /// <summary>
    /// D-5 "per material": each raw material is shared out on its own. Here the older order needs no starch, so a
    /// newer order gets all of it, while Cheese still goes to the older order first.
    /// </summary>
    [Fact]
    public void D_5_each_material_is_allocated_independently()
    {
        var orders = new[]
        {
            Order(1, At(8), OrderStatus.Created, (Cheese, 30m)),
            Order(2, At(9), OrderStatus.Created, (Cheese, 1000m), (PotatoStarch, 15m)),
        };
        var free = new Dictionary<int, decimal> { [Cheese] = 50m, [PotatoStarch] = 4m };

        var allocation = LeftoverAllocator.Allocate(orders, free);

        Assert.Equal(30m, allocation.For(1, Cheese));
        Assert.Equal(0m, allocation.For(1, PotatoStarch));
        Assert.Equal(20m, allocation.For(2, Cheese));
        Assert.Equal(4m, allocation.For(2, PotatoStarch));
    }

    /// <summary>D-4 "never less than zero" and D-5: a negative free-leftover input is treated as 0, so no order gets a negative allocation.</summary>
    [Fact]
    public void D_5_negative_free_leftover_allocates_zero()
    {
        var order = Order(1, At(8), OrderStatus.Created, (Cheese, 30m));

        var allocation = LeftoverAllocator.Allocate([order], new Dictionary<int, decimal> { [Cheese] = -5m });

        Assert.Equal(0m, allocation.For(1, Cheese));
    }

    /// <summary>D-4 and D-5: a material with no free leftover (missing from the input) allocates 0, so the nominal send is the full planned amount.</summary>
    [Fact]
    public void D_5_material_without_free_leftover_allocates_zero()
    {
        var order = Order(1, At(8), OrderStatus.Created, (PotatoStarch, 15m));

        var allocation = LeftoverAllocator.Allocate([order], FiftyKgOfCheese);

        Assert.Equal(0m, allocation.For(1, PotatoStarch));
    }
}
