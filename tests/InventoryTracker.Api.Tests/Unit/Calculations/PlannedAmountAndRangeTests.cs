using InventoryTracker.Api.Domain.Calculations;
using static InventoryTracker.Api.Tests.Unit.Calculations.CalculationTestData;

namespace InventoryTracker.Api.Tests.Unit.Calculations;

/// <summary>
/// AC-13.1 (planned amount), D-7 and AC-15.2 to AC-15.4 (send ranges), AC-18.1 and AC-18.2 (complementary
/// ranges), with the inclusive-limit and rounding rules of PRD 4.1, PRD section 6 rows 5 and 12, and
/// architecture 4.1 item 4.
/// </summary>
public class PlannedAmountAndRangeTests
{
    // ---- Planned amount (AC-13.1) ------------------------------------------------------------------------

    /// <summary>AC-08.3: 1 kg of Cheese per bag, 1,000 bags ordered, plans 1,000 kg of Cheese.</summary>
    [Fact]
    public void AC_08_3_one_kg_per_bag_times_1000_bags_plans_1000_kg()
    {
        Assert.Equal(1000m, ProductionCalculations.PlannedAmount(1m, 1000m));
    }

    /// <summary>AC-08.4 (and AC-13.1): after the recipe changes to 1.01 kg per bag, a new order for 1,000 bags plans 1,010 kg.</summary>
    [Fact]
    public void AC_08_4_new_recipe_amount_gives_1010_kg()
    {
        Assert.Equal(1010m, ProductionCalculations.PlannedAmount(1.01m, 1000m));
    }

    /// <summary>
    /// AC-13.1 and PRD 4.1: planned = amount per unit × ordered quantity, rounded to 3 places half away from zero.
    /// Includes the design 5.7 Potato starch line (0.015 × 1,000 = 15) and a midpoint (0.0005 → 0.001).
    /// </summary>
    [Theory]
    [InlineData("0.015", "1000", "15")]
    [InlineData("0.0005", "1", "0.001")]
    [InlineData("0.0004", "1", "0")]
    [InlineData("0.333333", "3", "1")]
    [InlineData("1.234567", "10.5", "12.963")]
    public void AC_13_1_planned_amount_is_rounded_to_three_places(string amountPerUnit, string ordered, string expected)
    {
        Assert.Equal(D(expected), ProductionCalculations.PlannedAmount(D(amountPerUnit), D(ordered)));
    }

    // ---- Send range, no leftover (AC-15.2) ---------------------------------------------------------------

    /// <summary>AC-15.2: planned 1,000 kg, leftover applied 0, t = 2: range 980 to 1,020, nominal 1,000.</summary>
    [Fact]
    public void AC_15_2_range_without_leftover_is_980_to_1020()
    {
        var range = ProductionCalculations.SendRange(1000m, 0m, DefaultTolerance);

        Assert.Equal(new AcceptedRange(980m, 1020m, 1000m), range);
    }

    /// <summary>
    /// AC-15.2 and PRD section 6 row 5: 980, 1,000 and 1,020 kg are accepted (exactly 2% under and over is inside);
    /// 979.999 and 1,020.001 kg are rejected.
    /// </summary>
    [Theory]
    [InlineData("980", true)]
    [InlineData("1000", true)]
    [InlineData("1020", true)]
    [InlineData("979.999", false)]
    [InlineData("1020.001", false)]
    public void AC_15_2_limits_are_included(string send, bool accepted)
    {
        var range = ProductionCalculations.SendRange(1000m, 0m, DefaultTolerance);

        Assert.Equal(accepted, ProductionCalculations.IsWithin(D(send), range));
    }

    // ---- Leftover netting (AC-15.3) ----------------------------------------------------------------------

    /// <summary>AC-15.3: planned 1,000 kg with 5 kg of leftover applied: nominal send 995, range 975 to 1,015.</summary>
    [Fact]
    public void AC_15_3_leftover_is_netted_but_window_uses_full_planned_amount()
    {
        var range = ProductionCalculations.SendRange(1000m, 5m, DefaultTolerance);

        Assert.Equal(new AcceptedRange(975m, 1015m, 995m), range);
    }

    /// <summary>AC-15.3: sending 995 kg is accepted; 974.999 and 1,015.001 kg are rejected; the limits 975 and 1,015 are accepted.</summary>
    [Theory]
    [InlineData("995", true)]
    [InlineData("975", true)]
    [InlineData("1015", true)]
    [InlineData("974.999", false)]
    [InlineData("1015.001", false)]
    public void AC_15_3_sends_against_netted_range(string send, bool accepted)
    {
        var range = ProductionCalculations.SendRange(1000m, 5m, DefaultTolerance);

        Assert.Equal(accepted, ProductionCalculations.IsWithin(D(send), range));
    }

    // ---- Leftover covers the need (AC-15.4) --------------------------------------------------------------

    /// <summary>
    /// AC-15.4: 50 kg free leftover, planned 30 kg, so leftover applied is 30 kg. Nominal send 0; the lower limit
    /// 29.4 − 30 is negative and becomes 0 (D-7 "S ≥ 0"); the range is 0 to 0.6 kg.
    /// </summary>
    [Fact]
    public void AC_15_4_range_is_0_to_0_6_when_leftover_covers_the_need()
    {
        var range = ProductionCalculations.SendRange(30m, 30m, DefaultTolerance);

        Assert.Equal(new AcceptedRange(0m, 0.6m, 0m), range);
    }

    /// <summary>AC-15.4: a zero send is accepted when the leftover covers the need; 0.6 kg (the limit) is accepted; 1 kg is rejected.</summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("0.6", true)]
    [InlineData("0.601", false)]
    [InlineData("1", false)]
    public void AC_15_4_zero_send_is_accepted_and_1_kg_is_rejected(string send, bool accepted)
    {
        var range = ProductionCalculations.SendRange(30m, 30m, DefaultTolerance);

        Assert.Equal(accepted, ProductionCalculations.IsWithin(D(send), range));
    }

    /// <summary>D-7 "S ≥ 0": a negative send is never inside a range, even one that starts at 0 (AC-15.8 also rejects it earlier).</summary>
    [Fact]
    public void D_7_negative_send_is_outside_every_range()
    {
        var range = ProductionCalculations.SendRange(30m, 30m, DefaultTolerance);

        Assert.False(ProductionCalculations.IsWithin(-0.001m, range));
    }

    // ---- Rounding and tolerance edge cases ---------------------------------------------------------------

    /// <summary>
    /// T-004 criterion: with tolerance 0 the window is exactly the nominal send, so only P − L is accepted.
    /// </summary>
    [Fact]
    public void Tolerance_zero_accepts_exactly_the_nominal_send()
    {
        var range = ProductionCalculations.SendRange(1000m, 5m, 0m);

        Assert.Equal(new AcceptedRange(995m, 995m, 995m), range);
        Assert.True(ProductionCalculations.IsWithin(995m, range));
        Assert.False(ProductionCalculations.IsWithin(994.999m, range));
        Assert.False(ProductionCalculations.IsWithin(995.001m, range));
    }

    /// <summary>
    /// Architecture 4.1 item 4 and PRD 4.1: the limits are rounded to 3 places half away from zero, then compared.
    /// Planned 1.025: 1.025 × 0.98 = 1.0045 rounds to 1.005 (banker's rounding would give 1.004), and
    /// 1.025 × 1.02 = 1.0455 rounds to 1.046. So 1.046 is accepted, because it is the limit the API shows,
    /// even though it is above the unrounded 1.0455.
    /// </summary>
    [Fact]
    public void Range_limits_are_rounded_half_away_from_zero_and_compared_after_rounding()
    {
        var range = ProductionCalculations.SendRange(1.025m, 0m, DefaultTolerance);

        Assert.Equal(new AcceptedRange(1.005m, 1.046m, 1.025m), range);
        Assert.True(ProductionCalculations.IsWithin(1.046m, range));
        Assert.False(ProductionCalculations.IsWithin(1.004m, range));
    }

    /// <summary>Design 5.7 (AC-13.2 estimate): Potato starch planned 15 kg, no leftover: range 14.7 to 15.3, nominal 15.</summary>
    [Fact]
    public void Design_example_potato_starch_range_is_14_7_to_15_3()
    {
        Assert.Equal(new AcceptedRange(14.7m, 15.3m, 15m), ProductionCalculations.SendRange(15m, 0m, DefaultTolerance));
    }

    // ---- Complementary send range (AC-18.1, AC-18.2) -----------------------------------------------------

    /// <summary>AC-18.2: requested 20 kg, t = 2: range 19.6 to 20.4 kg, no leftover netted.</summary>
    [Fact]
    public void AC_18_2_complementary_range_is_19_6_to_20_4()
    {
        Assert.Equal(new AcceptedRange(19.6m, 20.4m, 20m), ProductionCalculations.ComplementaryRange(20m, DefaultTolerance));
    }

    /// <summary>AC-18.2: sends of 19.6, 20 and 20.4 kg are accepted; 19.599 and 20.401 kg are rejected.</summary>
    [Theory]
    [InlineData("19.6", true)]
    [InlineData("20", true)]
    [InlineData("20.4", true)]
    [InlineData("19.599", false)]
    [InlineData("20.401", false)]
    public void AC_18_2_complementary_limits_are_included(string send, bool accepted)
    {
        var range = ProductionCalculations.ComplementaryRange(20m, DefaultTolerance);

        Assert.Equal(accepted, ProductionCalculations.IsWithin(D(send), range));
    }

    /// <summary>AC-18.1 and architecture 4.1 item 4: complementary limits are rounded too (1.025 × 0.98 = 1.0045 → 1.005).</summary>
    [Fact]
    public void AC_18_1_complementary_limits_are_rounded_half_away_from_zero()
    {
        Assert.Equal(new AcceptedRange(1.005m, 1.046m, 1.025m), ProductionCalculations.ComplementaryRange(1.025m, DefaultTolerance));
    }
}
