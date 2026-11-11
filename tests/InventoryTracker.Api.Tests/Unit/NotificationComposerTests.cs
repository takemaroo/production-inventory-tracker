using System.Globalization;
using System.Text.Json;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.Notifications;
using static InventoryTracker.Api.Tests.Unit.Calculations.CalculationTestData;

namespace InventoryTracker.Api.Tests.Unit;

/// <summary>
/// T-005: the five notification types (AC-23.2). Each test checks the exact message of design 7.3 and the
/// <c>data</c> object of design 5.10, using the design's example values. No database: the composer is pure.
/// </summary>
public class NotificationComposerTests
{
    private static readonly DateTime CompletedAt = At(15, 40);

    private static readonly NotificationItem CheeseItem = new(Cheese, "Cheese", "kg");

    private static readonly NotificationItem BagOfChips = new(7, "Potato chips 150 g", "Bag");

    // ---- UsageVariance (AC-20.4) -------------------------------------------------------------------------

    /// <summary>
    /// AC-23.2, AC-20.4, design 7.3 example: worked example B (AC-20.6) run through the real D-9 calculation
    /// gives "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%)."
    /// </summary>
    [Fact]
    public void UsageVariance_reproduces_the_design_example()
    {
        var completion = ExampleBLine();

        var notification = NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt);

        Assert.Equal(NotificationType.UsageVariance, notification.Type);
        Assert.Equal(12, notification.ProductionOrderNumber);
        Assert.Null(notification.ComplementaryOrderNumber);
        Assert.Equal(CompletedAt, notification.CreatedAt);
        Assert.Equal(
            "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%).",
            notification.Message);
    }

    /// <summary>
    /// AC-23.2 and design 5.10: the UsageVariance data has exactly itemId, itemName, unit,
    /// requiredForActualOutput, actualUsage, difference and percent, with the design example's values. The
    /// percentage is rounded to 2 places for display (2.8971...% becomes 2.90).
    /// </summary>
    [Fact]
    public void UsageVariance_data_matches_design_5_10()
    {
        var notification = NotificationComposer.UsageVariance(12, CheeseItem, ExampleBLine(), CompletedAt);

        var data = Data(notification);
        AssertFieldNames(data, "itemId", "itemName", "unit", "requiredForActualOutput", "actualUsage", "difference", "percent");
        Assert.Equal(Cheese, data.GetProperty("itemId").GetInt32());
        Assert.Equal("Cheese", data.GetProperty("itemName").GetString());
        Assert.Equal("kg", data.GetProperty("unit").GetString());
        Assert.Equal(1001m, data.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(1030m, data.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(29m, data.GetProperty("difference").GetDecimal());
        Assert.Equal(2.9m, data.GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// AC-20.7 and decision #12: under-use notifies too. Worked example C, used 975 kg against 1,000 required,
    /// reads "Difference -25 kg (-2.50%)" with the ASCII minus (design 6.1).
    /// </summary>
    [Fact]
    public void UsageVariance_under_use_shows_a_minus_sign()
    {
        var line = SentLine(amountPerUnit: 1m, leftoverApplied: 0m, warehouseSend: 1000m, complementarySends: 0m, carriedLeftover: 0m);
        var completion = ProductionCalculations.CompleteLine(line, producedQuantity: 1000m, leftoverEntered: 25m, DefaultTolerance);

        var notification = NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt);

        Assert.Equal(
            "Order #12, Cheese: used 975 kg, required 1,000 kg for actual output. Difference -25 kg (-2.50%).",
            notification.Message);
        Assert.Equal(-25m, Data(notification).GetProperty("difference").GetDecimal());
        Assert.Equal(-2.5m, Data(notification).GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// AC-20.4: a line at 2.0001% is above a 2% tolerance and notifies, and the notification shows it as 2.00%:
    /// "+2.00%" in the message and 2.00 in the data.
    /// </summary>
    [Fact]
    public void UsageVariance_at_2_0001_percent_is_shown_as_2_00()
    {
        var line = SentLine(amountPerUnit: 1m, leftoverApplied: 0m, warehouseSend: 1000m, complementarySends: 20.001m, carriedLeftover: 0m);
        var completion = ProductionCalculations.CompleteLine(line, producedQuantity: 1000m, leftoverEntered: 0m, DefaultTolerance);
        Assert.True(completion.AboveTolerance);

        var notification = NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt);

        Assert.Equal(
            "Order #12, Cheese: used 1,020.001 kg, required 1,000 kg for actual output. Difference +20.001 kg (+2.00%).",
            notification.Message);
        Assert.Equal(2m, Data(notification).GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// Decision #58: the shown percentage comes from the stored 6-place value. 2.0049996% is stored as 2.005000
    /// (architecture 3.1), and the order response shows that as 2.01, so the notification must show +2.01% and
    /// 2.01 too, not the 2.00 a direct rounding would give. The line figures are built by hand so the percentage
    /// can be exactly 2.0049996.
    /// </summary>
    [Fact]
    public void UsageVariance_percentage_is_rounded_from_the_stored_six_place_value()
    {
        // Only the percentage matters here. The composer does not recompute it, so the other figures only feed the
        // message text and do not have to add up to it (20.05 / 1,000 is exactly 2.005%, not 2.0049996%).
        var completion = new LineCompletion(
            RequiredForActualOutput: 1000m, LeftoverRemaining: 0m, ActualUsage: 1020.05m, UsageVariance: 20.05m,
            UsageVariancePercent: 2.0049996m, AboveTolerance: true);
        // The trap this test guards against: rounding straight to 2 places gives a different answer.
        Assert.Equal(2.00m, Math.Round(2.0049996m, 2, MidpointRounding.AwayFromZero));

        var notification = NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt);

        Assert.Equal(
            "Order #12, Cheese: used 1,020.05 kg, required 1,000 kg for actual output. Difference +20.05 kg (+2.01%).",
            notification.Message);
        Assert.Equal(2.01m, Data(notification).GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// Decision #58 for output: ordered 200,001 bags, produced 204,011.02 gives 2.004999975...%, stored as
    /// 2.005000, so it is shown as +2.01% in the message and 2.01 in the data.
    /// </summary>
    [Fact]
    public void OutputVariance_percentage_is_rounded_from_the_stored_six_place_value()
    {
        var variance = ProductionCalculations.OutputVariance(200001m, 204011.02m);
        Assert.Equal(2.005m, Math.Round(variance.Percent, 6, MidpointRounding.AwayFromZero));
        Assert.Equal(2.00m, Math.Round(variance.Percent, 2, MidpointRounding.AwayFromZero));

        var notification = NotificationComposer.OutputVariance(12, BagOfChips, 200001m, 204011.02m, CompletedAt);

        Assert.Equal(
            "Order #12: produced 204,011.02 Bag, ordered 200,001 Bag. Difference +4,010.02 Bag (+2.01%).",
            notification.Message);
        Assert.Equal(2.01m, Data(notification).GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// Decisions #49 and #57: when the required amount rounds to 0 kg (0.0004 kg × 1 bag), the percentage is
    /// null. The message ends "(percentage not applicable)." and the data still has a "percent" field, set to null.
    /// </summary>
    [Fact]
    public void UsageVariance_without_a_percentage_says_not_applicable()
    {
        var line = SentLine(amountPerUnit: 0.0004m, leftoverApplied: 0m, warehouseSend: 0.001m, complementarySends: 0m, carriedLeftover: 0m);
        var completion = ProductionCalculations.CompleteLine(line, producedQuantity: 1m, leftoverEntered: 0m, DefaultTolerance);
        Assert.Null(completion.UsageVariancePercent);

        var notification = NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt);

        Assert.Equal(
            "Order #12, Cheese: used 0.001 kg, required 0 kg for actual output. Difference +0.001 kg (percentage not applicable).",
            notification.Message);
        var data = Data(notification);
        AssertFieldNames(data, "itemId", "itemName", "unit", "requiredForActualOutput", "actualUsage", "difference", "percent");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("percent").ValueKind);
        Assert.Equal(0m, data.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(0.001m, data.GetProperty("difference").GetDecimal());
    }

    /// <summary>
    /// AC-20.4 and AC-20.5: a line within tolerance (worked example A, +1.50%) must not get a UsageVariance
    /// notification, so composing one is refused rather than produced.
    /// </summary>
    [Fact]
    public void UsageVariance_within_tolerance_is_refused()
    {
        var line = SentLine(1m, 5m, 995m, 20m, 0m);
        var completion = ProductionCalculations.CompleteLine(line, 1001m, 4m, DefaultTolerance);
        Assert.False(completion.AboveTolerance);

        Assert.Throws<ArgumentException>(() => NotificationComposer.UsageVariance(12, CheeseItem, completion, CompletedAt));
    }

    // ---- OutputVariance (AC-20.2) ------------------------------------------------------------------------

    /// <summary>
    /// AC-23.2, AC-20.2, design 7.3 example: ordered 1,000 bags, produced 1,001 gives
    /// "Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%)."
    /// </summary>
    [Fact]
    public void OutputVariance_reproduces_the_design_example()
    {
        var notification = NotificationComposer.OutputVariance(12, BagOfChips, orderedQuantity: 1000m, producedQuantity: 1001m, CompletedAt);

        Assert.Equal(NotificationType.OutputVariance, notification.Type);
        Assert.Equal(12, notification.ProductionOrderNumber);
        Assert.Null(notification.ComplementaryOrderNumber);
        Assert.Equal(CompletedAt, notification.CreatedAt);
        Assert.Equal("Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).", notification.Message);
    }

    /// <summary>
    /// AC-20.2 and design 5.10: the OutputVariance data has exactly finishedProductId, finishedProductName, unit,
    /// ordered, produced, difference and percent.
    /// </summary>
    [Fact]
    public void OutputVariance_data_matches_design_5_10()
    {
        var notification = NotificationComposer.OutputVariance(12, BagOfChips, 1000m, 1001m, CompletedAt);

        var data = Data(notification);
        AssertFieldNames(data, "finishedProductId", "finishedProductName", "unit", "ordered", "produced", "difference", "percent");
        Assert.Equal(7, data.GetProperty("finishedProductId").GetInt32());
        Assert.Equal("Potato chips 150 g", data.GetProperty("finishedProductName").GetString());
        Assert.Equal("Bag", data.GetProperty("unit").GetString());
        Assert.Equal(1000m, data.GetProperty("ordered").GetDecimal());
        Assert.Equal(1001m, data.GetProperty("produced").GetDecimal());
        Assert.Equal(1m, data.GetProperty("difference").GetDecimal());
        Assert.Equal(0.1m, data.GetProperty("percent").GetDecimal());
    }

    /// <summary>AC-20.2: producing less than ordered also notifies, with a signed difference: -10 Bag (-1.00%).</summary>
    [Fact]
    public void OutputVariance_under_production_shows_a_minus_sign()
    {
        var notification = NotificationComposer.OutputVariance(12, BagOfChips, 1000m, 990m, CompletedAt);

        Assert.Equal("Order #12: produced 990 Bag, ordered 1,000 Bag. Difference -10 Bag (-1.00%).", notification.Message);
        Assert.Equal(-10m, Data(notification).GetProperty("difference").GetDecimal());
        Assert.Equal(-1m, Data(notification).GetProperty("percent").GetDecimal());
    }

    /// <summary>AC-20.2 and AC-20.7: when produced equals ordered there is no OutputVariance notification, so composing one is refused.</summary>
    [Fact]
    public void OutputVariance_with_no_difference_is_refused()
    {
        Assert.Throws<ArgumentException>(() => NotificationComposer.OutputVariance(12, BagOfChips, 1000m, 1000m, CompletedAt));
    }

    // ---- LeftoverAtCompletion (AC-19.7) ------------------------------------------------------------------

    /// <summary>
    /// AC-23.2, AC-19.7, design 7.3 example (worked example A): 4 kg of Cheese left, reason "Last bag line jammed".
    /// </summary>
    [Fact]
    public void LeftoverAtCompletion_reproduces_the_design_example()
    {
        var notification = NotificationComposer.LeftoverAtCompletion(
            12, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 4m)], "Last bag line jammed", CompletedAt);

        Assert.Equal(NotificationType.LeftoverAtCompletion, notification.Type);
        Assert.Equal(12, notification.ProductionOrderNumber);
        Assert.Null(notification.ComplementaryOrderNumber);
        Assert.Equal(CompletedAt, notification.CreatedAt);
        Assert.Equal(
            "Order #12 was completed with leftover in production storage: Cheese 4 kg. Reason: \"Last bag line jammed\"",
            notification.Message);
    }

    /// <summary>
    /// AC-19.7 and design 5.10: the data has exactly materials and reason; each material has exactly itemId,
    /// itemName, unit and leftover. A line with 0 leftover is not listed (only leftover above zero), and neither
    /// is one of 0.0004 kg, which rounds to 0 at 3 places (decision #59).
    /// </summary>
    [Fact]
    public void LeftoverAtCompletion_lists_only_materials_above_zero()
    {
        NotificationMaterial[] leftovers =
        [
            new(1, Cheese, "Cheese", "kg", 4m),
            new(2, PotatoStarch, "Potato starch", "kg", 0m),
            new(3, 5, "Salt", "kg", 0.0004m),
        ];

        var notification = NotificationComposer.LeftoverAtCompletion(12, leftovers, "Last bag line jammed", CompletedAt);

        Assert.Equal(
            "Order #12 was completed with leftover in production storage: Cheese 4 kg. Reason: \"Last bag line jammed\"",
            notification.Message);
        var data = Data(notification);
        AssertFieldNames(data, "materials", "reason");
        Assert.Equal("Last bag line jammed", data.GetProperty("reason").GetString());
        var material = Assert.Single(data.GetProperty("materials").EnumerateArray().ToList());
        AssertFieldNames(material, "itemId", "itemName", "unit", "leftover");
        Assert.Equal(Cheese, material.GetProperty("itemId").GetInt32());
        Assert.Equal("Cheese", material.GetProperty("itemName").GetString());
        Assert.Equal("kg", material.GetProperty("unit").GetString());
        Assert.Equal(4m, material.GetProperty("leftover").GetDecimal());
    }

    /// <summary>AC-19.7: with no leftover above zero there is no LeftoverAtCompletion notification, so composing one is refused, naming the "leftovers" argument.</summary>
    [Fact]
    public void LeftoverAtCompletion_with_no_leftover_is_refused()
    {
        Assert.Throws<ArgumentException>("leftovers", () => NotificationComposer.LeftoverAtCompletion(
            12, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 0m)], "reason", CompletedAt));
    }

    // ---- ComplementaryAwaitingApproval (AC-16.1) ---------------------------------------------------------

    /// <summary>
    /// AC-23.2, AC-16.1, design 7.3 example: complementary order #3 for order #12, requested by
    /// lucia.mendez@example.com, Cheese 20 kg, reason "Hopper spill at start of run". The complementary order
    /// number is on the notification itself.
    /// </summary>
    [Fact]
    public void ComplementaryAwaitingApproval_reproduces_the_design_example()
    {
        var notification = NotificationComposer.ComplementaryAwaitingApproval(
            12, 3, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 20m)], "Hopper spill at start of run",
            new NotificationUser("5a7e-lucia", "lucia.mendez@example.com"), CompletedAt);

        Assert.Equal(NotificationType.ComplementaryAwaitingApproval, notification.Type);
        Assert.Equal(12, notification.ProductionOrderNumber);
        Assert.Equal(3, notification.ComplementaryOrderNumber);
        Assert.Equal(CompletedAt, notification.CreatedAt);
        Assert.Equal(
            "Complementary order #3 for order #12 is waiting for approval. lucia.mendez@example.com requested Cheese 20 kg. " +
            "Reason: \"Hopper spill at start of run\"",
            notification.Message);
    }

    /// <summary>
    /// AC-16.1 and design 5.10: the data has exactly materials, reason and requestedBy; each material has exactly
    /// itemId, itemName, unit and requestedAmount; requestedBy has exactly id and email.
    /// </summary>
    [Fact]
    public void ComplementaryAwaitingApproval_data_matches_design_5_10()
    {
        var notification = NotificationComposer.ComplementaryAwaitingApproval(
            12, 3, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 20m)], "Hopper spill at start of run",
            new NotificationUser("5a7e-lucia", "lucia.mendez@example.com"), CompletedAt);

        var data = Data(notification);
        AssertFieldNames(data, "materials", "reason", "requestedBy");
        Assert.Equal("Hopper spill at start of run", data.GetProperty("reason").GetString());
        var material = Assert.Single(data.GetProperty("materials").EnumerateArray().ToList());
        AssertFieldNames(material, "itemId", "itemName", "unit", "requestedAmount");
        Assert.Equal(Cheese, material.GetProperty("itemId").GetInt32());
        Assert.Equal(20m, material.GetProperty("requestedAmount").GetDecimal());
        var requestedBy = data.GetProperty("requestedBy");
        AssertFieldNames(requestedBy, "id", "email");
        Assert.Equal("5a7e-lucia", requestedBy.GetProperty("id").GetString());
        Assert.Equal("lucia.mendez@example.com", requestedBy.GetProperty("email").GetString());
    }

    /// <summary>AC-16.1 requires one or more lines, so a request with no material is refused, naming the "requested" argument.</summary>
    [Fact]
    public void ComplementaryAwaitingApproval_with_no_material_is_refused()
    {
        Assert.Throws<ArgumentException>("requested", () => NotificationComposer.ComplementaryAwaitingApproval(
            12, 3, [], "reason", new NotificationUser("id", "a@example.com"), CompletedAt));
    }

    // ---- OrderCancelledWithMaterial (AC-21.3) ------------------------------------------------------------

    /// <summary>
    /// AC-23.2, AC-21.3, design 7.3 example: order #9 cancelled with Cheese 50 kg and Potato starch 0.75 kg in
    /// production storage, reason "Customer order withdrawn". The materials are passed in reverse order to prove
    /// the list follows recipe line order (design 7.3), not the order of the input.
    /// </summary>
    [Fact]
    public void OrderCancelledWithMaterial_reproduces_the_design_example_in_recipe_line_order()
    {
        NotificationMaterial[] stock =
        [
            new(2, PotatoStarch, "Potato starch", "kg", 0.75m),
            new(1, Cheese, "Cheese", "kg", 50m),
        ];

        var notification = NotificationComposer.OrderCancelledWithMaterial(9, stock, "Customer order withdrawn", CompletedAt);

        Assert.Equal(NotificationType.OrderCancelledWithMaterial, notification.Type);
        Assert.Equal(9, notification.ProductionOrderNumber);
        Assert.Null(notification.ComplementaryOrderNumber);
        Assert.Equal(CompletedAt, notification.CreatedAt);
        Assert.Equal(
            "Order #9 was cancelled with material still in production storage: Cheese 50 kg, Potato starch 0.75 kg. " +
            "Reason: \"Customer order withdrawn\"",
            notification.Message);

        var data = Data(notification);
        AssertFieldNames(data, "materials", "reason");
        Assert.Equal("Customer order withdrawn", data.GetProperty("reason").GetString());
        var materials = data.GetProperty("materials").EnumerateArray().ToList();
        Assert.Equal(2, materials.Count);
        AssertFieldNames(materials[0], "itemId", "itemName", "unit", "productionStorageQuantity");
        Assert.Equal(Cheese, materials[0].GetProperty("itemId").GetInt32());
        Assert.Equal(50m, materials[0].GetProperty("productionStorageQuantity").GetDecimal());
        Assert.Equal(PotatoStarch, materials[1].GetProperty("itemId").GetInt32());
        Assert.Equal("Potato starch", materials[1].GetProperty("itemName").GetString());
        Assert.Equal(0.75m, materials[1].GetProperty("productionStorageQuantity").GetDecimal());
    }

    /// <summary>
    /// AC-21.3: only materials with production storage stock above zero are listed, and when none is above zero
    /// there is no notification, so composing one is refused, naming the "productionStorage" argument.
    /// </summary>
    [Fact]
    public void OrderCancelledWithMaterial_lists_only_stock_above_zero()
    {
        NotificationMaterial[] stock =
        [
            new(1, Cheese, "Cheese", "kg", 0m),
            new(2, PotatoStarch, "Potato starch", "kg", 0.75m),
        ];

        var notification = NotificationComposer.OrderCancelledWithMaterial(9, stock, "Customer order withdrawn", CompletedAt);

        Assert.Equal(
            "Order #9 was cancelled with material still in production storage: Potato starch 0.75 kg. Reason: \"Customer order withdrawn\"",
            notification.Message);
        Assert.Single(Data(notification).GetProperty("materials").EnumerateArray());
        Assert.Throws<ArgumentException>("productionStorage", () => NotificationComposer.OrderCancelledWithMaterial(
            9, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 0m)], "reason", CompletedAt));
    }

    // ---- Shared rules ------------------------------------------------------------------------------------

    /// <summary>
    /// Design 7.3: the reason is quoted as entered, even with quotes and accented letters, and the data gives back
    /// exactly the same text after the JSON round trip.
    /// </summary>
    [Fact]
    public void Reason_is_kept_exactly_as_entered()
    {
        const string reason = "Bolsa \"rota\" en la línea 2 <B>";

        var notification = NotificationComposer.OrderCancelledWithMaterial(
            9, [new NotificationMaterial(1, Cheese, "Cheese", "kg", 50m)], reason, CompletedAt);

        Assert.EndsWith($"Reason: \"{reason}\"", notification.Message);
        Assert.Equal(reason, Data(notification).GetProperty("reason").GetString());
    }

    /// <summary>Design 6.1: message numbers use the invariant culture, so a Spanish-language server still writes "1,030" and "+2.90%".</summary>
    [Fact]
    public void Messages_ignore_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");

            var notification = NotificationComposer.UsageVariance(12, CheeseItem, ExampleBLine(), CompletedAt);

            Assert.Equal(
                "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%).",
                notification.Message);
            Assert.Contains("\"percent\":2.90", notification.DataJson);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// Worked example B (AC-20.6): applied 5, send 995, complementary 30, carried 0; produced 1,001 bags with
    /// 0 kg leftover. Usage 1,030 kg against 1,001 required: +29 kg, above 2%.
    /// </summary>
    private static LineCompletion ExampleBLine()
    {
        var line = SentLine(amountPerUnit: 1m, leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 30m, carriedLeftover: 0m);
        return ProductionCalculations.CompleteLine(line, producedQuantity: 1001m, leftoverEntered: 0m, DefaultTolerance);
    }

    /// <summary>Parses the stored <c>DataJson</c>, the way the notifications API will read it.</summary>
    private static JsonElement Data(Notification notification)
        => JsonDocument.Parse(notification.DataJson).RootElement;

    /// <summary>Design 5.10: the object has exactly these field names, in this order, and no others.</summary>
    private static void AssertFieldNames(JsonElement element, params string[] expected)
        => Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name).ToArray());
}
