using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Integration.Scenarios.ScenarioApi;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// T-020: the PRD's worked examples A to D (AC-20.5 to AC-20.8) and the whole AC-15.5 scenario, each from an empty
/// database and through HTTP only, as a client would run them. The start-up Manager creates the staff; a Manager sets up
/// units, items and the recipe; the Warehouse receives stock and sends; Production requests extra material and
/// completes; a Manager approves, reads the notifications and handles the leftover. Every figure, movement and
/// notification text is the PRD's (or design 7.3's template filled with the PRD's figures).
/// </summary>
/// <remarks>
/// <para>
/// Each test has a factory of its own, so its database starts empty and its order numbers are the PRD's (#12 in
/// examples A and B, #7 to #9 in AC-15.5; orders before those are created and cancelled through the API). Times that
/// the PRD or design 5.7 give are set with a fake clock (<see cref="FakeTimeProvider"/>): the clock is not data, so the
/// scenario still writes nothing except through the API. Tokens last 8 hours (AC-01.2), so each scenario signs in no
/// earlier than 8 hours before its last request.
/// </para>
/// <para>
/// Every scenario ends with the two checks in <see cref="ScenarioApi"/>: AC-05.3 (every movement and status change has a
/// user and a UTC time) and AC-11.4 with PRD section 5 (for every item and location the movements add up to the stock,
/// which is never negative).
/// </para>
/// <para>
/// The people are design 5's: maria.lopez (Manager), jorge.ibarra (Warehouse), lucia.mendez (Production).
/// </para>
/// </remarks>
public class WorkedExampleScenarioTests
{
    /// <summary>The finished product of AC-08.3 and design 5.4.</summary>
    private const string ProductName = "Shredded cheese 1 kg bag";

    /// <summary>The complementary request reason of design 5.8 and 7.3.</summary>
    private const string SpillReason = "Hopper spill at start of run";

    /// <summary>The completion reason of worked example A (AC-20.5).</summary>
    private const string JammedReason = "Last bag line jammed";

    /// <summary>8:00 UTC on 11 November 2026, the creation time of order #12 in design 5.7 (and of #7 in AC-15.5).</summary>
    private static readonly DateTimeOffset EightAm = OrderFlow.EightAm;

    // ---- AC-20.5: worked example A ----------------------------------------------------------------------------------

    /// <summary>
    /// AC-20.5 (worked example A), AC-15.3, AC-16.1, AC-17.1, AC-18.1, AC-19.1 to AC-19.3, AC-19.7, AC-20.1 to AC-20.3,
    /// AC-23.1, AC-23.3, from an empty database through HTTP. Order #12: 1,000 bags at 1 kg Cheese per bag, planned
    /// 1,000 kg. 5 kg of free leftover is applied (estimate 5 / 995 / 975 to 1,015), carried leftover 0, the Warehouse
    /// sends 995 kg. Production requests 20 kg ("Hopper spill at start of run"; a ComplementaryAwaitingApproval
    /// notification), a Manager approves, the Warehouse sends 20 kg, so production storage holds 1,020 kg. Completion:
    /// 1,001 bags, 4 kg leftover, "Last bag line jammed". RequiredForActualOutput 1,001; ActualUsage 5 + 995 + 20 − 4 =
    /// 1,016 (ConsumedInProduction −1,016); production storage 4 kg; usage variance +15 kg, +1.50%, not above 2%; output
    /// variance +1 bag, +0.10%. The completion raises exactly 2 notifications, OutputVariance and LeftoverAtCompletion,
    /// with design 7.3's texts. Read status is per Manager (AC-23.3).
    /// </summary>
    [Fact]
    public async Task Example_A_within_tolerance_end_to_end()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await ExampleAbUpToCompletionAsync(app, time, complementary: 20m);

        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(40));
        var completion = await CompleteAsync(scene.Production.Client, 12, 1001m, JammedReason, (scene.Cheese.Id, 4m));

        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var order = await ApiJson.ReadAsync(completion);
        AssertCompletedOrder(order, produced: 1001m, outputVariance: 1m, outputPercent: 0.1m, reason: JammedReason);
        AssertCompletedLine(Line(order, scene.Cheese.Id), leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 20m,
            carried: 0m, entered: 4m, remaining: 4m, usage: 1016m, required: 1001m, variance: 15m, percent: 1.5m, above: false);
        AssertLastHistory(order, "Completed", "2026-11-11T15:40:00Z", scene.Production, JammedReason);

        // AC-19.2: the order's movements are the two sends, the consumption and the output, nothing else.
        var movements = await OrderMovementsAsync(scene.Manager.Client, 12);
        AssertMovementShapes(movements,
            ("SentToProduction", scene.Cheese.Id, -995m, 995m, scene.Warehouse),
            ("SentToProduction", scene.Cheese.Id, -20m, 20m, scene.Warehouse),
            ("ConsumedInProduction", scene.Cheese.Id, null, -1016m, scene.Production),
            ("ProducedOutput", scene.Product.Id, 1001m, null, scene.Production));
        Assert.Equal(scene.Complementary, movements[1].GetProperty("complementaryOrderNumber").GetInt32());

        // AC-19.3: production storage holds the leftover entered, all of it free leftover again.
        AssertStock(await StockAsync(scene.Warehouse.Client, scene.Cheese.Id), warehouse: 985m, production: 4m, committed: 0m, free: 4m);
        Assert.Equal(1001m, (await StockAsync(scene.Warehouse.Client, scene.Product.Id)).GetProperty("warehouse").GetDecimal());

        // AC-20.5 "Total: 2 notifications" from the completion, after the request's own (AC-16.1).
        var notifications = await OrderNotificationsAsync(scene.Manager.Client, 12);
        Assert.Equal(["ComplementaryAwaitingApproval", "OutputVariance", "LeftoverAtCompletion"], Types(notifications));
        AssertNotification(notifications[1], "OutputVariance", "2026-11-11T15:40:00Z",
            "Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).");
        AssertOutputData(notifications[1].GetProperty("data"), scene.Product.Id, ordered: 1000m, produced: 1001m, difference: 1m, percent: 0.1m);
        AssertNotification(notifications[2], "LeftoverAtCompletion", "2026-11-11T15:40:00Z",
            "Order #12 was completed with leftover in production storage: Cheese 4 kg. Reason: \"Last bag line jammed\"");
        var leftoverData = notifications[2].GetProperty("data");
        var material = Assert.Single(leftoverData.GetProperty("materials").EnumerateArray());
        Assert.Equal(scene.Cheese.Id, material.GetProperty("itemId").GetInt32());
        Assert.Equal(4m, material.GetProperty("leftover").GetDecimal());
        Assert.Equal(JammedReason, leftoverData.GetProperty("reason").GetString());

        // AC-23.3: Maria marks the output variance read; she sees it read, the start-up Manager still sees it unread.
        var outputId = notifications[1].GetProperty("id").GetInt32();
        var read = await scene.Manager.Client.PostAsync($"{ApiPath}/notifications/{outputId}/read", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.True((await GetAsync(scene.Manager.Client, $"{ApiPath}/notifications/{outputId}")).GetProperty("isRead").GetBoolean());
        Assert.False((await GetAsync(scene.Admin, $"{ApiPath}/notifications/{outputId}")).GetProperty("isRead").GetBoolean());

        await AssertScenarioConsistentAsync(scene.Admin, orders: 12, complementaryOrders: 1, returns: 0);
    }

    // ---- AC-20.6: worked example B ----------------------------------------------------------------------------------

    /// <summary>
    /// AC-20.6 (worked example B), AC-20.4, AC-23.2's example message, through HTTP from an empty database. As example A,
    /// but 30 kg is requested and sent, and the leftover entered is 0 kg with no reason. ActualUsage 5 + 995 + 30 − 0 =
    /// 1,030 kg; usage variance +29 kg, +2.90%, above 2%: one UsageVariance notification, "Order #12, Cheese: used 1,030
    /// kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%)."; output variance +1 bag: one OutputVariance;
    /// no LeftoverAtCompletion. ConsumedInProduction −1,030 kg empties production storage.
    /// </summary>
    [Fact]
    public async Task Example_B_above_tolerance_end_to_end()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await ExampleAbUpToCompletionAsync(app, time, complementary: 30m);

        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(40));
        var completion = await CompleteAsync(scene.Production.Client, 12, 1001m, reason: null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var order = await ApiJson.ReadAsync(completion);
        AssertCompletedOrder(order, produced: 1001m, outputVariance: 1m, outputPercent: 0.1m, reason: null);
        AssertCompletedLine(Line(order, scene.Cheese.Id), leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 30m,
            carried: 0m, entered: 0m, remaining: 0m, usage: 1030m, required: 1001m, variance: 29m, percent: 2.9m, above: true);

        var movements = await OrderMovementsAsync(scene.Manager.Client, 12);
        AssertMovementShapes(movements,
            ("SentToProduction", scene.Cheese.Id, -995m, 995m, scene.Warehouse),
            ("SentToProduction", scene.Cheese.Id, -30m, 30m, scene.Warehouse),
            ("ConsumedInProduction", scene.Cheese.Id, null, -1030m, scene.Production),
            ("ProducedOutput", scene.Product.Id, 1001m, null, scene.Production));
        AssertStock(await StockAsync(scene.Warehouse.Client, scene.Cheese.Id), warehouse: 975m, production: 0m, committed: 0m, free: 0m);

        // AC-20.6: 2 notifications from the completion (UsageVariance and OutputVariance), after the request's own.
        var notifications = await OrderNotificationsAsync(scene.Manager.Client, 12);
        Assert.Equal(["ComplementaryAwaitingApproval", "UsageVariance", "OutputVariance"], Types(notifications));
        AssertNotification(notifications[1], "UsageVariance", "2026-11-11T15:40:00Z",
            "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%).");
        var usageData = notifications[1].GetProperty("data");
        Assert.Equal(scene.Cheese.Id, usageData.GetProperty("itemId").GetInt32());
        Assert.Equal("Cheese", usageData.GetProperty("itemName").GetString());
        Assert.Equal("kg", usageData.GetProperty("unit").GetString());
        Assert.Equal(1001m, usageData.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(1030m, usageData.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(29m, usageData.GetProperty("difference").GetDecimal());
        Assert.Equal(2.9m, usageData.GetProperty("percent").GetDecimal());
        AssertNotification(notifications[2], "OutputVariance", "2026-11-11T15:40:00Z",
            "Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).");

        await AssertScenarioConsistentAsync(scene.Admin, orders: 12, complementaryOrders: 1, returns: 0);
    }

    // ---- AC-20.7: worked example C, then the leftover goes back to the Warehouse -------------------------------------

    /// <summary>
    /// AC-20.7 (worked example C), AC-05.2, AC-15.2, AC-22.1, AC-22.4, through HTTP from an empty database. Planned
    /// 1,000 kg, no leftover (range 980 to 1,020), send 1,000 kg, no complementary order, produced 1,000 bags, leftover
    /// 25 kg. The send and the completion are both made by one user holding Warehouse and Production (AC-05.2's own
    /// example). ActualUsage 975 kg; usage variance −25 kg, −2.50%: one UsageVariance notification; output variance 0:
    /// none; one LeftoverAtCompletion. Then a Manager instructs a return of the 25 kg (Pending: no stock moves, free
    /// leftover 0) and the Warehouse receives it: ReturnedToWarehouse W +25, P −25 linked to the order (decision #107),
    /// production storage 0, and the next order's leftover estimate for Cheese is 0 (AC-22.4).
    /// </summary>
    [Fact]
    public async Task Example_C_under_use_then_leftover_returned_end_to_end()
    {
        await using var app = new TestApiFactory();
        var setup = await MasterDataAsync(app);
        var both = await CreateStaffAsync(app, setup.Admin, "jorge.lucia@example.com", Roles.Warehouse, Roles.Production);
        await ReceiveAsync(setup.Warehouse.Client, setup.Cheese.Id, 1000m);
        var number = await CreateOrderAsync(setup.Manager.Client, setup.Product.Id, 1000m);
        AssertEstimate(Line(await ReadOrderAsync(both.Client, number), setup.Cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(both.Client, number, (setup.Cheese.Id, 1000m))).StatusCode);
        var completion = await CompleteAsync(both.Client, number, 1000m, "Scale drift at end of shift", (setup.Cheese.Id, 25m));

        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var order = await ApiJson.ReadAsync(completion);
        AssertCompletedOrder(order, produced: 1000m, outputVariance: 0m, outputPercent: 0m, reason: "Scale drift at end of shift");
        AssertCompletedLine(Line(order, setup.Cheese.Id), leftoverApplied: 0m, warehouseSend: 1000m, complementarySends: 0m,
            carried: 0m, entered: 25m, remaining: 25m, usage: 975m, required: 1000m, variance: -25m, percent: -2.5m, above: true);
        Assert.Equal(["Created", "InProduction", "Completed"], HistoryStatuses(order));
        Assert.All(order.GetProperty("history").EnumerateArray().Skip(1),
            entry => Assert.Equal(both.Email, entry.GetProperty("by").GetProperty("email").GetString()));
        AssertStock(await StockAsync(both.Client, setup.Cheese.Id), warehouse: 0m, production: 25m, committed: 0m, free: 25m);

        var notifications = await OrderNotificationsAsync(setup.Manager.Client, number);
        Assert.Equal(["UsageVariance", "LeftoverAtCompletion"], Types(notifications));
        Assert.Equal("Order #1, Cheese: used 975 kg, required 1,000 kg for actual output. Difference -25 kg (-2.50%).",
            notifications[0].GetProperty("message").GetString());
        Assert.Equal(
            "Order #1 was completed with leftover in production storage: Cheese 25 kg. Reason: \"Scale drift at end of shift\"",
            notifications[1].GetProperty("message").GetString());

        // AC-22.1: instructing the return moves no stock, but the 25 kg stops being free leftover.
        var returnNumber = await InstructedReturnAsync(setup.Manager.Client, setup.Cheese.Id, 25m, orderNumber: number);
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 0m, production: 25m, committed: 0m, free: 0m);
        Assert.Equal(25m, (await StockAsync(setup.Warehouse.Client, setup.Cheese.Id)).GetProperty("pendingReturns").GetDecimal());

        // AC-22.4: the Warehouse records receiving it.
        var received = await ReceiveReturnAsync(setup.Warehouse.Client, returnNumber);

        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var returnBody = await ApiJson.ReadAsync(received);
        Assert.Equal("Received", returnBody.GetProperty("status").GetString());
        Assert.Equal(["Pending", "Received"], HistoryStatuses(returnBody));
        Assert.Equal(setup.Manager.Email, returnBody.GetProperty("history")[0].GetProperty("by").GetProperty("email").GetString());
        Assert.Equal(setup.Warehouse.Email, returnBody.GetProperty("history")[1].GetProperty("by").GetProperty("email").GetString());
        var returned = (await OrderMovementsAsync(setup.Manager.Client, number)).Last();
        Assert.Equal("ReturnedToWarehouse", returned.GetProperty("type").GetString());
        Assert.Equal(25m, returned.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(-25m, returned.GetProperty("productionStorageChange").GetDecimal());
        Assert.Equal(returnNumber, returned.GetProperty("returnNumber").GetInt32());
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 25m, production: 0m, committed: 0m, free: 0m);

        var next = await CreateOrderAsync(setup.Manager.Client, setup.Product.Id, 1000m);
        AssertEstimate(Line(await ReadOrderAsync(setup.Warehouse.Client, next), setup.Cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);

        await AssertScenarioConsistentAsync(setup.Admin, orders: 2, complementaryOrders: 0, returns: 1);
    }

    // ---- AC-15.5 in full, with AC-20.8 (worked example D) ------------------------------------------------------------

    /// <summary>
    /// AC-15.5 as a whole scenario, AC-20.8 (worked example D), AC-15.4, AC-13.6, AC-21.2, through HTTP from an empty
    /// database. 50 kg of free Cheese leftover is in production storage (a Manager's adjustment); no order is
    /// InProduction; #7 (08:00, 30 bags), #8 (09:00, 1,000) and #9 (10:00, 500) are Created.
    /// <list type="bullet">
    /// <item>Estimates: #7 leftover 30, nominal 0, range 0 to 0.6; #8 20, 980, 960 to 1,000; #9 0, 500, 490 to 510.</item>
    /// <item>#8 is sent first with 980 kg: leftover applied 20 (not 50), carried leftover 30, production storage 1,030 kg.
    /// The 30 kg stays free and allocated to #7, whose estimate is unchanged.</item>
    /// <item>Example D: #8 completes with 1,000 bags and 30 kg leftover. LeftoverRemaining 30 − 30 = 0; ActualUsage 20 +
    /// 980 − 0 = 1,000; usage variance 0; ConsumedInProduction −1,000; production storage 30 kg, again allocated to #7.
    /// Only a LeftoverAtCompletion notification (no variance).</item>
    /// <item>Then #7 is sent with 0 kg (no movement, AC-15.4) and completed with 30 bags and no leftover: usage 30,
    /// variance 0, no notification, production storage 0. #9's estimate is still 0 / 500 / 490 to 510, and cancelling it
    /// while Created raises no notification (decision #104a).</item>
    /// </list>
    /// No order's finished product, ordered quantity or planned amounts changed on the way (AC-13.6).
    /// </summary>
    [Fact]
    public async Task Oldest_order_first_and_example_D_end_to_end()
    {
        var time = new FakeTimeProvider(EightAm.AddMinutes(-10));
        await using var app = new TestApiFactory { FakeTime = time };
        var setup = await MasterDataAsync(app);
        for (var i = 1; i <= 6; i++)
        {
            await CreateAndCancelAsync(setup, expectedNumber: i);
        }

        await ReceiveAsync(setup.Warehouse.Client, setup.Cheese.Id, 2000m);
        await AdjustAsync(setup.Manager.Client, setup.Cheese.Id, "ProductionStorage", 50m, "Leftover from an earlier run");

        time.SetUtcNow(EightAm);
        var created7 = await CreateOrderBodyAsync(setup, 30m, expectedNumber: 7);
        time.SetUtcNow(EightAm.AddHours(1));
        var created8 = await CreateOrderBodyAsync(setup, 1000m, expectedNumber: 8);
        time.SetUtcNow(EightAm.AddHours(2));
        var created9 = await CreateOrderBodyAsync(setup, 500m, expectedNumber: 9);
        Assert.Equal("2026-11-11T08:00:00Z", created7.GetProperty("createdAt").GetString());
        Assert.Equal("2026-11-11T09:00:00Z", created8.GetProperty("createdAt").GetString());
        Assert.Equal("2026-11-11T10:00:00Z", created9.GetProperty("createdAt").GetString());

        // AC-15.5, estimates.
        await AssertEstimatesAsync(setup, (7, 30m, 0m, 0m, 0.6m), (8, 20m, 980m, 960m, 1000m), (9, 0m, 500m, 490m, 510m));

        // AC-15.5, #8 before #7: only the 20 kg not allocated to #7 is applied.
        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(30));
        var sent8 = await SendAsync(setup.Warehouse.Client, 8, (setup.Cheese.Id, 980m));

        Assert.Equal(HttpStatusCode.OK, sent8.StatusCode);
        var line8 = Line(await ApiJson.ReadAsync(sent8), setup.Cheese.Id);
        Assert.Equal(20m, line8.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(980m, line8.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(30m, line8.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line8.GetProperty("estimate").ValueKind);
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 1020m, production: 1030m,
            committed: 1000m, free: 30m, committedOrder: 8);
        await AssertEstimatesAsync(setup, (7, 30m, 0m, 0m, 0.6m), (9, 0m, 500m, 490m, 510m));

        // AC-20.8, worked example D.
        time.SetUtcNow(EightAm.AddHours(6));
        var completed8 = await CompleteAsync(setup.Production.Client, 8, 1000m, "Leftover kept for order #7", (setup.Cheese.Id, 30m));

        Assert.Equal(HttpStatusCode.OK, completed8.StatusCode);
        var order8 = await ApiJson.ReadAsync(completed8);
        AssertCompletedOrder(order8, produced: 1000m, outputVariance: 0m, outputPercent: 0m, reason: "Leftover kept for order #7");
        AssertCompletedLine(Line(order8, setup.Cheese.Id), leftoverApplied: 20m, warehouseSend: 980m, complementarySends: 0m,
            carried: 30m, entered: 30m, remaining: 0m, usage: 1000m, required: 1000m, variance: 0m, percent: 0m, above: false);
        AssertMovementShapes(await OrderMovementsAsync(setup.Manager.Client, 8),
            ("SentToProduction", setup.Cheese.Id, -980m, 980m, setup.Warehouse),
            ("ConsumedInProduction", setup.Cheese.Id, null, -1000m, setup.Production),
            ("ProducedOutput", setup.Product.Id, 1000m, null, setup.Production));
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 1020m, production: 30m, committed: 0m, free: 30m);
        var notifications8 = await OrderNotificationsAsync(setup.Manager.Client, 8);
        Assert.Equal(["LeftoverAtCompletion"], Types(notifications8));
        Assert.Equal(
            "Order #8 was completed with leftover in production storage: Cheese 30 kg. Reason: \"Leftover kept for order #7\"",
            notifications8[0].GetProperty("message").GetString());

        // AC-20.8: "production storage after: 30 kg, which is again allocated to #7".
        await AssertEstimatesAsync(setup, (7, 30m, 0m, 0m, 0.6m), (9, 0m, 500m, 490m, 510m));

        // AC-15.4 shape: #7's leftover covers its need, so it is sent with 0 kg and no movement is created.
        time.SetUtcNow(EightAm.AddHours(6).AddMinutes(30));
        var sent7 = await SendAsync(setup.Warehouse.Client, 7, (setup.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, sent7.StatusCode);
        var line7 = Line(await ApiJson.ReadAsync(sent7), setup.Cheese.Id);
        Assert.Equal(30m, line7.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(0m, line7.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0m, line7.GetProperty("carriedLeftover").GetDecimal());
        Assert.Empty(await OrderMovementsAsync(setup.Manager.Client, 7));

        time.SetUtcNow(EightAm.AddHours(7));
        var completed7 = await CompleteAsync(setup.Production.Client, 7, 30m, reason: null, (setup.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, completed7.StatusCode);
        var order7 = await ApiJson.ReadAsync(completed7);
        AssertCompletedOrder(order7, produced: 30m, outputVariance: 0m, outputPercent: 0m, reason: null);
        AssertCompletedLine(Line(order7, setup.Cheese.Id), leftoverApplied: 30m, warehouseSend: 0m, complementarySends: 0m,
            carried: 0m, entered: 0m, remaining: 0m, usage: 30m, required: 30m, variance: 0m, percent: 0m, above: false);
        Assert.Empty(await OrderNotificationsAsync(setup.Manager.Client, 7));
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 1020m, production: 0m, committed: 0m, free: 0m);

        // #9 never had leftover to take; cancelling it while Created moves nothing and notifies no one (AC-21.2).
        await AssertEstimatesAsync(setup, (9, 0m, 500m, 490m, 510m));
        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(10));
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(setup.Manager.Client, 9, "Customer order withdrawn")).StatusCode);
        Assert.Empty(await OrderNotificationsAsync(setup.Manager.Client, 9));
        Assert.Empty(await OrderMovementsAsync(setup.Manager.Client, 9));

        // AC-13.6: what was fixed at creation is still the same on every order.
        foreach (var created in new[] { created7, created8, created9 })
        {
            var now = await ReadOrderAsync(setup.Manager.Client, created.GetProperty("number").GetInt32());
            Assert.Equal(created.GetProperty("finishedProductId").GetInt32(), now.GetProperty("finishedProductId").GetInt32());
            Assert.Equal(created.GetProperty("orderedQuantity").GetDecimal(), now.GetProperty("orderedQuantity").GetDecimal());
            Assert.Equal(Line(created, setup.Cheese.Id).GetProperty("plannedAmount").GetDecimal(),
                Line(now, setup.Cheese.Id).GetProperty("plannedAmount").GetDecimal());
        }

        await AssertScenarioConsistentAsync(setup.Admin, orders: 9, complementaryOrders: 0, returns: 0);
    }

    // ---- Set-up through the API -------------------------------------------------------------------------------------

    /// <summary>
    /// Design 3.2 to 3.4 from an empty database: the start-up Manager signs in and creates Maria (Manager), Jorge
    /// (Warehouse) and Lucia (Production) through <c>POST /users</c>; Maria creates the units kg and Bag, the raw material
    /// Cheese (kg) and the finished product "Shredded cheese 1 kg bag" (Bag), and saves its recipe of 1 kg Cheese per
    /// bag (AC-08.3). Nothing else exists yet: the item list has exactly these two.
    /// </summary>
    private static async Task<Setup> MasterDataAsync(TestApiFactory app)
    {
        var admin = await StartUpManagerAsync(app);
        var manager = await CreateStaffAsync(app, admin, "maria.lopez@example.com", Roles.Manager);
        var warehouse = await CreateStaffAsync(app, admin, "jorge.ibarra@example.com", Roles.Warehouse);
        var production = await CreateStaffAsync(app, admin, "lucia.mendez@example.com", Roles.Production);

        var kg = await Seed.CreateUnitAsync(manager.Client, "kg");
        var bag = await Seed.CreateUnitAsync(manager.Client, "Bag");
        var cheese = await Seed.CreateItemAsync(manager.Client, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager.Client, ItemType.FinishedProduct, bag, name: ProductName);
        await PutRecipeAsync(manager.Client, product.Id, (cheese.Id, 1m));

        // The database really started empty: these are the only items, and the start-up Manager plus three staff the only users.
        Assert.Equal(2, (await GetAsync(production.Client, $"{ApiPath}/items")).GetProperty("totalCount").GetInt32());
        Assert.Equal(4, (await GetAsync(admin, $"{ApiPath}/users")).GetProperty("totalCount").GetInt32());
        return new Setup(admin, manager, warehouse, production, cheese, product);
    }

    /// <summary>
    /// Examples A and B (AC-20.5, AC-20.6) up to the completion, through the API at design 5.7's times. Orders #1 to #11
    /// are created and cancelled (so the next is #12); the Warehouse receives 2,000 kg of Cheese; a Manager puts 5 kg of
    /// leftover into production storage by adjustment; Maria creates #12 at 08:00 for 1,000 bags; its estimate is 5 / 995
    /// / 975 to 1,015 (AC-15.3); Jorge sends 995 kg at 10:02; at 11:20 Lucia requests <paramref name="complementary"/> kg,
    /// raising a ComplementaryAwaitingApproval notification; Maria approves at 11:30; Jorge sends exactly that amount at
    /// 11:45. Each step's result is checked on the way.
    /// </summary>
    private static async Task<AbScene> ExampleAbUpToCompletionAsync(TestApiFactory app, FakeTimeProvider time, decimal complementary)
    {
        var setup = await MasterDataAsync(app);
        for (var i = 1; i <= 11; i++)
        {
            await CreateAndCancelAsync(setup, expectedNumber: i);
        }

        await ReceiveAsync(setup.Warehouse.Client, setup.Cheese.Id, 2000m);
        await AdjustAsync(setup.Manager.Client, setup.Cheese.Id, "ProductionStorage", 5m, "Leftover counted after the last run");
        var created = await CreateOrderBodyAsync(setup, 1000m, expectedNumber: 12);
        Assert.Equal(1000m, Line(created, setup.Cheese.Id).GetProperty("plannedAmount").GetDecimal());
        AssertEstimate(Line(await ReadOrderAsync(setup.Warehouse.Client, 12), setup.Cheese.Id), leftover: 5m, nominal: 995m, min: 975m, max: 1015m);

        // AC-15.3: the send of 995 kg is accepted; leftover applied 5 is fixed and carried leftover is 0.
        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(2));
        var sent = await SendAsync(setup.Warehouse.Client, 12, (setup.Cheese.Id, 995m));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var sentOrder = await ApiJson.ReadAsync(sent);
        Assert.Equal("InProduction", sentOrder.GetProperty("status").GetString());
        var sentLine = Line(sentOrder, setup.Cheese.Id);
        Assert.Equal(5m, sentLine.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(995m, sentLine.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0m, sentLine.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(0m, sentLine.GetProperty("complementarySends").GetDecimal());
        AssertLastHistory(sentOrder, "InProduction", "2026-11-11T10:02:00Z", setup.Warehouse, note: null);
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 1005m, production: 1000m,
            committed: 1000m, free: 0m, committedOrder: 12);

        // AC-16.1: Lucia's request is PendingApproval and raises a ComplementaryAwaitingApproval notification.
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(20));
        var requested = await RequestComplementaryAsync(setup.Production.Client, 12, SpillReason, (setup.Cheese.Id, complementary));
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
        var request = await ApiJson.ReadAsync(requested);
        var number = request.GetProperty("number").GetInt32();
        // Decision #109 (c): the first complementary order of a fresh database, so the message below names #1.
        Assert.Equal(1, number);
        Assert.Equal("PendingApproval", request.GetProperty("status").GetString());
        Assert.Equal(12, request.GetProperty("orderNumber").GetInt32());
        var awaiting = Assert.Single(await OrderNotificationsAsync(setup.Manager.Client, 12));
        AssertNotification(awaiting, "ComplementaryAwaitingApproval", "2026-11-11T11:20:00Z",
            $"Complementary order #{number} for order #12 is waiting for approval. lucia.mendez@example.com requested "
            + $"Cheese {complementary:0} kg. Reason: \"{SpillReason}\"");
        Assert.Equal(number, awaiting.GetProperty("complementaryOrderNumber").GetInt32());
        Assert.False(awaiting.GetProperty("isRead").GetBoolean());

        // AC-17.1, AC-18.1: Maria approves; Jorge sends exactly the requested amount; the parent line counts it (D-3).
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(setup.Manager.Client, number)).StatusCode);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(45));
        var complementarySent = await SendComplementaryAsync(setup.Warehouse.Client, number, (setup.Cheese.Id, complementary));
        Assert.Equal(HttpStatusCode.OK, complementarySent.StatusCode);
        var sentComplementary = await ApiJson.ReadAsync(complementarySent);
        Assert.Equal("Sent", sentComplementary.GetProperty("status").GetString());
        Assert.Equal(["PendingApproval", "Approved", "Sent"], HistoryStatuses(sentComplementary));
        Assert.Equal([setup.Production.Email, setup.Manager.Email, setup.Warehouse.Email],
            sentComplementary.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("by").GetProperty("email").GetString()));
        Assert.Equal(complementary, Line(await ReadOrderAsync(setup.Manager.Client, 12), setup.Cheese.Id).GetProperty("complementarySends").GetDecimal());

        // AC-20.5 / AC-20.6: "Production storage before completion" is 1,000 + the complementary send.
        AssertStock(await StockAsync(setup.Warehouse.Client, setup.Cheese.Id), warehouse: 1005m - complementary,
            production: 1000m + complementary, committed: 1000m + complementary, free: 0m, committedOrder: 12);
        return new AbScene(setup.Admin, setup.Manager, setup.Warehouse, setup.Production, setup.Cheese, setup.Product, number);
    }

    /// <summary>Creates an order for 1 bag and cancels it through the API, so the next order gets the next number.</summary>
    private static async Task CreateAndCancelAsync(Setup setup, int expectedNumber)
    {
        Assert.Equal(expectedNumber, await CreateOrderAsync(setup.Manager.Client, setup.Product.Id, 1m));
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(setup.Manager.Client, expectedNumber, "Not needed after all")).StatusCode);
    }

    /// <summary>Maria creates an order through <c>POST /production-orders</c> and gets the expected number; returns the 201 body.</summary>
    private static async Task<JsonElement> CreateOrderBodyAsync(Setup setup, decimal quantity, int expectedNumber)
    {
        var response = await setup.Manager.Client.PostAsJsonAsync(OrdersPath,
            new { finishedProductId = setup.Product.Id, orderedQuantity = quantity }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(expectedNumber, body.GetProperty("number").GetInt32());
        Assert.Equal(setup.Manager.Email, body.GetProperty("createdBy").GetProperty("email").GetString());
        return body;
    }

    /// <summary>AC-13.2: each listed Created order's Cheese estimate, as the Warehouse reads it now.</summary>
    private static async Task AssertEstimatesAsync(
        Setup setup, params (int Number, decimal Leftover, decimal Nominal, decimal Min, decimal Max)[] expected)
    {
        foreach (var (number, leftover, nominal, min, max) in expected)
        {
            var order = await ReadOrderAsync(setup.Warehouse.Client, number);
            Assert.Equal("Created", order.GetProperty("status").GetString());
            AssertEstimate(Line(order, setup.Cheese.Id), leftover, nominal, min, max);
        }
    }

    // ---- Assertions -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The two end-of-scenario checks (AC-05.3, AC-11.4 with PRD section 5), and the number of records each kind of
    /// audit read, so a scenario that silently created nothing fails.
    /// </summary>
    private static async Task AssertScenarioConsistentAsync(HttpClient manager, int orders, int complementaryOrders, int returns)
    {
        var audit = await AssertEveryChangeIsAttributedAsync(manager);
        Assert.Equal((orders, complementaryOrders, returns), (audit.Orders, audit.ComplementaryOrders, audit.Returns));
        Assert.True(audit.Movements > 0);
        Assert.Equal(2, await AssertMovementsAddUpToStockAsync(manager));
    }

    /// <summary>AC-14.2, AC-20.1, AC-20.10: the order-level completion figures, with the default tolerance of 2.</summary>
    private static void AssertCompletedOrder(JsonElement order, decimal produced, decimal outputVariance, decimal outputPercent, string? reason)
    {
        Assert.Equal("Completed", order.GetProperty("status").GetString());
        Assert.Equal(produced, order.GetProperty("producedQuantity").GetDecimal());
        Assert.Equal(outputVariance, order.GetProperty("outputVariance").GetDecimal());
        Assert.Equal(outputPercent, order.GetProperty("outputVariancePercent").GetDecimal());
        Assert.Equal(2m, order.GetProperty("toleranceAtCompletionPercent").GetDecimal());
        Assert.Equal(reason, order.GetProperty("completionReason").GetString());
        Assert.Equal(["Created", "InProduction", "Completed"], HistoryStatuses(order));
    }

    /// <summary>AC-14.2, AC-20.3, D-9: every figure of a completed line (design 5.7).</summary>
    private static void AssertCompletedLine(JsonElement line, decimal leftoverApplied, decimal warehouseSend,
        decimal complementarySends, decimal carried, decimal entered, decimal remaining, decimal usage, decimal required,
        decimal variance, decimal percent, bool above)
    {
        Assert.Equal(JsonValueKind.Null, line.GetProperty("estimate").ValueKind);
        Assert.Equal(leftoverApplied, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(warehouseSend, line.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(complementarySends, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(carried, line.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(entered, line.GetProperty("leftoverEntered").GetDecimal());
        Assert.Equal(remaining, line.GetProperty("leftoverRemaining").GetDecimal());
        Assert.Equal(usage, line.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(required, line.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(variance, line.GetProperty("usageVariance").GetDecimal());
        Assert.Equal(percent, line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(above, line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
    }

    /// <summary>AC-05.3, design 5.7: the last history entry: its status, UTC time, who, and note.</summary>
    private static void AssertLastHistory(JsonElement order, string status, string at, Staff by, string? note)
    {
        var last = order.GetProperty("history").EnumerateArray().Last();
        Assert.Equal(status, last.GetProperty("status").GetString());
        Assert.Equal(at, last.GetProperty("at").GetString());
        Assert.Equal(by.Id, last.GetProperty("by").GetProperty("id").GetString());
        Assert.Equal(by.Email, last.GetProperty("by").GetProperty("email").GetString());
        Assert.Equal(note, last.GetProperty("note").GetString());
    }

    /// <summary>
    /// AC-11.1, D-2, AC-05.3: exactly these movements, in this order: type, item, the signed change in each location
    /// (<c>null</c> where the location is not touched), and the user who made it.
    /// </summary>
    private static void AssertMovementShapes(List<JsonElement> movements,
        params (string Type, int ItemId, decimal? Warehouse, decimal? Production, Staff By)[] expected)
    {
        Assert.Equal(expected.Length, movements.Count);
        foreach (var (movement, shape) in movements.Zip(expected))
        {
            Assert.Equal(shape.Type, movement.GetProperty("type").GetString());
            Assert.Equal(shape.ItemId, movement.GetProperty("itemId").GetInt32());
            Assert.Equal(shape.Warehouse, NullableDecimal(movement.GetProperty("warehouseChange")));
            Assert.Equal(shape.Production, NullableDecimal(movement.GetProperty("productionStorageChange")));
            Assert.Equal(shape.By.Email, movement.GetProperty("user").GetProperty("email").GetString());
        }
    }

    /// <summary>
    /// AC-10.1, AC-10.3, design 5.5: an item's stock figures. <paramref name="committedOrder"/> is the InProduction order
    /// the material is committed to, or <c>null</c> when none is.
    /// </summary>
    private static void AssertStock(JsonElement stock, decimal warehouse, decimal production, decimal committed, decimal free,
        int? committedOrder = null)
    {
        Assert.Equal(warehouse, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(production, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(committed, stock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(free, stock.GetProperty("freeLeftover").GetDecimal());
        var order = stock.GetProperty("committedOrderNumber");
        Assert.Equal(committedOrder, order.ValueKind == JsonValueKind.Null ? null : order.GetInt32());
    }

    /// <summary>AC-23.1, design 5.10 and 7.3: a notification's type, creation time and exact message.</summary>
    private static void AssertNotification(JsonElement notification, string type, string createdAt, string message)
    {
        Assert.Equal(type, notification.GetProperty("type").GetString());
        Assert.Equal(createdAt, notification.GetProperty("createdAt").GetString());
        Assert.Equal(message, notification.GetProperty("message").GetString());
    }

    /// <summary>AC-20.2, design 5.10: the OutputVariance notification's structured values.</summary>
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

    private static IEnumerable<string?> Types(IEnumerable<JsonElement> notifications) =>
        notifications.Select(notification => notification.GetProperty("type").GetString()).ToList();

    private static IEnumerable<string?> HistoryStatuses(JsonElement record) =>
        record.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()).ToList();

    /// <summary>The start-up Manager and the three design 5 staff, the Cheese and the product with its recipe.</summary>
    private sealed record Setup(HttpClient Admin, Staff Manager, Staff Warehouse, Staff Production, CreatedItem Cheese, CreatedItem Product);

    /// <summary>Examples A and B up to the completion: who acts, the items, and the complementary order's number.</summary>
    private sealed record AbScene(
        HttpClient Admin, Staff Manager, Staff Warehouse, Staff Production, CreatedItem Cheese, CreatedItem Product, int Complementary);
}
