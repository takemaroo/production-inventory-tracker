using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-011: completing an order (<c>POST /api/v1/production-orders/{number}/complete</c>; US-19, US-20, D-8, D-9, the first
/// three notification types of AC-23.2; design 5.7, 5.10, 7.2 and 7.3).
/// </summary>
/// <remarks>
/// <para>
/// Orders are created and sent through the API. Free leftover reaches production storage through a ledger Adjustment
/// (the adjustment endpoint is T-015). Complementary orders (T-017, T-018 are not built yet) are inserted through the
/// DbContext; a complementary <em>send</em> is written the way T-018 will write it: a SentToProduction movement through
/// the ledger, linked to the complementary order, and the line's ComplementarySends increased, in one write transaction.
/// Notifications are read through the DbContext (their API is T-012).
/// </para>
/// <para>
/// The database allows only one InProduction order (AC-15.10), so every test that sends an order uses its own factory.
/// Tests on the shared factory only use Created or Cancelled orders.
/// </para>
/// </remarks>
public class CompleteOrderTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The finished product of design 5.7's example.</summary>
    private const string ProductName = "Shredded cheese 1 kg bag";

    /// <summary>The reason of worked example A (AC-20.5).</summary>
    private const string JammedReason = "Last bag line jammed";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Worked examples A to D (AC-20.5 to AC-20.8) ---------------------------------------------------------------

    /// <summary>
    /// AC-20.5 (worked example A), AC-19.1 to AC-19.3, AC-19.7, AC-20.1 to AC-20.3, AC-20.9, AC-05.3, design 5.7 (the
    /// completed-order example) and 7.3. Order #12: 1,000 bags, 1 kg Cheese per bag; leftover applied 5, warehouse send
    /// 995, complementary send 20, so production storage holds 1,020 kg. A Production user completes it with 1,001 bags,
    /// 4 kg leftover and "Last bag line jammed". Every line figure is the PRD's: required 1,001, remaining 4, usage 1,016,
    /// variance +15 (+1.50%, not above 2%); output variance +1 bag (+0.10%), tolerance 2. One ConsumedInProduction of
    /// −1,016 kg and one ProducedOutput of +1,001 bags, nothing else; production storage then holds 4 kg. Exactly two
    /// notifications, OutputVariance and LeftoverAtCompletion, with the design 7.3 example messages word for word and the
    /// design 5.10 data. The Sent complementary order is unchanged (AC-19.6). <c>GET</c> returns the same body, the stored
    /// percentage is 6 places (decision #58), and the completion is logged.
    /// </summary>
    [Fact]
    public async Task Example_A_within_tolerance_registers_output_and_two_notifications()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await ExampleAbSceneAsync(app, time, complementary: 20m);
        Assert.Equal(1020m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());

        var response = await CompleteAsync(scene.Production, 12, 1001m, JammedReason, (scene.Cheese.Id, 4m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal("Completed", order.GetProperty("status").GetString());
        AssertOrderFigures(order, produced: 1001m, outputVariance: 1m, outputPercent: 0.1m, reason: JammedReason);
        AssertCompletedLine(Line(order, scene.Cheese.Id), new CompletedLine(
            LeftoverApplied: 5m, WarehouseSend: 995m, ComplementarySends: 20m, CarriedLeftover: 0m, LeftoverEntered: 4m,
            LeftoverRemaining: 4m, ActualUsage: 1016m, Required: 1001m, UsageVariance: 15m, Percent: 1.5m, Above: false));

        // AC-05.3, design 5.7: the history ends with Completed, by the completing user, at the completion time, with the
        // reason as its note.
        var completed = order.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T15:40:00Z", completed.GetProperty("at").GetString());
        Assert.Equal(scene.ProductionUser.Id, completed.GetProperty("by").GetProperty("id").GetString());
        Assert.Equal(JammedReason, completed.GetProperty("note").GetString());

        // AC-19.6: a Sent complementary order is final and stays Sent.
        var complementary = Assert.Single(order.GetProperty("complementaryOrders").EnumerateArray());
        Assert.Equal("Sent", complementary.GetProperty("status").GetString());

        // AC-19.2, AC-20.9: the only completion movements; AC-19.3: storage holds the leftover entered.
        var completionTime = new DateTime(2026, 11, 11, 15, 40, 0, DateTimeKind.Utc);
        var movements = await CompletionMovementsAsync(app, 12);
        Assert.Equal(2, movements.Count);
        AssertConsumed(movements, scene.Cheese.Id, 1016m, scene.ProductionUser.Id, completionTime);
        AssertProduced(movements, scene.Product.Id, 1001m, scene.ProductionUser.Id, completionTime);
        Assert.Equal(4m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.Equal(1001m, (await StockAsync(scene.Manager, scene.Product.Id)).GetProperty("warehouse").GetDecimal());

        // AC-20.5 "Total: 2 notifications", AC-20.2, AC-19.7, design 7.3 and 5.10.
        var notifications = await NotificationsOfOrderAsync(app, 12);
        Assert.Equal([NotificationType.OutputVariance, NotificationType.LeftoverAtCompletion], notifications.Select(n => n.Type));
        Assert.All(notifications, notification => Assert.Equal(completionTime, notification.CreatedAt));
        var outputNotification = notifications[0];
        Assert.Equal("Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).", outputNotification.Message);
        AssertOutputData(Data(outputNotification), scene.Product.Id, ordered: 1000m, produced: 1001m, difference: 1m, percent: 0.1m);
        var leftoverNotification = notifications[1];
        Assert.Equal(
            "Order #12 was completed with leftover in production storage: Cheese 4 kg. Reason: \"Last bag line jammed\"",
            leftoverNotification.Message);
        var leftoverData = Data(leftoverNotification);
        var material = Assert.Single(leftoverData.GetProperty("materials").EnumerateArray());
        AssertMaterial(material, scene.Cheese.Id, "Cheese", "kg", "leftover", 4m);
        Assert.Equal(JammedReason, leftoverData.GetProperty("reason").GetString());

        // Decision #58: 15 ÷ 1,001 × 100 = 1.4985014985...; stored to 6 places, shown to 2. AC-20.10: tolerance stored.
        var stored = await StoredOrderAsync(app, 12);
        Assert.Equal(1.498501m, stored.Lines.Single().UsageVariancePercent);
        Assert.Equal(0.1m, stored.OutputVariancePercent);
        Assert.Equal(2m, stored.ToleranceAtCompletionPercent);
        Assert.Equal(completionTime, stored.CompletedAt);
        Assert.Equal(scene.ProductionUser.Id, stored.CompletedById);
        Assert.Equal(completionTime, stored.StatusChangedAt);

        // Decision #92: the response is the detail every reader gets. Architecture 6: the state change is logged.
        Assert.Equal(order.GetRawText(), (await ReadOrderAsync(scene.Manager, 12)).GetRawText());
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Order 12 completed by user {scene.ProductionUser.Id}.");
    }

    /// <summary>
    /// AC-20.6 (worked example B), AC-20.4, design 5.10 and 7.3 (the UsageVariance example, word for word), decision #58.
    /// As example A, but the complementary send is 30 kg and the leftover entered is 0 kg with no reason. ActualUsage =
    /// 5 + 995 + 30 − 0 = 1,030 kg, variance +29 kg, +2.90%, above 2%: one UsageVariance notification. Output +1 bag: one
    /// OutputVariance. No LeftoverAtCompletion. The response's percentages equal the notifications' <c>data.percent</c>.
    /// Production storage is emptied (−1,030 kg).
    /// </summary>
    [Fact]
    public async Task Example_B_above_tolerance_raises_a_usage_variance_notification()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await ExampleAbSceneAsync(app, time, complementary: 30m);

        // Design 5.7: the reason may be omitted when every leftover is zero.
        var response = await CompleteRawAsync(scene.Production, 12,
            $$"""{ "producedQuantity": 1001, "lines": [ { "itemId": {{scene.Cheese.Id}}, "leftover": 0 } ] }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        AssertOrderFigures(order, produced: 1001m, outputVariance: 1m, outputPercent: 0.1m, reason: null);
        var line = Line(order, scene.Cheese.Id);
        AssertCompletedLine(line, new CompletedLine(
            LeftoverApplied: 5m, WarehouseSend: 995m, ComplementarySends: 30m, CarriedLeftover: 0m, LeftoverEntered: 0m,
            LeftoverRemaining: 0m, ActualUsage: 1030m, Required: 1001m, UsageVariance: 29m, Percent: 2.9m, Above: true));

        var notifications = await NotificationsOfOrderAsync(app, 12);
        Assert.Equal([NotificationType.UsageVariance, NotificationType.OutputVariance], notifications.Select(n => n.Type));
        Assert.Equal(
            "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%).",
            notifications[0].Message);
        var usageData = Data(notifications[0]);
        AssertUsageData(usageData, scene.Cheese.Id, "Cheese", "kg", required: 1001m, actualUsage: 1030m, difference: 29m, percent: 2.9m);
        Assert.Equal("Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).", notifications[1].Message);

        // Decision #58: the response and the notification show the same figure.
        Assert.Equal(line.GetProperty("usageVariancePercent").GetDecimal(), usageData.GetProperty("percent").GetDecimal());
        Assert.Equal(order.GetProperty("outputVariancePercent").GetDecimal(),
            Data(notifications[1]).GetProperty("percent").GetDecimal());

        AssertConsumed(await CompletionMovementsAsync(app, 12), scene.Cheese.Id, 1030m, scene.ProductionUser.Id, null);
        Assert.Equal(0m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-20.7 (worked example C), decision #12 (under-use notifies too), AC-20.1 (output variance recorded when zero),
    /// AC-20.2 (none created when zero), AC-19.7. Planned 1,000 kg, leftover applied 0, send 1,000 kg, produced 1,000 bags,
    /// leftover 25 kg, completed by a Manager. ActualUsage 975 kg, variance −25 kg, −2.50%: one UsageVariance. Output
    /// variance 0 and 0%: no OutputVariance. One LeftoverAtCompletion. ConsumedInProduction −975 kg; production storage
    /// then holds 25 kg, all of it free leftover (AC-19.3).
    /// </summary>
    [Fact]
    public async Task Example_C_under_use_notifies_and_zero_output_variance_does_not()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);

        var response = await CompleteAsync(scene.Manager, scene.Number, 1000m, "Scale drift at end of shift", (scene.Cheese.Id, 25m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        AssertOrderFigures(order, produced: 1000m, outputVariance: 0m, outputPercent: 0m, reason: "Scale drift at end of shift");
        AssertCompletedLine(Line(order, scene.Cheese.Id), new CompletedLine(
            LeftoverApplied: 0m, WarehouseSend: 1000m, ComplementarySends: 0m, CarriedLeftover: 0m, LeftoverEntered: 25m,
            LeftoverRemaining: 25m, ActualUsage: 975m, Required: 1000m, UsageVariance: -25m, Percent: -2.5m, Above: true));
        Assert.Equal(scene.ManagerUser.Id,
            order.GetProperty("history").EnumerateArray().Last().GetProperty("by").GetProperty("id").GetString());

        var notifications = await NotificationsOfOrderAsync(app, scene.Number);
        Assert.Equal([NotificationType.UsageVariance, NotificationType.LeftoverAtCompletion], notifications.Select(n => n.Type));
        Assert.Equal(
            $"Order #{scene.Number}, Cheese: used 975 kg, required 1,000 kg for actual output. Difference -25 kg (-2.50%).",
            notifications[0].Message);
        AssertUsageData(Data(notifications[0]), scene.Cheese.Id, "Cheese", "kg", 1000m, 975m, -25m, -2.5m);
        Assert.Equal(
            $"Order #{scene.Number} was completed with leftover in production storage: Cheese 25 kg. Reason: \"Scale drift at end of shift\"",
            notifications[1].Message);

        var movements = await CompletionMovementsAsync(app, scene.Number);
        AssertConsumed(movements, scene.Cheese.Id, 975m, scene.ManagerUser.Id, null);
        AssertProduced(movements, scene.Product.Id, 1000m, scene.ManagerUser.Id, null);
        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal(25m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(25m, stock.GetProperty("freeLeftover").GetDecimal());

        // AC-20.1: recorded as zero, not left empty.
        var stored = await StoredOrderAsync(app, scene.Number);
        Assert.Equal(0m, stored.OutputVariance);
        Assert.Equal(0m, stored.OutputVariancePercent);
    }

    /// <summary>
    /// AC-20.8 (worked example D), D-8, decision #9 (the leftover entered is the physical total), AC-19.3, AC-19.7.
    /// Continuing AC-15.5: 50 kg of free Cheese, #7 (30 kg), #8 (1,000 kg) and #9 (500 kg) Created; #8 sent first with
    /// leftover applied 20 and send 980, so carried leftover 30 and production storage 1,030 kg. #8 completes with 1,000
    /// bags and 30 kg leftover: LeftoverRemaining 30 − 30 = 0, ActualUsage 20 + 980 − 0 = 1,000 kg, variance 0 (0%, not
    /// above). ConsumedInProduction −1,000 kg; production storage holds 30 kg, which is again allocated to #7 (estimate:
    /// leftover 30, nominal 0, range 0 to 0.6) while #9 still gets nothing. Only a LeftoverAtCompletion notification, since
    /// leftover is above zero; no usage or output variance.
    /// </summary>
    [Fact]
    public async Task Example_D_subtracts_carried_leftover_and_leaves_it_for_the_older_order()
    {
        var time = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);

        var response = await CompleteAsync(scene.Production, 8, 1000m, "Carried leftover still on the shelf", (scene.Cheese.Id, 30m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        AssertOrderFigures(order, produced: 1000m, outputVariance: 0m, outputPercent: 0m, reason: "Carried leftover still on the shelf");
        AssertCompletedLine(Line(order, scene.Cheese.Id), new CompletedLine(
            LeftoverApplied: 20m, WarehouseSend: 980m, ComplementarySends: 0m, CarriedLeftover: 30m, LeftoverEntered: 30m,
            LeftoverRemaining: 0m, ActualUsage: 1000m, Required: 1000m, UsageVariance: 0m, Percent: 0m, Above: false));

        AssertConsumed(await CompletionMovementsAsync(app, 8), scene.Cheese.Id, 1000m, scene.ProductionUser.Id, null);
        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal(30m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("freeLeftover").GetDecimal());
        AssertEstimate(Line(await ReadOrderAsync(scene.Manager, 7), scene.Cheese.Id), 30m, 0m, 0m, 0.6m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Manager, 9), scene.Cheese.Id), 0m, 500m, 490m, 510m);

        var notification = Assert.Single(await NotificationsOfOrderAsync(app, 8));
        Assert.Equal(NotificationType.LeftoverAtCompletion, notification.Type);
        Assert.Equal(
            "Order #8 was completed with leftover in production storage: Cheese 30 kg. Reason: \"Carried leftover still on the shelf\"",
            notification.Message);
    }

    /// <summary>
    /// Decision #48, D-9, AC-19.2, AC-19.3: in the example D scene, #8 completes with only 20 kg weighed, less than the
    /// 30 kg carried leftover, so the run also used 10 kg of the carried material. LeftoverRemaining = 20 − 30 = −10 kg,
    /// stored and returned as calculated (not clamped); ActualUsage = 20 + 980 − (−10) = 1,010 kg, variance +10 (+1.00%, not
    /// above 2%). The ConsumedInProduction movement is the same 1,010 kg (1,030 − 20), and production storage then holds
    /// exactly the 20 kg entered.
    /// </summary>
    [Fact]
    public async Task Negative_leftover_remaining_is_stored_as_calculated()
    {
        var time = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);

        var response = await CompleteAsync(scene.Production, 8, 1000m, "Used some of the shelf stock", (scene.Cheese.Id, 20m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCompletedLine(Line(await ApiJson.ReadAsync(response), scene.Cheese.Id), new CompletedLine(
            LeftoverApplied: 20m, WarehouseSend: 980m, ComplementarySends: 0m, CarriedLeftover: 30m, LeftoverEntered: 20m,
            LeftoverRemaining: -10m, ActualUsage: 1010m, Required: 1000m, UsageVariance: 10m, Percent: 1m, Above: false));
        Assert.Equal(-10m, (await StoredOrderAsync(app, 8)).Lines.Single().LeftoverRemaining);
        AssertConsumed(await CompletionMovementsAsync(app, 8), scene.Cheese.Id, 1010m, scene.ProductionUser.Id, null);
        Assert.Equal(20m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.Equal([NotificationType.LeftoverAtCompletion], (await NotificationsOfOrderAsync(app, 8)).Select(n => n.Type));
    }

    // ---- Decision #98: choices the design leaves open -------------------------------------------------------------

    /// <summary>
    /// Decision #98 (f), AC-19.2, AC-19.3. Production storage is changed during the run (a ledger Adjustment of +10 kg;
    /// no HTTP path allows this today, AC-12.5), so it holds 1,010 kg instead of the 1,000 kg sent. Completing with 25 kg
    /// left: ActualUsage stays the D-9 figure, 1,000 − 25 = 975 kg, while the ConsumedInProduction movement follows
    /// AC-19.2, 1,010 − 25 = 985 kg, so production storage still ends at exactly the 25 kg entered. The completion
    /// succeeds and exactly one Warning names the order, the item and both figures.
    /// </summary>
    [Fact]
    public async Task Stock_changed_during_the_run_follows_AC_19_2_and_logs_a_warning()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        await PutLeftoverAsync(app, scene.Cheese.Id, 10m, scene.ManagerUser.Id);

        var response = await CompleteAsync(scene.Production, scene.Number, 1000m, "Recount", (scene.Cheese.Id, 25m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(975m, Line(await ApiJson.ReadAsync(response), scene.Cheese.Id).GetProperty("actualUsage").GetDecimal());
        AssertConsumed(await CompletionMovementsAsync(app, scene.Number), scene.Cheese.Id, 985m, scene.ProductionUser.Id, null);
        Assert.Equal(25m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        var warning = Assert.Single(app.Logs.Entries, entry => entry.Level == LogLevel.Warning && IsMismatchWarning(entry.Message));
        // The log writes the decimals as they come back from SQLite, possibly with a trailing ".0", so the two figures are
        // read out and compared as numbers.
        var match = System.Text.RegularExpressions.Regex.Match(warning.Message,
            @"^Order (\d+), item (\d+): consumed ([\d.]+) differs from actual usage ([\d.]+)\.$");
        Assert.True(match.Success, warning.Message);
        Assert.Equal(scene.Number, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(scene.Cheese.Id, int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(985m, decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(975m, decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Decision #98 (f), architecture 6 (Logging), PRD 4.1 "rejected": the mismatch Warning is written only after the
    /// commit. The same mismatch scene, but the completion's last save fails (an EF Core interceptor throws as the
    /// notifications are about to be saved, after the movements and the mismatch check ran). The answer is <c>500</c>,
    /// nothing is recorded, production storage still holds 1,010 kg, and neither the mismatch Warning nor the "completed"
    /// line is logged.
    /// </summary>
    [Fact]
    public async Task Rolled_back_completion_logs_no_mismatch_warning()
    {
        var failLastSave = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Added),
            work: (_, _) => throw new InvalidOperationException("Save failed on purpose by the test."));
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(failLastSave)),
        };
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        await PutLeftoverAsync(app, scene.Cheese.Id, 10m, scene.ManagerUser.Id);

        var response = await CompleteAsync(scene.Production, scene.Number, 1000m, "Recount", (scene.Cheese.Id, 25m));

        Assert.True(failLastSave.HasRun);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertNothingCompletedAsync(app, scene.Number, OrderStatus.InProduction);
        Assert.Equal(1010m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.DoesNotContain(app.Logs.Entries, entry => IsMismatchWarning(entry.Message));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains("completed by user", StringComparison.Ordinal));
    }

    /// <summary>
    /// Decision #98 (d), AC-20.4, design 7.3. Both materials of a two-line recipe (Cheese 1 kg, Potato starch 0.015 kg per
    /// bag) are over-used: 1,020 kg and 15.3 kg sent, 990 bags made, nothing left, so +30 kg and +0.45 kg, both +3.03%.
    /// The body lists starch first, but the two UsageVariance notifications come in recipe line order (Cheese, then
    /// starch), followed by the OutputVariance (−10 bags); with no leftover there is no LeftoverAtCompletion.
    /// </summary>
    [Fact]
    public async Task Usage_notifications_follow_recipe_line_order_not_the_body()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 1020m);
        await ReceiveAsync(manager, starch.Id, 15.3m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1020m), (starch.Id, 15.3m))).StatusCode);

        var response = await CompleteAsync(manager, number, 990m, null, (starch.Id, 0m), (cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notifications = await NotificationsOfOrderAsync(app, number);
        Assert.Equal([NotificationType.UsageVariance, NotificationType.UsageVariance, NotificationType.OutputVariance],
            notifications.Select(n => n.Type));
        Assert.Equal(
            $"Order #{number}, Cheese: used 1,020 kg, required 990 kg for actual output. Difference +30 kg (+3.03%).",
            notifications[0].Message);
        Assert.Equal(
            $"Order #{number}, Potato starch: used 15.3 kg, required 14.85 kg for actual output. Difference +0.45 kg (+3.03%).",
            notifications[1].Message);
    }

    /// <summary>
    /// Decision #98 (c), design 2.6, AC-19.7. A reason sent when every leftover is zero is accepted and stored trimmed, as
    /// the completion reason and as the note of the Completed history entry; since nothing is left, no
    /// LeftoverAtCompletion is raised (and usage and output both match the plan, so there is no notification at all).
    /// </summary>
    [Fact]
    public async Task Reason_with_no_leftover_is_stored_trimmed_without_a_leftover_notification()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);

        var response = await CompleteAsync(scene.Production, scene.Number, 1000m, "  Only a note  ", (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal("Only a note", order.GetProperty("completionReason").GetString());
        Assert.Equal("Only a note", order.GetProperty("history").EnumerateArray().Last().GetProperty("note").GetString());
        Assert.Equal("Only a note", (await StoredOrderAsync(app, scene.Number)).CompletionReason);
        Assert.Empty(await NotificationsOfOrderAsync(app, scene.Number));
    }

    // ---- AC-20.4: the tolerance boundary ----------------------------------------------------------------------

    /// <summary>
    /// AC-20.4, PRD section 6 row 5 (limit included), decision #47, decision #12, design 2.4. The comparison uses the
    /// unrounded percentage: exactly −2.00% (1,000 kg sent, 1,000 bags, 20 kg left) and exactly +2.00% (510 kg sent for 500
    /// bags, nothing left) create no UsageVariance notification and are not above tolerance; −2.0001% (10,000 kg sent,
    /// 10,000 bags, 200.01 kg left) creates one, shown as −2.00%, and is above. The stored percentage keeps 6 places
    /// (−2.0001).
    /// </summary>
    [Theory]
    [InlineData(1000, 1000, 1000, "20", "-2", false)]
    [InlineData(500, 510, 500, "0", "2", false)]
    [InlineData(10000, 10000, 10000, "200.01", "-2.0001", true)]
    public async Task Exactly_the_tolerance_does_not_notify_and_just_above_does(
        int ordered, int send, int produced, string leftoverText, string storedPercentText, bool above)
    {
        var leftover = decimal.Parse(leftoverText, System.Globalization.CultureInfo.InvariantCulture);
        var storedPercent = decimal.Parse(storedPercentText, System.Globalization.CultureInfo.InvariantCulture);
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered, send);

        var response = await CompleteAsync(scene.Production, scene.Number, produced, "Counted at the end", (scene.Cheese.Id, leftover));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Line(await ApiJson.ReadAsync(response), scene.Cheese.Id);
        Assert.Equal(above, line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
        Assert.Equal(Math.Round(storedPercent, 2), line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(storedPercent, (await StoredOrderAsync(app, scene.Number)).Lines.Single().UsageVariancePercent);

        var usage = (await NotificationsOfOrderAsync(app, scene.Number)).Where(n => n.Type == NotificationType.UsageVariance).ToList();
        if (!above)
        {
            Assert.Empty(usage);
            return;
        }

        // "a line at 2.0001% creates one (shown as 2.00%)".
        Assert.Equal(
            $"Order #{scene.Number}, Cheese: used 9,799.99 kg, required 10,000 kg for actual output. Difference -200.01 kg (-2.00%).",
            Assert.Single(usage).Message);
        Assert.Equal(-2m, Data(usage[0]).GetProperty("percent").GetDecimal());
    }

    // ---- Decision #49: the required amount rounds to 0 ---------------------------------------------------------

    /// <summary>
    /// Decision #49 and #57, design 5.7 and 7.3 (the null-percent example), decision #48. Recipe 0.0004 kg of Potato starch
    /// per bag, order of 1 bag: planned and required both round to 0 kg. With 0.004 kg of starch already in production
    /// storage (carried leftover, since nothing is planned) and 0 kg weighed at the end, the run used 0.004 kg: the
    /// completion succeeds, LeftoverRemaining is −0.004, the percentage is null in the response and in storage, the line is
    /// above tolerance, and the UsageVariance notification reads "... Difference +0.004 kg (percentage not applicable)."
    /// with <c>data.percent</c> null. ConsumedInProduction −0.004 kg.
    /// </summary>
    [Fact]
    public async Task Required_amount_of_zero_with_usage_gives_a_null_percent_and_notifies()
    {
        await using var app = new TestApiFactory();
        var scene = await TinyAmountSceneAsync(app, starchInStorage: 0.004m);

        var response = await CompleteAsync(scene.Production, scene.Number, 1m, null, (scene.Starch.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Line(await ApiJson.ReadAsync(response), scene.Starch.Id);
        AssertCompletedLine(line, new CompletedLine(
            LeftoverApplied: 0m, WarehouseSend: 0m, ComplementarySends: 0m, CarriedLeftover: 0.004m, LeftoverEntered: 0m,
            LeftoverRemaining: -0.004m, ActualUsage: 0.004m, Required: 0m, UsageVariance: 0.004m, Percent: null, Above: true));
        Assert.Null((await StoredOrderAsync(app, scene.Number)).Lines.Single().UsageVariancePercent);

        var notification = Assert.Single(await NotificationsOfOrderAsync(app, scene.Number));
        Assert.Equal(NotificationType.UsageVariance, notification.Type);
        Assert.Equal(
            $"Order #{scene.Number}, Potato starch: used 0.004 kg, required 0 kg for actual output. Difference +0.004 kg (percentage not applicable).",
            notification.Message);
        AssertUsageData(Data(notification), scene.Starch.Id, "Potato starch", "kg", 0m, 0.004m, 0.004m, percent: null);
        AssertConsumed(await CompletionMovementsAsync(app, scene.Number), scene.Starch.Id, 0.004m, scene.ProductionUser.Id, null);
    }

    /// <summary>
    /// Decision #49, AC-19.2, PRD section 6 row 17: the same tiny recipe with nothing in production storage and 0 kg
    /// weighed. Required 0 and usage 0: the percentage is null, the line is not above tolerance, and no notification is
    /// raised. Nothing was consumed, so there is no ConsumedInProduction movement at all; the ProducedOutput (+1 bag) is
    /// the only completion movement.
    /// </summary>
    [Fact]
    public async Task Required_amount_of_zero_without_usage_gives_a_null_percent_and_no_movement()
    {
        await using var app = new TestApiFactory();
        var scene = await TinyAmountSceneAsync(app, starchInStorage: 0m);

        var response = await CompleteAsync(scene.Production, scene.Number, 1m, null, (scene.Starch.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCompletedLine(Line(await ApiJson.ReadAsync(response), scene.Starch.Id), new CompletedLine(
            LeftoverApplied: 0m, WarehouseSend: 0m, ComplementarySends: 0m, CarriedLeftover: 0m, LeftoverEntered: 0m,
            LeftoverRemaining: 0m, ActualUsage: 0m, Required: 0m, UsageVariance: 0m, Percent: null, Above: false));
        Assert.Empty(await NotificationsOfOrderAsync(app, scene.Number));
        var movement = Assert.Single(await CompletionMovementsAsync(app, scene.Number));
        Assert.Equal(MovementType.ProducedOutput, movement.Type);
        Assert.Equal(1m, movement.WarehouseChange);
    }

    // ---- Decisions #82 and #63: very large percentages and very long messages --------------------------------------

    /// <summary>
    /// Decision #82, decision #80, architecture 3.1: an order of 0.001 bag (1 kg per bag, 0.001 kg sent) completed with the
    /// largest allowed produced quantity, 999,999,999.999. Output variance 999,999,999.998 and output variance percentage
    /// 99,999,999,999,800 (about 1e14, beyond the 12 integer digits of decimal(18,6)) are stored and returned without
    /// error; the usage variance percentage (−99.9999999999...%) is stored rounded to 6 places as −100.
    /// </summary>
    [Fact]
    public async Task Huge_output_variance_percentage_is_stored_and_returned()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 0.001m, send: 0.001m);

        var response = await CompleteAsync(scene.Production, scene.Number, 999_999_999.999m, null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal(999_999_999.998m, order.GetProperty("outputVariance").GetDecimal());
        Assert.Equal(99_999_999_999_800m, order.GetProperty("outputVariancePercent").GetDecimal());
        var stored = await StoredOrderAsync(app, scene.Number);
        Assert.Equal(99_999_999_999_800m, stored.OutputVariancePercent);
        Assert.Equal(-100m, stored.Lines.Single().UsageVariancePercent);
        Assert.Equal(999_999_999.999m, (await StockAsync(scene.Manager, scene.Product.Id)).GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// Decision #63, AC-19.7 ("listing every material"), design 7.3. An order of ten materials whose names have the
    /// maximum 100 characters, in a unit whose name has the maximum 50, completed with leftover on every line and a reason
    /// of the maximum 500 characters (with quotes and accented letters). The LeftoverAtCompletion message is about 1,700
    /// characters, far over the old 1,000 limit; it is stored exactly as design 7.3 builds it, every material in recipe
    /// line order, character for character, and its data lists all ten.
    /// </summary>
    [Fact]
    public async Task Leftover_notification_longer_than_1000_characters_is_stored_whole()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var unit = await Seed.CreateUnitAsync(manager, $"unit-{Guid.NewGuid():N}".PadRight(50, 'u'));
        var materials = new List<CreatedItem>();
        for (var i = 0; i < 10; i++)
        {
            var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit,
                name: $"Material {i} {Guid.NewGuid():N}".PadRight(100, 'm'));
            materials.Add(material);
            await ReceiveAsync(manager, material.Id, 20m);
        }

        var product = await ProductWithRecipeAsync(manager, materials.Select(material => (material.Id, 1m)).ToArray());
        var number = await CreateOrderAsync(manager, product.Id, 10m);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(warehouse, number, materials.Select(material => (material.Id, 10m)).ToArray())).StatusCode);
        var reason = ("Bandeja dañada, «revisión» pendiente; operator said \"check the hopper\". " + new string('é', 500))[..500];

        // Sent in reverse order: the message still lists them in recipe line order (design 7.3).
        var response = await CompleteAsync(manager, number, 10m, reason,
            materials.Select(material => (material.Id, 1.5m)).Reverse().ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = $"Order #{number} was completed with leftover in production storage: " +
            string.Join(", ", materials.Select(material => $"{material.Name} 1.5 {unit.Name}")) +
            $". Reason: \"{reason}\"";
        Assert.True(expected.Length > 1000, $"The test message has only {expected.Length} characters.");
        var notification = Assert.Single(await NotificationsOfOrderAsync(app, number),
            n => n.Type == NotificationType.LeftoverAtCompletion);
        Assert.Equal(expected, notification.Message);
        var data = Data(notification);
        Assert.Equal(materials.Select(material => material.Id),
            data.GetProperty("materials").EnumerateArray().Select(entry => entry.GetProperty("itemId").GetInt32()));
        Assert.Equal(reason, data.GetProperty("reason").GetString());
    }

    // ---- AC-19.6: unsent complementary orders are cancelled ---------------------------------------------------------

    /// <summary>
    /// AC-19.6, PRD section 6 row 6, AC-05.3: the order's complementary orders (inserted directly; T-017 builds them) that
    /// are PendingApproval or Approved become Cancelled with the note "Parent order completed", recording the completing
    /// user and the completion time. Rejected and Sent ones are unchanged, and a complementary order of another order is
    /// not touched. The response lists the new statuses.
    /// </summary>
    [Fact]
    public async Task Unsent_complementary_orders_are_cancelled_on_completion()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        var other = await CreateOrderAsync(scene.Manager, scene.Product.Id, 10m);
        var userId = scene.ManagerUser.Id;
        var pending = await InsertComplementaryOrderAsync(app, scene.Number, ComplementaryOrderStatus.PendingApproval, scene.Cheese.Id, userId);
        var approved = await InsertComplementaryOrderAsync(app, scene.Number, ComplementaryOrderStatus.Approved, scene.Cheese.Id, userId);
        var rejected = await InsertComplementaryOrderAsync(app, scene.Number, ComplementaryOrderStatus.Rejected, scene.Cheese.Id, userId);
        var sent = await InsertComplementaryOrderAsync(app, scene.Number, ComplementaryOrderStatus.Sent, scene.Cheese.Id, userId);
        var otherPending = await InsertComplementaryOrderAsync(app, other, ComplementaryOrderStatus.PendingApproval, scene.Cheese.Id, userId);
        time.SetUtcNow(EightAm.AddHours(5));

        var response = await CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Cancelled", "Cancelled", "Rejected", "Sent"],
            (await ApiJson.ReadAsync(response)).GetProperty("complementaryOrders").EnumerateArray()
                .Select(entry => entry.GetProperty("status").GetString()));

        var completionTime = EightAm.AddHours(5).UtcDateTime;
        foreach (var number in new[] { pending, approved })
        {
            var cancelled = await StoredComplementaryOrderAsync(app, number);
            Assert.Equal(ComplementaryOrderStatus.Cancelled, cancelled.Status);
            Assert.Equal("Parent order completed", cancelled.CancellationNote);
            Assert.Equal(completionTime, cancelled.CancelledAt);
            Assert.Equal(scene.ProductionUser.Id, cancelled.CancelledById);
        }

        foreach (var (number, status) in new[] { (rejected, ComplementaryOrderStatus.Rejected), (sent, ComplementaryOrderStatus.Sent), (otherPending, ComplementaryOrderStatus.PendingApproval) })
        {
            var unchanged = await StoredComplementaryOrderAsync(app, number);
            Assert.Equal(status, unchanged.Status);
            Assert.Null(unchanged.CancellationNote);
            Assert.Null(unchanged.CancelledAt);
        }
    }

    // ---- AC-20.10: the tolerance in force at completion ------------------------------------------------------------

    /// <summary>
    /// AC-20.10: an order completed at tolerance 2% with −2.50% usage (example C) is above tolerance and notified. The app
    /// is then restarted on the same database with the tolerance set to 5%. The new setting is in force (a new order's
    /// estimate shows 5), but the completed order still shows tolerance 2, the same percentage, still above tolerance, and
    /// the notifications are exactly the ones created at completion.
    /// </summary>
    [Fact]
    public async Task Changing_the_tolerance_later_changes_no_recorded_value()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        var completed = await CompleteAsync(scene.Production, scene.Number, 1000m, "Scale drift", (scene.Cheese.Id, 25m));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var before = await ApiJson.ReadAsync(completed);
        var notificationsBefore = (await NotificationsOfOrderAsync(app, scene.Number)).Select(n => (n.Id, n.Message, n.DataJson)).ToList();

        await using var restarted = new TestApiFactory { DatabasePath = app.DatabasePath };
        restarted.Settings["Inventory:TolerancePercent"] = "5";
        using var manager = await restarted.CreateClientAsAsync(Roles.Manager);

        var after = await ReadOrderAsync(manager, scene.Number);
        Assert.Equal(2m, after.GetProperty("toleranceAtCompletionPercent").GetDecimal());
        var line = Line(after, scene.Cheese.Id);
        Assert.True(line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
        Assert.Equal(-2.5m, line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(before.GetProperty("lines").GetRawText(), after.GetProperty("lines").GetRawText());
        Assert.Equal(notificationsBefore,
            (await NotificationsOfOrderAsync(restarted, scene.Number)).Select(n => (n.Id, n.Message, n.DataJson)).ToList());

        // The new setting really is in force for new work.
        var newOrder = await CreateOrderAsync(manager, scene.Product.Id, 10m);
        Assert.Equal(5m, Line(await ReadOrderAsync(manager, newOrder), scene.Cheese.Id)
            .GetProperty("estimate").GetProperty("tolerancePercent").GetDecimal());
    }

    // ---- AC-19.5: output registered once ------------------------------------------------------------------------

    /// <summary>
    /// AC-19.5, architecture 5.2 ("Output registered once"), PRD section 5: two simultaneous completions of the same order
    /// (separate clients) give exactly one <c>200</c> and one <c>409</c> <c>already-completed</c> "Output for order #N has
    /// already been registered.". There is exactly one ProducedOutput and one ConsumedInProduction movement and one set of
    /// notifications. Repeated over three rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Simultaneous_completions_register_output_once()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var productionEmail = Seed.UniqueEmail("production");
        await app.CreateUserAsync(productionEmail, Roles.Production);
        var token = await app.SignInAsync(productionEmail);

        for (var round = 0; round < 3; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m))).StatusCode);
            using var first = app.CreateClientWithToken(token);
            using var second = app.CreateClientWithToken(token);

            var responses = await Task.WhenAll(
                CompleteAsync(first, number, 1001m, "End of run", (cheese.Id, 10m)),
                CompleteAsync(second, number, 1001m, "End of run", (cheese.Id, 10m)));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, "already-completed", $"Output for order #{number} has already been registered.");
            var movements = await CompletionMovementsAsync(app, number);
            Assert.Single(movements, movement => movement.Type == MovementType.ProducedOutput);
            Assert.Single(movements, movement => movement.Type == MovementType.ConsumedInProduction);
            Assert.Equal(1001m, (await StockAsync(manager, product.Id)).GetProperty("warehouse").GetDecimal());
            Assert.Equal(10m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
            Assert.Equal([NotificationType.OutputVariance, NotificationType.LeftoverAtCompletion],
                (await NotificationsOfOrderAsync(app, number)).Select(n => n.Type));
        }
    }

    /// <summary>
    /// AC-19.5: a second completion of a Completed order, even with different figures, is <c>409</c>
    /// <c>already-completed</c>, and nothing more is recorded: the stored figures, movements and notifications are those
    /// of the first completion.
    /// </summary>
    [Fact]
    public async Task Second_completion_is_rejected_and_changes_nothing()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        Assert.Equal(HttpStatusCode.OK,
            (await CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m))).StatusCode);

        var second = await CompleteAsync(scene.Production, scene.Number, 900m, "Recount", (scene.Cheese.Id, 0m));

        await ApiAssert.ProblemAsync(second, HttpStatusCode.Conflict, "already-completed",
            $"Output for order #{scene.Number} has already been registered.");
        var stored = await StoredOrderAsync(app, scene.Number);
        Assert.Equal(1000m, stored.ProducedQuantity);
        Assert.Null(stored.CompletionReason);
        Assert.Equal(2, (await CompletionMovementsAsync(app, scene.Number)).Count);
        Assert.Empty(await NotificationsOfOrderAsync(app, scene.Number));
    }

    /// <summary>
    /// AC-19.5, design 7.2: only an InProduction order can be completed. A Created order is <c>409</c>
    /// <c>order-not-sent</c> "Order #N has not been sent yet. Send its materials before completing it."; a Cancelled order
    /// (status set directly; cancellation is T-016) is <c>409</c> <c>order-cancelled</c> "This order is cancelled.".
    /// Nothing is recorded.
    /// </summary>
    [Fact]
    public async Task Orders_that_are_not_in_production_cannot_be_completed()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (created, cheese, starch) = await TwoLineOrderAsync(manager, receive: false);
        var (cancelled, _, _) = await TwoLineOrderAsync(manager, receive: false);
        await SetStatusAsync(factory, cancelled, OrderStatus.Cancelled, managerUser.Id);

        await ApiAssert.ProblemAsync(await CompleteAsync(manager, created, 1000m, null, (cheese.Id, 0m), (starch.Id, 0m)),
            HttpStatusCode.Conflict, "order-not-sent",
            $"Order #{created} has not been sent yet. Send its materials before completing it.");
        var cancelledLines = (await StoredOrderAsync(factory, cancelled)).Lines;
        await ApiAssert.ProblemAsync(await CompleteAsync(manager, cancelled, 1000m, null,
                cancelledLines.Select(line => (line.RawMaterialId, 0m)).ToArray()),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");

        await AssertNothingCompletedAsync(factory, created, OrderStatus.Created);
        await AssertNothingCompletedAsync(factory, cancelled, OrderStatus.Cancelled);
    }

    // ---- AC-19.4: rejected completions -----------------------------------------------------------------------------

    /// <summary>
    /// AC-19.4, design 7.2, decisions #75, #80, #95, #96: each bad body is <c>400</c> <c>validation</c> with one message
    /// under the field's JSON path. Each case changes one thing in an otherwise valid body for a two-line order (Cheese,
    /// Potato starch): the produced quantity missing, zero, negative, with 4 decimal places or above 999,999,999.999; a
    /// missing line, an item not on the order or unknown, a missing item, the same item twice, no lines or no list; a
    /// leftover missing, negative, with 4 decimal places or above 999,999,999.999; a reason over 500 characters. The orders
    /// are Created: the body is checked before the state (design 2.2), so these are the answers an InProduction order gets
    /// too (see <see cref="Every_body_problem_of_an_order_in_production_is_reported_at_once"/>).
    /// </summary>
    [Theory]
    [InlineData("produced-missing")]
    [InlineData("produced-zero")]
    [InlineData("produced-negative")]
    [InlineData("produced-decimals")]
    [InlineData("produced-above-maximum")]
    [InlineData("missing-line")]
    [InlineData("not-on-order")]
    [InlineData("unknown-item")]
    [InlineData("item-missing")]
    [InlineData("duplicate")]
    [InlineData("empty-list")]
    [InlineData("no-list")]
    [InlineData("leftover-missing")]
    [InlineData("leftover-negative")]
    [InlineData("leftover-decimals")]
    [InlineData("leftover-above-maximum")]
    [InlineData("reason-too-long")]
    public async Task Bad_completion_bodies_are_rejected(string problem)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(manager, receive: false);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var cheeseLine = $$"""{ "itemId": {{cheese.Id}}, "leftover": 0 }""";
        var starchLine = $$"""{ "itemId": {{starch.Id}}, "leftover": 0 }""";
        var lines = $"[ {cheeseLine}, {starchLine} ]";
        var longReason = new string('r', 501);

        var (body, path, message) = problem switch
        {
            "produced-missing" => ($$"""{ "lines": {{lines}} }""", "producedQuantity", "Produced quantity is required."),
            "produced-zero" => ($$"""{ "producedQuantity": 0, "lines": {{lines}} }""",
                "producedQuantity", "Produced quantity must be greater than 0."),
            "produced-negative" => ($$"""{ "producedQuantity": -1, "lines": {{lines}} }""",
                "producedQuantity", "Produced quantity must be greater than 0."),
            "produced-decimals" => ($$"""{ "producedQuantity": 1000.0001, "lines": {{lines}} }""",
                "producedQuantity", "Produced quantity can have at most 3 decimal places."),
            "produced-above-maximum" => ($$"""{ "producedQuantity": 1000000000, "lines": {{lines}} }""",
                "producedQuantity", "Produced quantity must be 999,999,999.999 or less."),
            "missing-line" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}} ] }""",
                "lines", $"Every order line needs a quantity. Missing: {starch.Name}."),
            "not-on-order" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, {{starchLine}}, { "itemId": {{other.Id}}, "leftover": 0 } ] }""",
                "lines[2].itemId", $"Item {other.Id} is not on order #{number}."),
            "unknown-item" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, {{starchLine}}, { "itemId": 999999, "leftover": 0 } ] }""",
                "lines[2].itemId", $"Item 999999 is not on order #{number}."),
            "item-missing" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, {{starchLine}}, { "leftover": 0 } ] }""",
                "lines[2].itemId", "Item is required."),
            "duplicate" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, {{starchLine}}, {{cheeseLine}} ] }""",
                "lines[2].itemId", $"{cheese.Name} appears on more than one line."),
            "empty-list" => ("""{ "producedQuantity": 1000, "lines": [] }""",
                "lines", $"Every order line needs a quantity. Missing: {cheese.Name}, {starch.Name}."),
            "no-list" => ("""{ "producedQuantity": 1000 }""",
                "lines", $"Every order line needs a quantity. Missing: {cheese.Name}, {starch.Name}."),
            "leftover-missing" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}} } ] }""",
                "lines[1].leftover", "Leftover is required."),
            "leftover-negative" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}}, "leftover": -0.001 } ] }""",
                "lines[1].leftover", "Leftover cannot be negative."),
            "leftover-decimals" => ($$"""{ "producedQuantity": 1000, "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}}, "leftover": 0.0001 } ] }""",
                "lines[1].leftover", "Leftover can have at most 3 decimal places."),
            "leftover-above-maximum" => ($$"""{ "producedQuantity": 1000, "lines": [ { "itemId": {{cheese.Id}}, "leftover": 1000000000 }, {{starchLine}} ] }""",
                "lines[0].leftover", "Leftover must be 999,999,999.999 or less."),
            "reason-too-long" => ($$"""{ "producedQuantity": 1000, "lines": {{lines}}, "reason": "{{longReason}}" }""",
                "reason", "Reason must be 500 characters or fewer."),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var response = await CompleteRawAsync(manager, number, body);

        await ApiAssert.ValidationAsync(response, path, message);
        await AssertNothingCompletedAsync(factory, number, OrderStatus.Created);
    }

    /// <summary>
    /// AC-19.4, design 6 and 2.2, decision #74: on an InProduction order every problem in a body is reported in one
    /// <c>400</c> <c>validation</c>, in body order with the missing lines after the lines sent and the reason last: a
    /// produced quantity of 0, a line sent as <c>null</c> (both "required" messages), a negative leftover, the order line
    /// left without a leftover, and a reason over 500 characters. Nothing is recorded and the order stays InProduction.
    /// Then a leftover sent as a string is <c>400</c> "lines[0].leftover has the wrong type." (architecture 4.1 item 2).
    /// </summary>
    [Fact]
    public async Task Every_body_problem_of_an_order_in_production_is_reported_at_once()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var unit = await Seed.CreateUnitAsync(manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m), (salt.Id, 0.01m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        await ReceiveAsync(manager, starch.Id, 15m);
        await ReceiveAsync(manager, salt.Id, 10m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m), (salt.Id, 10m))).StatusCode);
        var longReason = new string('r', 501);

        var response = await CompleteRawAsync(manager, number,
            $$"""{ "producedQuantity": 0, "lines": [ null, { "itemId": {{starch.Id}}, "leftover": -2 }, { "itemId": {{salt.Id}}, "leftover": 0 } ], "reason": "{{longReason}}" }""");

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Produced quantity must be greater than 0. Item is required. Leftover is required. Leftover cannot be negative. " +
            $"Every order line needs a quantity. Missing: {cheese.Name}. Reason must be 500 characters or fewer.");
        Assert.Equal(["Produced quantity must be greater than 0."], ApiJson.Messages(body, "producedQuantity"));
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[0].itemId"));
        Assert.Equal(["Leftover is required."], ApiJson.Messages(body, "lines[0].leftover"));
        Assert.Equal(["Leftover cannot be negative."], ApiJson.Messages(body, "lines[1].leftover"));
        Assert.Equal([$"Every order line needs a quantity. Missing: {cheese.Name}."], ApiJson.Messages(body, "lines"));
        Assert.Equal(["Reason must be 500 characters or fewer."], ApiJson.Messages(body, "reason"));
        await AssertNothingCompletedAsync(app, number, OrderStatus.InProduction);

        var wrongType = await CompleteRawAsync(manager, number,
            $$"""{ "producedQuantity": 1000, "lines": [ { "itemId": {{cheese.Id}}, "leftover": "0" } ] }""");

        await ApiAssert.ValidationAsync(wrongType, "lines[0].leftover", "lines[0].leftover has the wrong type.");
        await AssertNothingCompletedAsync(app, number, OrderStatus.InProduction);
    }

    /// <summary>
    /// AC-19.1, AC-19.4 (PRD message), PRD section 6 row 8: when any leftover entered is above zero, a reason is required.
    /// With 4 kg of Cheese left and the reason missing, <c>null</c>, empty or only spaces, the answer is <c>400</c>
    /// <c>reason-required</c> "A reason is required when leftover is recorded." under <c>reason</c>, and nothing is
    /// recorded. With a reason, the same body is accepted, and the reason is stored trimmed.
    /// </summary>
    [Fact]
    public async Task Leftover_above_zero_needs_a_reason()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        var lines = $$"""[ { "itemId": {{scene.Cheese.Id}}, "leftover": 4 } ]""";

        foreach (var reasonJson in new[] { "", """, "reason": null""", """, "reason": "" """, """, "reason": "   " """ })
        {
            var response = await CompleteRawAsync(scene.Production, scene.Number,
                $$"""{ "producedQuantity": 1000, "lines": {{lines}}{{reasonJson}} }""");

            var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "reason-required",
                "A reason is required when leftover is recorded.");
            Assert.Equal(["A reason is required when leftover is recorded."], ApiJson.Messages(body, "reason"));
            await AssertNothingCompletedAsync(app, scene.Number, OrderStatus.InProduction);
        }

        var accepted = await CompleteRawAsync(scene.Production, scene.Number,
            $$"""{ "producedQuantity": 1000, "lines": {{lines}}, "reason": "  Hopper not emptied  " }""");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Hopper not emptied", (await ApiJson.ReadAsync(accepted)).GetProperty("completionReason").GetString());
    }

    /// <summary>
    /// AC-19.4 (the PRD message, word for word), design 2.2 step 6. In worked example A's scene production storage holds
    /// 1,020 kg of Cheese: a leftover of 1,020.001 kg is <c>400</c> <c>leftover-exceeds-stock</c> "Leftover for Cheese
    /// cannot exceed the 1,020 kg in production storage." under <c>lines[0].leftover</c>, and nothing is recorded. Exactly
    /// 1,020 kg (nothing used at all) is accepted, and then no ConsumedInProduction movement is written.
    /// </summary>
    [Fact]
    public async Task Leftover_cannot_exceed_production_storage()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await ExampleAbSceneAsync(app, time, complementary: 20m);

        var rejected = await CompleteAsync(scene.Production, 12, 1001m, JammedReason, (scene.Cheese.Id, 1020.001m));

        var body = await ApiAssert.ProblemAsync(rejected, HttpStatusCode.BadRequest, "leftover-exceeds-stock",
            "Leftover for Cheese cannot exceed the 1,020 kg in production storage.");
        Assert.Equal(["Leftover for Cheese cannot exceed the 1,020 kg in production storage."],
            ApiJson.Messages(body, "lines[0].leftover"));
        await AssertNothingCompletedAsync(app, 12, OrderStatus.InProduction);

        var accepted = await CompleteAsync(scene.Production, 12, 1001m, JammedReason, (scene.Cheese.Id, 1020m));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var movement = Assert.Single(await CompletionMovementsAsync(app, 12));
        Assert.Equal(MovementType.ProducedOutput, movement.Type);
        Assert.Equal(1020m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-19.4, design 6: every line whose leftover exceeds production storage is named in one <c>400</c>
    /// <c>leftover-exceeds-stock</c>, each under its own <c>lines[i].leftover</c>, in body order; a line within its stock
    /// is not named.
    /// </summary>
    [Fact]
    public async Task Every_leftover_above_its_stock_is_named()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Salt");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m), (salt.Id, 0.01m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        await ReceiveAsync(manager, starch.Id, 15m);
        await ReceiveAsync(manager, salt.Id, 10m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m), (salt.Id, 10m))).StatusCode);

        var response = await CompleteAsync(manager, number, 1000m, "Recount", (salt.Id, 10.5m), (cheese.Id, 5m), (starch.Id, 15.3m));

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "leftover-exceeds-stock",
            "Leftover for Salt cannot exceed the 10 kg in production storage. " +
            "Leftover for Potato starch cannot exceed the 15 kg in production storage.");
        Assert.Equal(["lines[0].leftover", "lines[2].leftover"],
            body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        await AssertNothingCompletedAsync(app, number, OrderStatus.InProduction);
    }

    // ---- Design 2.2: the order of checks ------------------------------------------------------------------------

    /// <summary>
    /// Design 2.2 step 3 before step 4, AC-14.3: an unknown order number is <c>404</c> "Order #999999 not found.", even
    /// with a body that is wrong too.
    /// </summary>
    [Fact]
    public async Task Unknown_order_is_404_before_the_body_is_checked()
    {
        var production = await factory.CreateClientAsAsync(Roles.Production);

        var response = await CompleteRawAsync(production, 999999, """{ "producedQuantity": -1 }""");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Order #999999 not found.");
    }

    /// <summary>
    /// Design 2.2: the body (step 4) comes before the state (step 5), and the state before the stock range (step 6). On a
    /// Cancelled order, a negative leftover is <c>400</c> <c>validation</c> and leftover without a reason is <c>400</c>
    /// <c>reason-required</c>, while a valid body is <c>409</c> <c>order-cancelled</c>. On a Created order with nothing in
    /// production storage, a leftover of 5 (more than the 0 there) is <c>409</c> <c>order-not-sent</c>, not
    /// <c>leftover-exceeds-stock</c>.
    /// </summary>
    [Fact]
    public async Task Body_comes_before_state_and_state_before_the_stock_range()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (cancelled, cheese, starch) = await TwoLineOrderAsync(manager, receive: false);
        await SetStatusAsync(factory, cancelled, OrderStatus.Cancelled, managerUser.Id);
        var (created, createdCheese, createdStarch) = await TwoLineOrderAsync(manager, receive: false);

        await ApiAssert.ValidationAsync(await CompleteAsync(manager, cancelled, 1000m, null, (cheese.Id, -1m), (starch.Id, 0m)),
            "lines[0].leftover", "Leftover cannot be negative.");
        await ApiAssert.ProblemAsync(await CompleteAsync(manager, cancelled, 1000m, null, (cheese.Id, 5m), (starch.Id, 0m)),
            HttpStatusCode.BadRequest, "reason-required", "A reason is required when leftover is recorded.");
        await ApiAssert.ProblemAsync(await CompleteAsync(manager, cancelled, 1000m, "Recount", (cheese.Id, 5m), (starch.Id, 0m)),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
        await ApiAssert.ProblemAsync(
            await CompleteAsync(manager, created, 1000m, "Recount", (createdCheese.Id, 5m), (createdStarch.Id, 0m)),
            HttpStatusCode.Conflict, "order-not-sent",
            $"Order #{created} has not been sent yet. Send its materials before completing it.");
    }

    // ---- Access (PRD 4.2) -----------------------------------------------------------------------------------------

    /// <summary>AC-01.5: completing without a token is <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Complete_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await CompleteRawAsync(anonymous, 1, """{ "producedQuantity": 1 }"""));
    }

    /// <summary>
    /// AC-19.8, AC-05.1, PRD 4.2 ("Complete an order": Production or Manager): a Warehouse-only user gets <c>403</c> "It
    /// needs the Production or Manager role.", for a real InProduction order with a valid body and for an unknown number
    /// (the role is checked before the record, design 2.2). Nothing is recorded. A user holding Warehouse and Production
    /// may complete (AC-05.2).
    /// </summary>
    [Fact]
    public async Task Warehouse_only_user_cannot_complete()
    {
        await using var app = new TestApiFactory();
        var scene = await SimpleSceneAsync(app, ordered: 1000m, send: 1000m);
        using var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);

        await ApiAssert.ProblemAsync(await CompleteAsync(warehouse, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ProductionOrManager);
        await ApiAssert.ProblemAsync(await CompleteAsync(warehouse, 999999, 1000m, null, (scene.Cheese.Id, 0m)),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ProductionOrManager);
        await AssertNothingCompletedAsync(app, scene.Number, OrderStatus.InProduction);

        using var both = await app.CreateClientAsAsync(Roles.Warehouse, Roles.Production);
        Assert.Equal(HttpStatusCode.OK,
            (await CompleteAsync(both, scene.Number, 1000m, null, (scene.Cheese.Id, 0m))).StatusCode);
    }

    // ---- Scenes ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Worked examples A and B (AC-20.5, AC-20.6), with design 5.7's names: Cheese in kg, "Shredded cheese 1 kg bag" in
    /// Bag, 1 kg per bag. 5 kg of free Cheese in production storage; order #12 for 1,000 bags created at 08:00 (orders #1
    /// to #11 are created and cancelled first, so the number is the PRD's); sent at 10:02 with 995 kg (leftover applied 5);
    /// a complementary order requested and sent at 11:20 for <paramref name="complementary"/> kg. The clock is left at
    /// 15:40, the design's completion time.
    /// </summary>
    private static async Task<OrderScene> ExampleAbSceneAsync(TestApiFactory app, FakeTimeProvider time, decimal complementary)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, warehouseUser) = await SignedInAsync(app, Roles.Warehouse);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: ProductName);
        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1m));
        for (var i = 0; i < 11; i++)
        {
            await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 1m), OrderStatus.Cancelled, managerUser.Id);
        }

        await ReceiveAsync(manager, cheese.Id, 1200m);
        await PutLeftoverAsync(app, cheese.Id, 5m, managerUser.Id);
        Assert.Equal(12, await CreateOrderAsync(manager, product.Id, 1000m));

        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, 12, (cheese.Id, 995m))).StatusCode);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(20));
        await ComplementarySendAsync(app, 12, cheese.Id, complementary, warehouseUser.Id);
        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(40));

        return new OrderScene(manager, managerUser, production, productionUser, cheese, product, 12);
    }

    /// <summary>
    /// The AC-15.5 starting point continued to worked example D (AC-20.8): Cheese in kg, 1 kg per bag, 2,000 kg in the
    /// Warehouse, 50 kg of free leftover, Created orders #7 (08:00, 30 bags), #8 (09:00, 1,000 bags) and #9 (10:00, 500
    /// bags) (orders #1 to #6 are created and cancelled first), and #8 sent first with 980 kg: leftover applied 20,
    /// carried leftover 30, production storage 1,030 kg.
    /// </summary>
    private static async Task<OrderScene> OldestFirstSceneAsync(TestApiFactory app, FakeTimeProvider time)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        for (var i = 0; i < 6; i++)
        {
            await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 1m), OrderStatus.Cancelled, managerUser.Id);
        }

        await ReceiveAsync(manager, cheese.Id, 2000m);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        time.SetUtcNow(EightAm);
        Assert.Equal(7, await CreateOrderAsync(manager, product.Id, 30m));
        time.SetUtcNow(EightAm.AddHours(1));
        Assert.Equal(8, await CreateOrderAsync(manager, product.Id, 1000m));
        time.SetUtcNow(EightAm.AddHours(2));
        Assert.Equal(9, await CreateOrderAsync(manager, product.Id, 500m));

        var sent = await SendAsync(warehouse, 8, (cheese.Id, 980m));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(30m, Line(await ApiJson.ReadAsync(sent), cheese.Id).GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(1030m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
        return new OrderScene(manager, managerUser, production, productionUser, cheese, product, 8);
    }

    /// <summary>
    /// One InProduction order: Cheese in kg at 1 kg per bag, <paramref name="ordered"/> bags, no leftover, and
    /// <paramref name="send"/> kg received and sent, so leftover applied and carried leftover are 0.
    /// </summary>
    private static async Task<OrderScene> SimpleSceneAsync(TestApiFactory app, decimal ordered, decimal send)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, send);
        var number = await CreateOrderAsync(manager, product.Id, ordered);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, send))).StatusCode);
        return new OrderScene(manager, managerUser, production, productionUser, cheese, product, number);
    }

    /// <summary>
    /// Decision #49's case: Potato starch in kg at 0.0004 kg per bag, an order of 1 bag (planned 0 kg, range 0 to 0), with
    /// <paramref name="starchInStorage"/> kg of starch already in production storage, sent with 0 kg. The starch there is
    /// all carried leftover, since the order applies nothing (D-5: it plans 0).
    /// </summary>
    private static async Task<TinyScene> TinyAmountSceneAsync(TestApiFactory app, decimal starchInStorage)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (starch.Id, 0.0004m));
        if (starchInStorage > 0m)
        {
            await PutLeftoverAsync(app, starch.Id, starchInStorage, managerUser.Id);
        }

        var number = await CreateOrderAsync(manager, product.Id, 1m);
        var sent = await SendAsync(warehouse, number, (starch.Id, 0m));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(0m, Line(await ApiJson.ReadAsync(sent), starch.Id).GetProperty("plannedAmount").GetDecimal());
        return new TinyScene(production, productionUser, starch, number);
    }

    /// <summary>What a scene set up: the clients and users that act in it, its material, product and order number.</summary>
    private sealed record OrderScene(
        HttpClient Manager, AppUser ManagerUser, HttpClient Production, AppUser ProductionUser,
        CreatedItem Cheese, CreatedItem Product, int Number);

    /// <summary>What <see cref="TinyAmountSceneAsync"/> set up.</summary>
    private sealed record TinyScene(HttpClient Production, AppUser ProductionUser, CreatedItem Starch, int Number);

    /// <summary>The figures a completed line must show (design 5.7), in the order of the response's fields.</summary>
    private sealed record CompletedLine(
        decimal LeftoverApplied, decimal WarehouseSend, decimal ComplementarySends, decimal CarriedLeftover,
        decimal LeftoverEntered, decimal LeftoverRemaining, decimal ActualUsage, decimal Required, decimal UsageVariance,
        decimal? Percent, bool Above);

    // ---- Helpers: requests -------------------------------------------------------------------------------------

    /// <summary>The completion under test with exactly this JSON body.</summary>
    private static Task<HttpResponseMessage> CompleteRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{OrdersPath}/{number}/complete", ApiJson.Content(json), Cancel);

    // ---- Helpers: the states other tasks will produce ------------------------------------------------------------

    /// <summary>
    /// A complementary order requested, approved and sent for <paramref name="quantity"/> of one material, written as T-017
    /// and T-018 will: the Sent complementary order, a SentToProduction movement through the ledger linked to it and its
    /// parent, and the parent line's ComplementarySends increased (D-3), in one write transaction.
    /// </summary>
    private static Task ComplementarySendAsync(TestApiFactory app, int parent, int itemId, decimal quantity, string userId) =>
        InTransactionAsync(app, async (db, ledger, now) =>
        {
            var complementary = new ComplementaryOrder
            {
                ParentOrderNumber = parent, Status = ComplementaryOrderStatus.Sent, Reason = "Hopper spill at start of run",
                RequestedAt = now, RequestedById = userId, ApprovedAt = now, ApprovedById = userId, SentAt = now, SentById = userId,
                Lines = [new ComplementaryOrderLine { LineNo = 1, RawMaterialId = itemId, RequestedAmount = quantity, SentQuantity = quantity }],
            };
            db.ComplementaryOrders.Add(complementary);
            await db.SaveChangesAsync(Cancel);

            var line = await db.ProductionOrderLines.SingleAsync(l => l.OrderNumber == parent && l.RawMaterialId == itemId, Cancel);
            line.ComplementarySends += quantity;
            await ledger.AddAsync(new Movement
            {
                Type = MovementType.SentToProduction, ItemId = itemId, WarehouseChange = -quantity, ProductionStorageChange = quantity,
                OccurredAt = now, UserId = userId, ProductionOrderNumber = parent, ComplementaryOrderNumber = complementary.Number,
            }, Cancel);
        });

    /// <summary>Inserts a complementary order of one line (T-017 builds them) in <paramref name="status"/> and returns its number.</summary>
    private static async Task<int> InsertComplementaryOrderAsync(
        TestApiFactory app, int parent, ComplementaryOrderStatus status, int itemId, string userId)
    {
        var number = 0;
        await InTransactionAsync(app, async (db, _, now) =>
        {
            var complementary = new ComplementaryOrder
            {
                ParentOrderNumber = parent, Status = status, Reason = "Hopper spill at start of run",
                RequestedAt = now, RequestedById = userId,
                Lines = [new ComplementaryOrderLine { LineNo = 1, RawMaterialId = itemId, RequestedAmount = 20m }],
            };
            switch (status)
            {
                case ComplementaryOrderStatus.Approved:
                    (complementary.ApprovedAt, complementary.ApprovedById) = (now, userId);
                    break;
                case ComplementaryOrderStatus.Rejected:
                    (complementary.RejectedAt, complementary.RejectedById) = (now, userId);
                    break;
                case ComplementaryOrderStatus.Sent:
                    (complementary.ApprovedAt, complementary.ApprovedById, complementary.SentAt, complementary.SentById) = (now, userId, now, userId);
                    break;
            }

            db.ComplementaryOrders.Add(complementary);
            await db.SaveChangesAsync(Cancel);
            number = complementary.Number;
        });
        return number;
    }

    // ---- Helpers: reading what was stored --------------------------------------------------------------------------

    /// <summary>The order's ConsumedInProduction and ProducedOutput movements (the ones a completion writes), oldest first.</summary>
    private static async Task<List<Movement>> CompletionMovementsAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ProductionOrderNumber == number
                && (movement.Type == MovementType.ConsumedInProduction || movement.Type == MovementType.ProducedOutput))
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>The order's notifications, in the order they were created.</summary>
    private static async Task<List<Notification>> NotificationsOfOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking()
            .Where(notification => notification.ProductionOrderNumber == number)
            .OrderBy(notification => notification.Id)
            .ToListAsync(Cancel);
    }

    private static async Task<ComplementaryOrder> StoredComplementaryOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ComplementaryOrders.AsNoTracking()
            .SingleAsync(complementary => complementary.Number == number, Cancel);
    }

    /// <summary>Decision #98 (f): the Warning a completion logs when consumed stock and actual usage differ.</summary>
    private static bool IsMismatchWarning(string message) =>
        message.Contains("differs from actual usage", StringComparison.Ordinal);

    /// <summary>The notification's design 5.10 <c>data</c> object.</summary>
    private static JsonElement Data(Notification notification)
    {
        using var document = JsonDocument.Parse(notification.DataJson);
        return document.RootElement.Clone();
    }

    // ---- Helpers: assertions ---------------------------------------------------------------------------------------

    /// <summary>
    /// PRD 4.1 "rejected": the order still has <paramref name="status"/>, no completion figure is stored on it or its
    /// lines, no completion movement names it, and it has no notification.
    /// </summary>
    private static async Task AssertNothingCompletedAsync(TestApiFactory app, int number, OrderStatus status)
    {
        var order = await StoredOrderAsync(app, number);
        Assert.Equal(status, order.Status);
        Assert.Null(order.CompletedAt);
        Assert.Null(order.CompletedById);
        Assert.Null(order.ProducedQuantity);
        Assert.Null(order.OutputVariancePercent);
        Assert.Null(order.ToleranceAtCompletionPercent);
        Assert.All(order.Lines, line =>
        {
            Assert.Null(line.LeftoverEntered);
            Assert.Null(line.ActualUsage);
            Assert.Null(line.UsageVarianceAboveTolerance);
        });
        Assert.Empty(await CompletionMovementsAsync(app, number));
        Assert.Empty(await NotificationsOfOrderAsync(app, number));
    }

    /// <summary>AC-20.1, AC-20.10, design 5.7: the order-level completion fields, at the default 2% tolerance.</summary>
    private static void AssertOrderFigures(JsonElement order, decimal produced, decimal outputVariance, decimal outputPercent, string? reason)
    {
        Assert.Equal("Completed", order.GetProperty("status").GetString());
        Assert.Equal(produced, order.GetProperty("producedQuantity").GetDecimal());
        Assert.Equal(outputVariance, order.GetProperty("outputVariance").GetDecimal());
        Assert.Equal(outputPercent, order.GetProperty("outputVariancePercent").GetDecimal());
        Assert.Equal(2m, order.GetProperty("toleranceAtCompletionPercent").GetDecimal());
        Assert.Equal(reason, order.GetProperty("completionReason").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("cancellationReason").ValueKind);
    }

    /// <summary>AC-20.3, design 5.7: every figure of a completed line, and no estimate.</summary>
    private static void AssertCompletedLine(JsonElement line, CompletedLine expected)
    {
        Assert.Equal(JsonValueKind.Null, line.GetProperty("estimate").ValueKind);
        Assert.Equal(expected.LeftoverApplied, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(expected.WarehouseSend, line.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(expected.ComplementarySends, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(expected.CarriedLeftover, line.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(expected.LeftoverEntered, line.GetProperty("leftoverEntered").GetDecimal());
        Assert.Equal(expected.LeftoverRemaining, line.GetProperty("leftoverRemaining").GetDecimal());
        Assert.Equal(expected.ActualUsage, line.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(expected.Required, line.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(expected.UsageVariance, line.GetProperty("usageVariance").GetDecimal());
        if (expected.Percent is { } percent)
        {
            Assert.Equal(percent, line.GetProperty("usageVariancePercent").GetDecimal());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty("usageVariancePercent").ValueKind);
        }

        Assert.Equal(expected.Above, line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
    }

    /// <summary>
    /// AC-19.2, D-2: exactly one ConsumedInProduction movement for <paramref name="itemId"/>: production storage −quantity,
    /// no Warehouse change, linked to the order, by the completing user (at <paramref name="at"/>, when given).
    /// </summary>
    private static void AssertConsumed(IEnumerable<Movement> movements, int itemId, decimal quantity, string userId, DateTime? at)
    {
        var movement = Assert.Single(movements, m => m.Type == MovementType.ConsumedInProduction && m.ItemId == itemId);
        Assert.Equal(-quantity, movement.ProductionStorageChange);
        Assert.Null(movement.WarehouseChange);
        Assert.Equal(userId, movement.UserId);
        Assert.NotNull(movement.ProductionOrderNumber);
        Assert.Null(movement.ComplementaryOrderNumber);
        if (at is { } time)
        {
            Assert.Equal(time, movement.OccurredAt);
        }
    }

    /// <summary>AC-19.2, D-2: exactly one ProducedOutput movement: the finished product into the Warehouse, +quantity.</summary>
    private static void AssertProduced(IEnumerable<Movement> movements, int productId, decimal quantity, string userId, DateTime? at)
    {
        var movement = Assert.Single(movements, m => m.Type == MovementType.ProducedOutput);
        Assert.Equal(productId, movement.ItemId);
        Assert.Equal(quantity, movement.WarehouseChange);
        Assert.Null(movement.ProductionStorageChange);
        Assert.Equal(userId, movement.UserId);
        if (at is { } time)
        {
            Assert.Equal(time, movement.OccurredAt);
        }
    }

    /// <summary>Design 5.10: the <c>data</c> of a UsageVariance notification.</summary>
    private static void AssertUsageData(JsonElement data, int itemId, string itemName, string unit,
        decimal required, decimal actualUsage, decimal difference, decimal? percent)
    {
        Assert.Equal(itemId, data.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, data.GetProperty("itemName").GetString());
        Assert.Equal(unit, data.GetProperty("unit").GetString());
        Assert.Equal(required, data.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(actualUsage, data.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(difference, data.GetProperty("difference").GetDecimal());
        if (percent is { } value)
        {
            Assert.Equal(value, data.GetProperty("percent").GetDecimal());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, data.GetProperty("percent").ValueKind);
        }
    }

    /// <summary>Design 5.10: the <c>data</c> of an OutputVariance notification for the design 5.7 product.</summary>
    private static void AssertOutputData(JsonElement data, int productId, decimal ordered, decimal produced, decimal difference, decimal percent)
    {
        Assert.Equal(productId, data.GetProperty("finishedProductId").GetInt32());
        Assert.Equal(ProductName, data.GetProperty("finishedProductName").GetString());
        Assert.Equal("Bag", data.GetProperty("unit").GetString());
        Assert.Equal(ordered, data.GetProperty("ordered").GetDecimal());
        Assert.Equal(produced, data.GetProperty("produced").GetDecimal());
        Assert.Equal(difference, data.GetProperty("difference").GetDecimal());
        Assert.Equal(percent, data.GetProperty("percent").GetDecimal());
    }

    /// <summary>Design 5.10: one entry of a notification's <c>materials</c> list.</summary>
    private static void AssertMaterial(JsonElement material, int itemId, string itemName, string unit, string quantityField, decimal quantity)
    {
        Assert.Equal(itemId, material.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, material.GetProperty("itemName").GetString());
        Assert.Equal(unit, material.GetProperty("unit").GetString());
        Assert.Equal(quantity, material.GetProperty(quantityField).GetDecimal());
    }
}
