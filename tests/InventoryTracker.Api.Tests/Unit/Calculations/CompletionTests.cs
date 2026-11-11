using System.Globalization;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using static InventoryTracker.Api.Tests.Unit.Calculations.CalculationTestData;

namespace InventoryTracker.Api.Tests.Unit.Calculations;

/// <summary>
/// D-9 at completion: usage variance per line (AC-20.3, AC-20.4) and output variance (AC-20.1, AC-20.2), with
/// worked examples A to D (AC-20.5 to AC-20.8).
/// </summary>
public class CompletionTests
{
    // ---- Worked example A (AC-20.5) ----------------------------------------------------------------------

    /// <summary>
    /// AC-20.5 (worked example A, within tolerance): applied 5, send 995, complementary 20, carried 0; produced
    /// 1,001 bags, leftover entered 4. Required 1,001; remaining 4; usage 5 + 995 + 20 − 4 = 1,016; variance +15;
    /// 1.4985...% shown as +1.50%; not above 2%, so no UsageVariance notification.
    /// </summary>
    [Fact]
    public void AC_20_5_example_A_is_within_tolerance()
    {
        var line = SentLine(amountPerUnit: 1m, leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 20m, carriedLeftover: 0m);

        var result = ProductionCalculations.CompleteLine(line, producedQuantity: 1001m, leftoverEntered: 4m, DefaultTolerance);

        Assert.Equal(1001m, result.RequiredForActualOutput);
        Assert.Equal(4m, result.LeftoverRemaining);
        Assert.Equal(1016m, result.ActualUsage);
        Assert.Equal(15m, result.UsageVariance);
        Assert.StartsWith("1.4985", result.UsageVariancePercent!.Value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("+1.50%", MessageFormat.SignedPercent(result.UsageVariancePercent!.Value));
        Assert.False(result.AboveTolerance);
    }

    /// <summary>
    /// AC-20.5 and AC-19.2: the ConsumedInProduction quantity (storage before completion − leftover entered,
    /// 1,020 − 4) equals ActualUsage, 1,016 kg, so production storage after completion is 4 kg.
    /// </summary>
    [Fact]
    public void AC_20_5_example_A_actual_usage_equals_the_consumption_movement()
    {
        const decimal carriedLeftover = 0m;
        var line = SentLine(1m, 5m, 995m, 20m, carriedLeftover);
        // Production storage just before completion holds everything committed to the order plus any carried leftover.
        var storageBeforeCompletion = ProductionCalculations.Committed(5m, 995m, 20m) + carriedLeftover;

        var result = ProductionCalculations.CompleteLine(line, 1001m, 4m, DefaultTolerance);

        Assert.Equal(1020m, storageBeforeCompletion);
        Assert.Equal(storageBeforeCompletion - 4m, result.ActualUsage);
        Assert.Equal(4m, storageBeforeCompletion - result.ActualUsage);
    }

    /// <summary>AC-20.5: output variance +1 bag, +0.10%, which raises one OutputVariance notification.</summary>
    [Fact]
    public void AC_20_5_example_A_output_variance_is_plus_1_bag()
    {
        var result = ProductionCalculations.OutputVariance(orderedQuantity: 1000m, producedQuantity: 1001m);

        Assert.Equal(1m, result.Variance);
        Assert.Equal(0.1m, result.Percent);
        Assert.Equal("+0.10%", MessageFormat.SignedPercent(result.Percent));
        Assert.True(result.RaisesNotification);
    }

    // ---- Worked example B (AC-20.6) ----------------------------------------------------------------------

    /// <summary>
    /// AC-20.6 (worked example B, above tolerance): as A but complementary 30 and leftover entered 0. Usage
    /// 5 + 995 + 30 − 0 = 1,030; variance +29; +2.90%; above 2%, so one UsageVariance notification. Output +1 bag.
    /// </summary>
    [Fact]
    public void AC_20_6_example_B_is_above_tolerance()
    {
        var line = SentLine(1m, 5m, 995m, 30m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 1001m, 0m, DefaultTolerance);
        var output = ProductionCalculations.OutputVariance(1000m, 1001m);

        Assert.Equal(1001m, result.RequiredForActualOutput);
        Assert.Equal(0m, result.LeftoverRemaining);
        Assert.Equal(1030m, result.ActualUsage);
        Assert.Equal(29m, result.UsageVariance);
        Assert.Equal("+2.90%", MessageFormat.SignedPercent(result.UsageVariancePercent!.Value));
        Assert.True(result.AboveTolerance);
        Assert.Equal(1m, output.Variance);
        Assert.True(output.RaisesNotification);
    }

    // ---- Worked example C (AC-20.7) ----------------------------------------------------------------------

    /// <summary>
    /// AC-20.7 (worked example C, under-use): applied 0, send 1,000, no complementary, produced 1,000, leftover
    /// entered 25. Usage 975; variance −25; −2.50%; under-use above 2% also notifies (decision #12).
    /// </summary>
    [Fact]
    public void AC_20_7_example_C_under_use_is_above_tolerance()
    {
        var line = SentLine(1m, 0m, 1000m, 0m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 1000m, 25m, DefaultTolerance);

        Assert.Equal(1000m, result.RequiredForActualOutput);
        Assert.Equal(25m, result.LeftoverRemaining);
        Assert.Equal(975m, result.ActualUsage);
        Assert.Equal(-25m, result.UsageVariance);
        Assert.Equal(-2.5m, result.UsageVariancePercent);
        Assert.Equal("-2.50%", MessageFormat.SignedPercent(result.UsageVariancePercent!.Value));
        Assert.True(result.AboveTolerance);
    }

    /// <summary>AC-20.7 and AC-20.1: output variance 0 is recorded (0, 0%) and raises no OutputVariance notification.</summary>
    [Fact]
    public void AC_20_7_example_C_zero_output_variance_raises_no_notification()
    {
        var result = ProductionCalculations.OutputVariance(1000m, 1000m);

        Assert.Equal(0m, result.Variance);
        Assert.Equal(0m, result.Percent);
        Assert.False(result.RaisesNotification);
    }

    // ---- Worked example D (AC-20.8) ----------------------------------------------------------------------

    /// <summary>
    /// AC-20.8 (worked example D, carried leftover): #8 applied 20, send 980, carried 50 − 20 = 30; storage before
    /// completion 1,030; produced 1,000; leftover entered 30. Remaining 30 − 30 = 0; usage 20 + 980 − 0 = 1,000;
    /// variance 0. ConsumedInProduction 1,030 − 30 = 1,000, leaving 30 kg in storage.
    /// </summary>
    [Fact]
    public void AC_20_8_example_D_carried_leftover_is_not_counted_as_usage()
    {
        var carried = ProductionCalculations.CarriedLeftover(productionStorageBeforeSend: 50m, leftoverApplied: 20m);
        var line = SentLine(1m, 20m, 980m, 0m, carried);
        // Production storage just before completion = material committed to #8 + the carried leftover beside it.
        var storageBeforeCompletion = ProductionCalculations.Committed(20m, 980m, 0m) + carried;

        var result = ProductionCalculations.CompleteLine(line, 1000m, 30m, DefaultTolerance);

        Assert.Equal(30m, carried);
        Assert.Equal(1030m, storageBeforeCompletion);
        Assert.Equal(1000m, result.RequiredForActualOutput);
        Assert.Equal(0m, result.LeftoverRemaining);
        Assert.Equal(1000m, result.ActualUsage);
        Assert.Equal(0m, result.UsageVariance);
        Assert.Equal(0m, result.UsageVariancePercent);
        Assert.False(result.AboveTolerance);
        Assert.Equal(storageBeforeCompletion - 30m, result.ActualUsage); // equals the ConsumedInProduction quantity
        Assert.Equal(30m, storageBeforeCompletion - result.ActualUsage);
    }

    /// <summary>
    /// D-9 and AC-19.2 (decision #48): LeftoverRemaining is not clamped at zero. Example D with 25 kg entered
    /// instead of 30 means the run also used 5 kg of the carried leftover: remaining 25 − 30 = −5, usage
    /// 20 + 980 − (−5) = 1,005 kg, which equals the ConsumedInProduction quantity 1,030 − 25. A clamp at 0 would
    /// give 1,000 kg and hide 5 kg of real usage.
    /// </summary>
    [Fact]
    public void D_9_negative_leftover_remaining_counts_used_carried_leftover_as_usage()
    {
        var carried = ProductionCalculations.CarriedLeftover(productionStorageBeforeSend: 50m, leftoverApplied: 20m);
        var line = SentLine(1m, 20m, 980m, 0m, carried);
        var storageBeforeCompletion = ProductionCalculations.Committed(20m, 980m, 0m) + carried;
        const decimal leftoverEntered = 25m;

        var result = ProductionCalculations.CompleteLine(line, 1000m, leftoverEntered, DefaultTolerance);

        Assert.Equal(1030m, storageBeforeCompletion);
        Assert.Equal(-5m, result.LeftoverRemaining);
        Assert.Equal(1005m, result.ActualUsage);
        Assert.Equal(storageBeforeCompletion - leftoverEntered, result.ActualUsage); // the ConsumedInProduction quantity
        Assert.Equal(5m, result.UsageVariance);
    }

    /// <summary>
    /// PRD section 6 row 1: entering the physical total matters. In the AC-15.4 case (50 kg in storage, 30 kg
    /// applied, carried 20, zero send) a run that used all 30 kg leaves 20 kg; usage is 30 kg, not 10 kg.
    /// </summary>
    [Fact]
    public void PRD_row_1_physical_total_with_carried_leftover_gives_the_real_usage()
    {
        var line = SentLine(1m, leftoverApplied: 30m, warehouseSend: 0m, complementarySends: 0m, carriedLeftover: 20m);

        var result = ProductionCalculations.CompleteLine(line, producedQuantity: 30m, leftoverEntered: 20m, DefaultTolerance);

        Assert.Equal(0m, result.LeftoverRemaining);
        Assert.Equal(30m, result.ActualUsage);
        Assert.Equal(0m, result.UsageVariance);
    }

    // ---- AC-20.4 threshold boundaries --------------------------------------------------------------------

    /// <summary>
    /// AC-20.4 and PRD section 6 row 5: a line at exactly +2.00% (used 1,020, required 1,000) is not above a 2%
    /// tolerance and creates no notification.
    /// </summary>
    [Fact]
    public void AC_20_4_exactly_2_percent_over_is_not_above_tolerance()
    {
        var line = SentLine(1m, 0m, 1020m, 0m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 1000m, 0m, DefaultTolerance);

        Assert.Equal(2m, result.UsageVariancePercent);
        Assert.False(result.AboveTolerance);
    }

    /// <summary>AC-20.4 and decision #12: exactly −2.00% (used 980, required 1,000) is not above tolerance either.</summary>
    [Fact]
    public void AC_20_4_exactly_2_percent_under_is_not_above_tolerance()
    {
        var line = SentLine(1m, 0m, 1000m, 0m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 1000m, 20m, DefaultTolerance);

        Assert.Equal(-2m, result.UsageVariancePercent);
        Assert.False(result.AboveTolerance);
    }

    /// <summary>
    /// AC-20.4: a line at 2.0001% (used 102,000.1, required 100,000) is above tolerance and notifies, although it is
    /// shown as +2.00%. The decision uses the unrounded value.
    /// </summary>
    [Fact]
    public void AC_20_4_2_point_0001_percent_is_above_tolerance_and_shown_as_2_00()
    {
        var line = SentLine(1m, 0m, 102000.1m, 0m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 100000m, 0m, DefaultTolerance);

        Assert.Equal(2000.1m, result.UsageVariance);
        Assert.Equal(2.0001m, result.UsageVariancePercent);
        Assert.Equal("+2.00%", MessageFormat.SignedPercent(result.UsageVariancePercent!.Value));
        Assert.True(result.AboveTolerance);
    }

    /// <summary>AC-20.4 and decision #12: −2.0001% (used 97,999.9, required 100,000) is above tolerance too.</summary>
    [Fact]
    public void AC_20_4_minus_2_point_0001_percent_is_above_tolerance()
    {
        var line = SentLine(1m, 0m, 100000m, 0m, 0m);

        var result = ProductionCalculations.CompleteLine(line, 100000m, 2000.1m, DefaultTolerance);

        Assert.Equal(-2.0001m, result.UsageVariancePercent);
        Assert.True(result.AboveTolerance);
    }

    /// <summary>
    /// AC-20.10 (the tolerance in force at completion is the one used): the same +2.90% line of example B is
    /// above a 2% tolerance but not above a 3% tolerance.
    /// </summary>
    [Fact]
    public void AC_20_10_threshold_uses_the_tolerance_passed_in()
    {
        var line = SentLine(1m, 5m, 995m, 30m, 0m);

        Assert.True(ProductionCalculations.CompleteLine(line, 1001m, 0m, 2m).AboveTolerance);
        Assert.False(ProductionCalculations.CompleteLine(line, 1001m, 0m, 3m).AboveTolerance);
    }

    // ---- Rounding and recipe snapshot --------------------------------------------------------------------

    /// <summary>
    /// D-9, PRD 4.1 and AC-08.4: required uses the line's own amount per unit and is rounded to 3 places half
    /// away from zero: 0.00125 × 2 = 0.0025 → 0.003 (banker's rounding would give 0.002).
    /// </summary>
    [Fact]
    public void D_9_required_is_rounded_half_away_from_zero()
    {
        var line = SentLine(amountPerUnit: 0.00125m, leftoverApplied: 0m, warehouseSend: 0.003m, complementarySends: 0m, carriedLeftover: 0m);

        var result = ProductionCalculations.CompleteLine(line, 2m, 0m, DefaultTolerance);

        Assert.Equal(0.003m, result.RequiredForActualOutput);
        Assert.Equal(0m, result.UsageVariance);
    }

    /// <summary>
    /// AC-08.4: an order created at 1 kg per bag keeps that amount, so completing 1,000 bags requires 1,000 kg even
    /// if the recipe is later 1.01 kg per bag (the line, not the recipe, is the input).
    /// </summary>
    [Fact]
    public void AC_08_4_required_uses_the_amount_copied_onto_the_line()
    {
        var line = SentLine(1m, 0m, 1000m, 0m, 0m);

        Assert.Equal(1000m, ProductionCalculations.CompleteLine(line, 1000m, 0m, DefaultTolerance).RequiredForActualOutput);
    }

    // ---- Failure paths -----------------------------------------------------------------------------------

    /// <summary>D-9 needs the send figures: completing a line that was never sent is a programming error and throws.</summary>
    [Fact]
    public void Completing_an_unsent_line_throws()
    {
        var line = new ProductionOrderLine { RawMaterialId = Cheese, AmountPerUnit = 1m, PlannedAmount = 1000m };

        var error = Assert.Throws<InvalidOperationException>(() => ProductionCalculations.CompleteLine(line, 1000m, 0m, DefaultTolerance));
        Assert.Contains(nameof(ProductionOrderLine.LeftoverApplied), error.Message);
    }

    // ---- Required amount rounds to zero (decision #49) ---------------------------------------------------

    /// <summary>
    /// Decision #49: required rounds to 0 (0.0004 kg × 1 bag = 0.0004 → 0) and nothing was used. The percentage
    /// is null (it would divide by zero), and zero usage against zero required is not above tolerance.
    /// </summary>
    [Fact]
    public void Decision_49_required_rounds_to_zero_with_no_usage_has_null_percent_and_is_not_above_tolerance()
    {
        var line = SentLine(0.0004m, leftoverApplied: 0m, warehouseSend: 0m, complementarySends: 0m, carriedLeftover: 0m);

        var result = ProductionCalculations.CompleteLine(line, producedQuantity: 1m, leftoverEntered: 0m, DefaultTolerance);

        Assert.Equal(0m, result.RequiredForActualOutput);
        Assert.Equal(0m, result.ActualUsage);
        Assert.Equal(0m, result.UsageVariance);
        Assert.Null(result.UsageVariancePercent);
        Assert.False(result.AboveTolerance);
    }

    /// <summary>
    /// Decision #49: required rounds to 0 but 0.002 kg was used (0.003 sent, 0.001 left). Every other figure is
    /// calculated as usual. The percentage is null, and any usage against zero required is above tolerance, so it
    /// raises a UsageVariance notification.
    /// </summary>
    [Fact]
    public void Decision_49_required_rounds_to_zero_with_usage_has_null_percent_and_is_above_tolerance()
    {
        var line = SentLine(0.0004m, leftoverApplied: 0m, warehouseSend: 0.003m, complementarySends: 0m, carriedLeftover: 0m);

        var result = ProductionCalculations.CompleteLine(line, producedQuantity: 1m, leftoverEntered: 0.001m, DefaultTolerance);

        Assert.Equal(0m, result.RequiredForActualOutput);
        Assert.Equal(0.001m, result.LeftoverRemaining);
        Assert.Equal(0.002m, result.ActualUsage);
        Assert.Equal(0.002m, result.UsageVariance);
        Assert.Null(result.UsageVariancePercent);
        Assert.True(result.AboveTolerance);
    }

    /// <summary>
    /// Decision #49, with the largest tolerance allowed (100%, PRD section 5): any usage against zero required is
    /// still above tolerance, because no finite tolerance covers it.
    /// </summary>
    [Fact]
    public void Decision_49_usage_against_zero_required_is_above_even_a_100_percent_tolerance()
    {
        var line = SentLine(0.0004m, 0m, 0.001m, 0m, 0m);

        Assert.True(ProductionCalculations.CompleteLine(line, 1m, 0m, 100m).AboveTolerance);
    }

    /// <summary>CompleteLine is pure: the line it reads is not changed (the completion service stores the results).</summary>
    [Fact]
    public void CompleteLine_does_not_change_the_line()
    {
        var line = SentLine(1m, 5m, 995m, 20m, 0m);

        ProductionCalculations.CompleteLine(line, 1001m, 4m, DefaultTolerance);

        Assert.Null(line.ActualUsage);
        Assert.Null(line.LeftoverEntered);
        Assert.Null(line.UsageVarianceAboveTolerance);
    }
}
