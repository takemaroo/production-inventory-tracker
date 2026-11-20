using System.Net;
using System.Net.Http.Json;
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
/// T-019: leftover returns (US-22, AC-22.1 to AC-22.6; AC-15.11 and AC-10.3 end to end; decisions #11, #18, #80, #107;
/// design 3.9, 5.9 and 7.2): <c>POST /api/v1/returns</c>, <c>GET /api/v1/returns</c>, <c>GET /api/v1/returns/{number}</c>,
/// <c>POST /api/v1/returns/{number}/receive</c> and <c>POST /api/v1/returns/{number}/cancel</c>.
/// </summary>
/// <remarks>
/// <para>
/// Returns are instructed, received and cancelled through the real endpoints. Free leftover reaches production storage
/// through a ledger Adjustment (<see cref="OrderFlow.PutLeftoverAsync"/>), and write-offs go through
/// <c>POST /adjustments</c>, so the stock behind every check is real movements.
/// </para>
/// <para>
/// Tests on the shared factory use their own new materials, so they never see each other's stock or returns. Tests that
/// need exact numbers, names or times, that send an order (only one can be InProduction per database, AC-15.10), or that
/// force an ordering between two requests create their own factory.
/// </para>
/// </remarks>
public class ReturnsTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The design 3.9 time of the instruction, 16:05 UTC on 11 November 2026.</summary>
    private static readonly DateTimeOffset FiveAfterFour = new(2026, 11, 11, 16, 5, 0, TimeSpan.Zero);

    /// <summary>AC-22.5, design 7.2: the end of every <c>not-pending</c> message.</summary>
    private const string OnlyPending = "Only a Pending return can be received or cancelled.";

    /// <summary>Decision #18, design 7.2: the end of the receive's <c>insufficient-stock</c> message.</summary>
    private const string CancelOrAdjust = "Cancel this return or adjust the stock first.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-22.1: instructing a return ----------------------------------------------------------------------------

    /// <summary>
    /// AC-22.1, AC-10.3, AC-05.3, D-4, design 3.9 and 5.9. Cheese (kg) has 20 kg of free leftover in production storage and
    /// 1,200 kg in the Warehouse; order #1 was cancelled. At 16:05 the Manager maria.lopez@example.com instructs a return
    /// of 20 kg linked to order #1, with a reason padded by spaces. <c>201</c>, <c>Location: /api/v1/returns/1</c>, and
    /// exactly the design 5.9 body: number 1, the material, its unit, 20, the trimmed reason, order 1, Pending, and a
    /// history of one Pending entry at 16:05 by Maria with no note. <c>GET</c> returns the same body. No stock moves (no
    /// movement names the return; Warehouse 1,200 and production storage 20 unchanged), but <c>GET /stock/{itemId}</c>
    /// now shows 20 in <c>pendingReturns</c> and free leftover 0 instead of 20. The instruction is logged.
    /// </summary>
    [Fact]
    public async Task Manager_instructs_a_return_and_it_stops_counting_as_free_leftover()
    {
        // Set up an hour earlier: well inside the 8-hour token lifetime (AC-01.2) of the users signed in now.
        var time = new FakeTimeProvider(FiveAfterFour.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var maria = await app.CreateUserAsync("maria.lopez@example.com", Roles.Manager);
        using var manager = app.CreateClientWithToken(await app.SignInAsync(maria.Email!));
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1200m);
        await PutLeftoverAsync(app, cheese.Id, 20m, maria.Id);
        var order = await CreateOrderAsync(manager, product.Id, 50m);
        await SetStatusAsync(app, order, OrderStatus.Cancelled, maria.Id);
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 1200m, production: 20m, pending: 0m, free: 20m);
        time.SetUtcNow(FiveAfterFour);

        var response = await InstructReturnAsync(manager, cheese.Id, 20m, "  Order #1 cancelled, cheese not needed this week  ", order);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/returns/1", response.Headers.Location?.AbsolutePath);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(["number", "itemId", "itemName", "unit", "quantity", "reason", "orderNumber", "status", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, body.GetProperty("number").GetInt32());
        Assert.Equal(cheese.Id, body.GetProperty("itemId").GetInt32());
        Assert.Equal("Cheese", body.GetProperty("itemName").GetString());
        Assert.Equal("kg", body.GetProperty("unit").GetString());
        Assert.Equal(20m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal("Order #1 cancelled, cheese not needed this week", body.GetProperty("reason").GetString());
        Assert.Equal(order, body.GetProperty("orderNumber").GetInt32());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        var entry = Assert.Single(body.GetProperty("history").EnumerateArray());
        AssertHistoryEntry(entry, "Pending", "2026-11-11T16:05:00Z", maria);

        Assert.Equal(body.GetRawText(), (await ReadReturnAsync(manager, 1)).GetRawText());
        Assert.Empty(await MovementsOfReturnAsync(app, 1));
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 1200m, production: 20m, pending: 20m, free: 0m);

        var stored = await StoredReturnAsync(app, 1);
        Assert.Equal((maria.Id, FiveAfterFour.UtcDateTime), (stored.CreatedById, stored.CreatedAt));
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Null(stored.ReceivedAt);
        Assert.Null(stored.CancelledAt);
        Assert.Contains(app.Logs.Entries, log => log.Level == LogLevel.Information
            && log.Message == $"Return 1 of item {cheese.Id} instructed by user {maria.Id}.");
    }

    /// <summary>
    /// AC-22.1, design 5.9: the order link is optional. Without it the return's <c>orderNumber</c> is <c>null</c>; with an
    /// order in any status (here a Created one) it is accepted.
    /// </summary>
    [Fact]
    public async Task Order_link_is_optional_and_may_name_an_order_in_any_status()
    {
        var scene = await LeftoverSceneAsync(factory, 10m);
        var (createdOrder, _, _) = await TwoLineOrderAsync(scene.Manager, receive: false);

        var unlinked = await ApiJson.ReadAsync(await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 4m));
        var linked = await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 6m, orderNumber: createdOrder);

        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("orderNumber").ValueKind);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        Assert.Equal(createdOrder, (await ApiJson.ReadAsync(linked)).GetProperty("orderNumber").GetInt32());
    }

    // ---- AC-22.2: only free leftover; the body ----------------------------------------------------------------------

    /// <summary>
    /// AC-22.2, D-4, design 7.2: with 20 of free leftover, 20.001 is refused with <c>409</c>
    /// <c>insufficient-free-leftover</c> "Not enough free leftover: {item} has 20 {unit} free in production storage."
    /// After a Pending return of 15, the free leftover is 5: 5.001 is refused ("has 5 … free") and exactly 5 is accepted.
    /// Then nothing is free ("has 0 … free"), and a material with no stock at all has 0 free too. Refusals save nothing.
    /// </summary>
    [Fact]
    public async Task Only_free_leftover_can_be_returned()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var cheese = scene.Cheese;

        await AssertFreeLeftoverRefusedAsync(await InstructReturnAsync(scene.Manager, cheese.Id, 20.001m), cheese, "20");
        await InstructedReturnAsync(scene.Manager, cheese.Id, 15m);
        await AssertFreeLeftoverRefusedAsync(await InstructReturnAsync(scene.Manager, cheese.Id, 5.001m), cheese, "5");
        await InstructedReturnAsync(scene.Manager, cheese.Id, 5m);
        await AssertFreeLeftoverRefusedAsync(await InstructReturnAsync(scene.Manager, cheese.Id, 0.001m), cheese, "0");

        var empty = await Seed.CreateItemAsync(scene.Manager, ItemType.RawMaterial);
        await AssertFreeLeftoverRefusedAsync(await InstructReturnAsync(scene.Manager, empty.Id, 1m), empty, "0");

        Assert.Equal(2, await ReturnCountAsync(factory, cheese.Id));
        Assert.Equal(0, await ReturnCountAsync(factory, empty.Id));
        AssertStock(await StockAsync(scene.Manager, cheese.Id), warehouse: 0m, production: 20m, pending: 20m, free: 0m);
    }

    /// <summary>
    /// AC-22.1, AC-22.2, decisions #80 and #95, design 2.6 and 7.2: each bad field is <c>400</c> <c>validation</c> with the
    /// design's message under its JSON path, and nothing is saved. A missing or blank reason (AC-22.2), a quantity that
    /// is missing, not above zero, has more than 3 decimal places or is above 999,999,999.999, an unknown or missing item,
    /// and an order number that names no order.
    /// </summary>
    [Theory]
    [InlineData("""{ "quantity": 1, "reason": "Spare" }""", "itemId", "Item is required.")]
    [InlineData("""{ "itemId": null, "quantity": 1, "reason": "Spare" }""", "itemId", "Item is required.")]
    [InlineData("""{ "itemId": 999999, "quantity": 1, "reason": "Spare" }""", "itemId", "Item 999999 does not exist.")]
    [InlineData("""{ "itemId": {item}, "reason": "Spare" }""", "quantity", "Quantity is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 0, "reason": "Spare" }""", "quantity", "Quantity must be greater than 0.")]
    [InlineData("""{ "itemId": {item}, "quantity": -1, "reason": "Spare" }""", "quantity", "Quantity must be greater than 0.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1.0001, "reason": "Spare" }""", "quantity", "Quantity can have at most 3 decimal places.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1000000000, "reason": "Spare" }""", "quantity", ExpectedMessages.QuantityAboveMax)]
    [InlineData("""{ "itemId": {item}, "quantity": 999999999.9991, "reason": "Spare" }""", "quantity", "Quantity can have at most 3 decimal places.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1 }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": null }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": "" }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": "   " }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": "{501}" }""", "reason", "Reason must be 500 characters or fewer.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": "Spare", "orderNumber": 999999 }""", "orderNumber", "Order #999999 does not exist.")]
    public async Task Invalid_instruction_is_rejected(string template, string path, string message)
    {
        var scene = await LeftoverSceneAsync(factory, 10m);
        var json = template.Replace("{item}", scene.Cheese.Id.ToString()).Replace("{501}", new string('x', 501));

        await ApiAssert.ValidationAsync(await PostReturnRawAsync(scene.Manager, json), path, message);

        Assert.Equal(0, await ReturnCountAsync(factory, scene.Cheese.Id));
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 0m, production: 10m, pending: 0m, free: 10m);
    }

    /// <summary>
    /// Design 2.2 step 4, design 6: every bad field is reported in one <c>400</c>, in body order, with <c>detail</c> the
    /// messages joined by a space.
    /// </summary>
    [Fact]
    public async Task Every_problem_is_reported_in_one_response()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await PostReturnRawAsync(manager,
            """{ "itemId": 999999, "quantity": 0, "reason": " ", "orderNumber": 999998 }""");

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Item 999999 does not exist. Quantity must be greater than 0. Reason is required. Order #999998 does not exist.");
        Assert.Equal(["itemId", "quantity", "reason", "orderNumber"], body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    /// <summary>
    /// Design 2.4 and 2.6, decision #80: values on the limits are accepted. 12.5000 counts as 12.5 (trailing zeros do not
    /// count) and is all the free leftover; a reason of exactly 500 characters after trimming is kept whole; and
    /// 999,999,999.999 passes the field check, so the answer is the free-leftover refusal, not <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Values_on_the_limits_are_accepted()
    {
        var scene = await LeftoverSceneAsync(factory, 12.5m);
        var reason = new string('r', 500);

        var response = await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 12.5000m, $"  {reason}  ");
        var largest = await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 999_999_999.999m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(12.5m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        await AssertFreeLeftoverRefusedAsync(largest, scene.Cheese, "0");
    }

    /// <summary>
    /// Design 7.2, decision #78: a finished product is <c>400</c> <c>return-not-raw-material</c> "Returns are for raw
    /// materials only."; a field error in the same body is reported first (<c>400</c> <c>validation</c>). Nothing saved.
    /// </summary>
    [Fact]
    public async Task Finished_product_cannot_be_returned()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        await ApiAssert.ProblemAsync(await InstructReturnAsync(manager, product.Id, 1m),
            HttpStatusCode.BadRequest, "return-not-raw-material", "Returns are for raw materials only.");
        await ApiAssert.ValidationAsync(await InstructReturnAsync(manager, product.Id, 0m),
            "quantity", "Quantity must be greater than 0.");

        Assert.Equal(0, await ReturnCountAsync(factory, product.Id));
    }

    /// <summary>
    /// Architecture 4.1 item 2: no body is <c>400</c> "A request body is required.", JSON that does not parse is "The
    /// request body is not valid JSON.", and a quantity sent as text is "quantity has the wrong type." under
    /// <c>quantity</c> (design 2.4). Nothing saved.
    /// </summary>
    [Fact]
    public async Task Unreadable_bodies_are_rejected()
    {
        var scene = await LeftoverSceneAsync(factory, 10m);

        await ApiAssert.ProblemAsync(await PostReturnRawAsync(scene.Manager, ""),
            HttpStatusCode.BadRequest, "validation", "A request body is required.");
        await ApiAssert.ProblemAsync(await PostReturnRawAsync(scene.Manager, "{ \"itemId\": "),
            HttpStatusCode.BadRequest, "validation", "The request body is not valid JSON.");
        await ApiAssert.ValidationAsync(await PostReturnRawAsync(scene.Manager,
                $$"""{ "itemId": {{scene.Cheese.Id}}, "quantity": "5", "reason": "Spare" }"""),
            "quantity", "quantity has the wrong type.");

        Assert.Equal(0, await ReturnCountAsync(factory, scene.Cheese.Id));
    }

    // ---- AC-22.3: not while the material is on the order in production -----------------------------------------------

    /// <summary>
    /// AC-22.3, decision #11, design 2.2 and 7.2. While an order using Cheese is InProduction (all 1,000 kg in production
    /// storage committed to it), a return of Cheese is <c>409</c> <c>material-in-use</c> "{item} is in use by order #N,
    /// which is in production. Return it after the order is completed or cancelled." even for more than the stock
    /// (step 5 comes before the free-leftover check, step 7), while a bad quantity is still <c>400</c> first (step 4). A
    /// material that is not on the running order can be returned. Once the order is cancelled (AC-21.3), its 1,000 kg is
    /// free leftover and can be returned.
    /// </summary>
    [Fact]
    public async Task Material_of_the_order_in_production_cannot_be_returned()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);
        var (_, managerUser) = await SignedInAsync(app, Roles.Manager);
        var other = await Seed.CreateItemAsync(scene.Manager, ItemType.RawMaterial);
        await PutLeftoverAsync(app, other.Id, 5m, managerUser.Id);
        var inUse = $"{scene.Cheese.Name} is in use by order #{scene.Number}, which is in production. " +
            "Return it after the order is completed or cancelled.";

        await ApiAssert.ProblemAsync(await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 1m),
            HttpStatusCode.Conflict, "material-in-use", inUse);
        await ApiAssert.ProblemAsync(await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 5000m),
            HttpStatusCode.Conflict, "material-in-use", inUse);
        await ApiAssert.ValidationAsync(await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 0m),
            "quantity", "Quantity must be greater than 0.");
        Assert.Equal(0, await ReturnCountAsync(app, scene.Cheese.Id));
        Assert.Equal(HttpStatusCode.Created, (await InstructReturnAsync(scene.Manager, other.Id, 5m)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(scene.Manager, scene.Number, "Line breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 1000m)).StatusCode);
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 0m, production: 1000m, pending: 1000m, free: 0m);
    }

    // ---- AC-22.4: receiving a return --------------------------------------------------------------------------------

    /// <summary>
    /// AC-22.4, AC-05.3, D-2, decisions #87 and #107, design 3.9, 5.6 and 5.9. Return #1 of 20 kg of Cheese, linked to
    /// cancelled order #1, instructed by Maria at 16:05, is received at 16:30 by the Warehouse user
    /// jorge.ibarra@example.com. <c>200</c>: Received, with the history Pending (16:05, Maria) then Received (16:30,
    /// Jorge); <c>GET</c> returns the same body. One ReturnedToWarehouse movement at 16:30 by Jorge: production storage
    /// −20, Warehouse +20, return #1 and order #1, which <c>GET /movements?orderNumber=1</c> lists. Production storage holds
    /// 0 kg, the Warehouse 1,220 kg, and nothing is pending or free. The receipt is logged.
    /// </summary>
    [Fact]
    public async Task Warehouse_user_receives_a_return_and_the_stock_moves()
    {
        // Set up an hour earlier: well inside the 8-hour token lifetime (AC-01.2) of the users signed in now.
        var time = new FakeTimeProvider(FiveAfterFour.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var maria = await app.CreateUserAsync("maria.lopez@example.com", Roles.Manager);
        using var manager = app.CreateClientWithToken(await app.SignInAsync(maria.Email!));
        var jorge = await app.CreateUserAsync("jorge.ibarra@example.com", Roles.Warehouse);
        using var warehouse = app.CreateClientWithToken(await app.SignInAsync(jorge.Email!));
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1200m);
        await PutLeftoverAsync(app, cheese.Id, 20m, maria.Id);
        var order = await CreateOrderAsync(manager, product.Id, 50m);
        await SetStatusAsync(app, order, OrderStatus.Cancelled, maria.Id);
        time.SetUtcNow(FiveAfterFour);
        Assert.Equal(1, await InstructedReturnAsync(manager, cheese.Id, 20m, order));
        time.SetUtcNow(FiveAfterFour.AddMinutes(25));

        var response = await ReceiveReturnAsync(warehouse, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal("Received", body.GetProperty("status").GetString());
        Assert.Equal(20m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal(order, body.GetProperty("orderNumber").GetInt32());
        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        AssertHistoryEntry(history[0], "Pending", "2026-11-11T16:05:00Z", maria);
        AssertHistoryEntry(history[1], "Received", "2026-11-11T16:30:00Z", jorge);
        Assert.Equal(body.GetRawText(), (await ReadReturnAsync(manager, 1)).GetRawText());

        var movement = Assert.Single(await MovementsOfReturnAsync(app, 1));
        Assert.Equal(MovementType.ReturnedToWarehouse, movement.Type);
        Assert.Equal(cheese.Id, movement.ItemId);
        Assert.Equal(20m, movement.WarehouseChange);
        Assert.Equal(-20m, movement.ProductionStorageChange);
        Assert.Equal((jorge.Id, FiveAfterFour.AddMinutes(25).UtcDateTime), (movement.UserId, movement.OccurredAt));
        Assert.Equal(order, movement.ProductionOrderNumber);
        var listed = await manager.GetAsync($"/api/v1/movements?orderNumber={order}", Cancel);
        var row = Assert.Single((await ApiJson.ReadAsync(listed)).GetProperty("items").EnumerateArray());
        Assert.Equal("ReturnedToWarehouse", row.GetProperty("type").GetString());
        Assert.Equal(1, row.GetProperty("returnNumber").GetInt32());

        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 1220m, production: 0m, pending: 0m, free: 0m);
        var stored = await StoredReturnAsync(app, 1);
        Assert.Equal(jorge.Id, stored.ReceivedById);
        Assert.Equal(FiveAfterFour.AddMinutes(25).UtcDateTime, stored.ReceivedAt);
        Assert.Null(stored.CancelledAt);
        Assert.Contains(app.Logs.Entries, log => log.Level == LogLevel.Information
            && log.Message == $"Return 1 received in the Warehouse by user {jorge.Id}.");
    }

    /// <summary>
    /// AC-22.4, decision #107, PRD 4.2: a Manager may record receiving a return too, and a return with no order link gives
    /// a ReturnedToWarehouse movement with no order number.
    /// </summary>
    [Fact]
    public async Task Manager_receives_an_unlinked_return()
    {
        var scene = await LeftoverSceneAsync(factory, 8m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 8m);

        var response = await ReceiveReturnAsync(scene.Manager, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = Assert.Single(await MovementsOfReturnAsync(factory, number));
        Assert.Null(movement.ProductionOrderNumber);
        Assert.Equal(scene.ManagerUser.Id, movement.UserId);
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 8m, production: 0m, pending: 0m, free: 0m);
    }

    /// <summary>
    /// AC-22.4's example, D-4, D-5, AC-13.2: 20 kg of Cheese leftover; a Created order planning 1,000 kg shows leftover
    /// applied 20, nominal send 980, range 960 to 1,000. Instructing a return of the 20 kg already drops the estimate to
    /// leftover 0, nominal 1,000, range 980 to 1,020 (Pending is not free). After the return is received, production
    /// storage holds 0 kg, the Warehouse has gained 20 kg, and the next order's estimate for Cheese is still 0.
    /// </summary>
    [Fact]
    public async Task Returned_leftover_is_no_longer_in_the_next_orders_estimate()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var product = await ProductWithRecipeAsync(scene.Manager, (scene.Cheese.Id, 1m));
        var order = await CreateOrderAsync(scene.Manager, product.Id, 1000m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Manager, order), scene.Cheese.Id), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);

        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m, order);
        AssertEstimate(Line(await ReadOrderAsync(scene.Manager, order), scene.Cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);

        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(scene.Warehouse, number)).StatusCode);
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 20m, production: 0m, pending: 0m, free: 0m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Manager, order), scene.Cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);
    }

    /// <summary>
    /// Decision #18, design 7.2 and section 9 question 2, AC-12.3: a Pending return of 20 kg does not block a write-off
    /// (−5 kg in production storage, <c>201</c>), so production storage now holds less than the return. Receiving it is
    /// <c>409</c> <c>insufficient-stock</c> "Not enough stock: {item} in Production storage has 15 {unit}. Cancel this
    /// return or adjust the stock first." and nothing moves; the return stays Pending. The Manager cancels it and
    /// instructs a new one of 15 kg, which is received.
    /// </summary>
    [Fact]
    public async Task Return_larger_than_production_storage_cannot_be_received()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var cheese = scene.Cheese;
        var number = await InstructedReturnAsync(scene.Manager, cheese.Id, 20m);
        Assert.Equal(HttpStatusCode.Created, (await WriteOffAsync(scene.Manager, cheese.Id, -5m)).StatusCode);

        var refused = await ReceiveReturnAsync(scene.Warehouse, number);

        await ApiAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {cheese.Name} in Production storage has 15 {cheese.UnitName}. {CancelOrAdjust}");
        Assert.Equal(ReturnStatus.Pending, (await StoredReturnAsync(factory, number)).Status);
        Assert.Empty(await MovementsOfReturnAsync(factory, number));
        AssertStock(await StockAsync(scene.Manager, cheese.Id), warehouse: 0m, production: 15m, pending: 20m, free: 0m);

        Assert.Equal(HttpStatusCode.OK, (await CancelReturnAsync(scene.Manager, number)).StatusCode);
        var replacement = await InstructedReturnAsync(scene.Manager, cheese.Id, 15m);
        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(scene.Warehouse, replacement)).StatusCode);
        AssertStock(await StockAsync(scene.Manager, cheese.Id), warehouse: 15m, production: 0m, pending: 0m, free: 0m);
    }

    // ---- AC-22.5: cancelling, and only Pending returns ----------------------------------------------------------------

    /// <summary>
    /// AC-22.5, AC-05.3, D-4, design 3.9 step 4. Maria instructs a return of all 20 kg at 16:05 (free leftover 0); at 16:20
    /// another Manager cancels it. <c>200</c>: Cancelled, with the history Pending (16:05, Maria) then Cancelled (16:20, the
    /// other Manager); <c>GET</c> returns the same body. No movement; the 20 kg is free leftover again. Receiving or
    /// cancelling it again is <c>409</c> <c>not-pending</c> "Return #1 is Cancelled. Only a Pending return can be received
    /// or cancelled.", and still nothing moves. The cancellation is logged.
    /// </summary>
    [Fact]
    public async Task Manager_cancels_a_pending_return_and_the_leftover_is_free_again()
    {
        var time = new FakeTimeProvider(FiveAfterFour);
        await using var app = new TestApiFactory { FakeTime = time };
        var maria = await app.CreateUserAsync("maria.lopez@example.com", Roles.Manager);
        using var manager = app.CreateClientWithToken(await app.SignInAsync(maria.Email!));
        var (otherManager, otherUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await PutLeftoverAsync(app, cheese.Id, 20m, maria.Id);
        Assert.Equal(1, await InstructedReturnAsync(manager, cheese.Id, 20m));
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 0m, production: 20m, pending: 20m, free: 0m);
        time.SetUtcNow(FiveAfterFour.AddMinutes(15));

        var response = await CancelReturnAsync(otherManager, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        AssertHistoryEntry(history[0], "Pending", "2026-11-11T16:05:00Z", maria);
        AssertHistoryEntry(history[1], "Cancelled", "2026-11-11T16:20:00Z", otherUser);
        Assert.Equal(body.GetRawText(), (await ReadReturnAsync(manager, 1)).GetRawText());
        Assert.Empty(await MovementsOfReturnAsync(app, 1));
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 0m, production: 20m, pending: 0m, free: 20m);
        var stored = await StoredReturnAsync(app, 1);
        Assert.Equal(otherUser.Id, stored.CancelledById);
        Assert.Equal(FiveAfterFour.AddMinutes(15).UtcDateTime, stored.CancelledAt);
        Assert.Null(stored.ReceivedAt);
        Assert.Contains(app.Logs.Entries, log => log.Level == LogLevel.Information
            && log.Message == $"Return 1 cancelled by user {otherUser.Id}.");

        await ApiAssert.ProblemAsync(await ReceiveReturnAsync(warehouse, 1), HttpStatusCode.Conflict, "not-pending",
            $"Return #1 is Cancelled. {OnlyPending}");
        await ApiAssert.ProblemAsync(await CancelReturnAsync(manager, 1), HttpStatusCode.Conflict, "not-pending",
            $"Return #1 is Cancelled. {OnlyPending}");
        Assert.Empty(await MovementsOfReturnAsync(app, 1));
        Assert.Equal(otherUser.Id, (await StoredReturnAsync(app, 1)).CancelledById);
    }

    /// <summary>
    /// AC-22.5, AC-11.3: a Received return cannot be received again or cancelled: both are <c>409</c> <c>not-pending</c>
    /// "Return #N is Received. Only a Pending return can be received or cancelled.". The material moved exactly once.
    /// </summary>
    [Fact]
    public async Task Received_return_cannot_be_received_again_or_cancelled()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(scene.Warehouse, number)).StatusCode);

        await ApiAssert.ProblemAsync(await ReceiveReturnAsync(scene.Warehouse, number), HttpStatusCode.Conflict, "not-pending",
            $"Return #{number} is Received. {OnlyPending}");
        await ApiAssert.ProblemAsync(await CancelReturnAsync(scene.Manager, number), HttpStatusCode.Conflict, "not-pending",
            $"Return #{number} is Received. {OnlyPending}");

        Assert.Single(await MovementsOfReturnAsync(factory, number));
        var stored = await StoredReturnAsync(factory, number);
        Assert.Equal(ReturnStatus.Received, stored.Status);
        Assert.Null(stored.CancelledAt);
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 10m, production: 10m, pending: 0m, free: 10m);
    }

    /// <summary>
    /// Design 7.2, design 2.2 step 3: an unknown number is <c>404</c> "Return #999999 not found." on view, receive and
    /// cancel.
    /// </summary>
    [Fact]
    public async Task Unknown_return_is_404()
    {
        var scene = await LeftoverSceneAsync(factory, 0m);
        const string NotFound = "Return #999999 not found.";

        await ApiAssert.ProblemAsync(await scene.Manager.GetAsync($"{ReturnsPath}/999999", Cancel), HttpStatusCode.NotFound, "not-found", NotFound);
        await ApiAssert.ProblemAsync(await ReceiveReturnAsync(scene.Warehouse, 999999), HttpStatusCode.NotFound, "not-found", NotFound);
        await ApiAssert.ProblemAsync(await CancelReturnAsync(scene.Manager, 999999), HttpStatusCode.NotFound, "not-found", NotFound);
    }

    /// <summary>
    /// Design 2.2: on receive the state (step 5) comes before the stock (step 7). A cancelled return whose material has
    /// since been written off entirely is <c>409</c> <c>not-pending</c>, not <c>insufficient-stock</c>.
    /// </summary>
    [Fact]
    public async Task Receive_checks_the_state_before_the_stock()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);
        Assert.Equal(HttpStatusCode.OK, (await CancelReturnAsync(scene.Manager, number)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WriteOffAsync(scene.Manager, scene.Cheese.Id, -20m)).StatusCode);

        await ApiAssert.ProblemAsync(await ReceiveReturnAsync(scene.Warehouse, number), HttpStatusCode.Conflict, "not-pending",
            $"Return #{number} is Cancelled. {OnlyPending}");
    }

    // ---- AC-15.11 and AC-10.3 end to end ---------------------------------------------------------------------------

    /// <summary>
    /// AC-15.11 through HTTP, decision #11, AC-22.4, D-6. Cheese has 10 kg of free leftover and 1,000 kg in the Warehouse;
    /// a Created order plans 1,000 kg. The Manager instructs a return of the 10 kg: the send is <c>409</c>
    /// <c>pending-return</c> "Return #1 of Cheese is waiting to be received by the warehouse. Record it first." and the
    /// order stays Created. The warehouse receives the return (production storage 0, Warehouse 1,010), and the same send
    /// of 1,000 succeeds with leftover applied 0 and warehouse send 1,000. A Received return no longer blocks.
    /// </summary>
    [Fact]
    public async Task Pending_return_blocks_the_send_until_it_is_received()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        await PutLeftoverAsync(app, cheese.Id, 10m, managerUser.Id);
        var order = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(1, await InstructedReturnAsync(manager, cheese.Id, 10m));

        var blocked = await SendAsync(warehouse, order, (cheese.Id, 1000m));

        await ApiAssert.ProblemAsync(blocked, HttpStatusCode.Conflict, "pending-return",
            "Return #1 of Cheese is waiting to be received by the warehouse. Record it first.");
        Assert.Equal(OrderStatus.Created, (await StoredOrderAsync(app, order)).Status);

        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(warehouse, 1)).StatusCode);
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 1010m, production: 0m, pending: 0m, free: 0m);
        var sent = await SendAsync(warehouse, order, (cheese.Id, 1000m));

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var line = Line(await ApiJson.ReadAsync(sent), cheese.Id);
        Assert.Equal(0m, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(1000m, line.GetProperty("warehouseSend").GetDecimal());
    }

    // ---- Lists ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PRD 4.1, design 2.5 and 5.9: <c>GET /returns</c> lists newest first (by instruction time, then number), with the
    /// envelope and paging, and the filters <c>status</c> (any letter case) and <c>itemId</c>, combined with AND. Returns
    /// #1 (Cheese, 08:00, Pending), #2 (Starch, 08:01, Pending), #3 (Cheese, 08:02, Received) and #4 (Cheese, 08:02,
    /// Cancelled). Each row is the full design 5.9 shape, the same as <c>GET /returns/{number}</c>. An item id that
    /// matches nothing, even one that does not exist, gives an empty page.
    /// </summary>
    [Fact]
    public async Task Returns_are_listed_newest_first_with_filters_and_paging()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await LeftoverSceneAsync(app, 100m);
        var cheese = scene.Cheese;
        var starch = await Seed.CreateItemAsync(scene.Manager, ItemType.RawMaterial);
        await PutLeftoverAsync(app, starch.Id, 100m, scene.ManagerUser.Id);
        var manager = scene.Manager;
        Assert.Equal(1, await InstructedReturnAsync(manager, cheese.Id, 10m));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, await InstructedReturnAsync(manager, starch.Id, 1m));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, await InstructedReturnAsync(manager, cheese.Id, 5m));
        Assert.Equal(4, await InstructedReturnAsync(manager, cheese.Id, 2m));
        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(scene.Warehouse, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CancelReturnAsync(manager, 4)).StatusCode);
        var (production, _) = await SignedInAsync(app, Roles.Production);

        var all = await ListReturnsAsync(production, "");
        Assert.Equal([4, 3, 2, 1], Numbers(all));
        Assert.Equal((1, 50, 4), (all.GetProperty("page").GetInt32(), all.GetProperty("pageSize").GetInt32(), all.GetProperty("totalCount").GetInt32()));
        foreach (var row in all.GetProperty("items").EnumerateArray())
        {
            Assert.Equal((await ReadReturnAsync(production, row.GetProperty("number").GetInt32())).GetRawText(), row.GetRawText());
        }

        Assert.Equal([2, 1], Numbers(await ListReturnsAsync(production, "status=Pending")));
        Assert.Equal([3], Numbers(await ListReturnsAsync(production, "status=received")));
        Assert.Equal([4], Numbers(await ListReturnsAsync(production, "status=CANCELLED")));
        Assert.Equal([4, 3, 1], Numbers(await ListReturnsAsync(production, $"itemId={cheese.Id}")));
        Assert.Equal([1], Numbers(await ListReturnsAsync(production, $"itemId={cheese.Id}&status=Pending")));
        Assert.Equal([2], Numbers(await ListReturnsAsync(production, $"status=Pending&itemId={starch.Id}")));
        var none = await ListReturnsAsync(production, "itemId=999999");
        Assert.Empty(Numbers(none));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());

        var firstPage = await ListReturnsAsync(production, "pageSize=2");
        Assert.Equal([4, 3], Numbers(firstPage));
        Assert.Equal(4, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal([2, 1], Numbers(await ListReturnsAsync(production, "page=2&pageSize=2")));
        Assert.Empty(Numbers(await ListReturnsAsync(production, "page=3&pageSize=2")));
    }

    /// <summary>
    /// Design 2.5 and 7.2: an unknown status is <c>400</c> "status must be Pending, Received or Cancelled." under
    /// <c>status</c>; bad paging values get the shared messages; problems in several query values are reported together.
    /// </summary>
    [Fact]
    public async Task Bad_list_queries_are_rejected()
    {
        var client = await factory.CreateClientAsAsync(Roles.Warehouse);

        await ApiAssert.ValidationAsync(await client.GetAsync($"{ReturnsPath}?status=Done", Cancel),
            "status", "status must be Pending, Received or Cancelled.");
        await ApiAssert.ValidationAsync(await client.GetAsync($"{ReturnsPath}?pageSize=0", Cancel),
            "pageSize", "pageSize must be between 1 and 100.");
        await ApiAssert.ValidationAsync(await client.GetAsync($"{ReturnsPath}?page=0", Cancel),
            "page", "page must be 1 or greater.");
        var both = await ApiAssert.ProblemAsync(await client.GetAsync($"{ReturnsPath}?pageSize=101&status=Done", Cancel),
            HttpStatusCode.BadRequest, "validation",
            "pageSize must be between 1 and 100. status must be Pending, Received or Cancelled.");
        Assert.Equal(["pageSize", "status"], both.GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    // ---- Races (PRD section 5, architecture 5.2) --------------------------------------------------------------------

    /// <summary>
    /// AC-15.11, AC-22.3, decision #106 (c), forced order "instruct, then send": a send of an order using Cheese, arriving
    /// while the instruction holds the write lock, waits; then it finds the Pending return and gets <c>409</c>
    /// <c>pending-return</c>. The order stays Created and nothing moves.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_an_instruction_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await SendSceneAsync(race.App);

        var (instruct, send) = await race.RunAsync(
            () => InstructReturnAsync(scene.Manager, scene.Cheese.Id, 10m),
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 990m)));

        Assert.Equal(HttpStatusCode.Created, instruct.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "pending-return",
            $"Return #1 of {scene.Cheese.Name} is waiting to be received by the warehouse. Record it first.");
        Assert.Equal(OrderStatus.Created, (await StoredOrderAsync(race.App, scene.Number)).Status);
        Assert.Empty(await MovementsOfOrderAsync(race.App, scene.Number));
    }

    /// <summary>
    /// AC-22.3, D-6, forced order "send, then instruct": an instruction arriving while the send holds the write lock waits;
    /// by then the order is InProduction with the 10 kg of leftover applied, so the instruction gets <c>409</c>
    /// <c>material-in-use</c>. No return is stored, and the 1,000 kg in production storage is all committed.
    /// </summary>
    [Fact]
    public async Task Instruction_waiting_behind_a_send_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await SendSceneAsync(race.App);

        var (send, instruct) = await race.RunAsync(
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 990m)),
            () => InstructReturnAsync(scene.Manager, scene.Cheese.Id, 10m));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(10m, Line(await ApiJson.ReadAsync(send), scene.Cheese.Id).GetProperty("leftoverApplied").GetDecimal());
        await ApiAssert.ProblemAsync(instruct, HttpStatusCode.Conflict, "material-in-use",
            $"{scene.Cheese.Name} is in use by order #{scene.Number}, which is in production. Return it after the order is completed or cancelled.");
        Assert.Equal(0, await ReturnCountAsync(race.App, scene.Cheese.Id));
        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal((1000m, 1000m, 0m), (Figure(stock, "productionStorage"), Figure(stock, "committedToOrder"), Figure(stock, "freeLeftover")));
    }

    /// <summary>
    /// AC-22.2, D-4, architecture 5.2, forced order "instruct, then instruct": with 20 of free leftover, a second
    /// instruction of 15 arriving while the first (also 15) holds the write lock waits; then it counts the first one's
    /// Pending return, so only 5 is free and it gets <c>409</c> <c>insufficient-free-leftover</c> "… has 5 {unit} free in
    /// production storage." Together they never exceed the free leftover. This proves the free-leftover check runs inside
    /// the write transaction: read before it, both would see 20 free and both would be accepted. The interceptor waits on
    /// <c>Items</c>, the first table the instruction reads inside its transaction.
    /// </summary>
    [Fact]
    public async Task Instruction_waiting_behind_an_instruction_counts_its_pending_return()
    {
        await using var race = new ForcedOrder("Items");
        var scene = await LeftoverSceneAsync(race.App, 20m);

        var (first, second) = await race.RunAsync(
            () => InstructReturnAsync(scene.Manager, scene.Cheese.Id, 15m),
            () => InstructReturnAsync(scene.Manager, scene.Cheese.Id, 15m));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertFreeLeftoverRefusedAsync(second, scene.Cheese, "5");
        Assert.Equal(1, await ReturnCountAsync(race.App, scene.Cheese.Id));
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 0m, production: 20m, pending: 15m, free: 5m);
    }

    /// <summary>
    /// Decision #18, forced order "write off, then receive": a receive arriving while a −5 kg write-off of the same
    /// material holds the write lock waits; then production storage holds 15 kg, less than the 20 kg return, so the
    /// receive gets <c>409</c> <c>insufficient-stock</c> "… has 15 {unit}. Cancel this return or adjust the stock
    /// first." The return stays Pending and nothing moves for it.
    /// </summary>
    [Fact]
    public async Task Receive_waiting_behind_a_write_off_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await LeftoverSceneAsync(race.App, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

        var (writeOff, receive) = await race.RunAsync(
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, -5m),
            () => ReceiveReturnAsync(scene.Warehouse, number));

        Assert.Equal(HttpStatusCode.Created, writeOff.StatusCode);
        await ApiAssert.ProblemAsync(receive, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Production storage has 15 {scene.Cheese.UnitName}. {CancelOrAdjust}");
        Assert.Equal(ReturnStatus.Pending, (await StoredReturnAsync(race.App, number)).Status);
        Assert.Empty(await MovementsOfReturnAsync(race.App, number));
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 0m, production: 15m, pending: 20m, free: 0m);
    }

    /// <summary>
    /// AC-12.3, decision #18, forced order "receive, then write off": a −5 kg write-off arriving while the receive holds
    /// the write lock waits; by then production storage is empty, so the write-off gets <c>409</c>
    /// <c>insufficient-stock</c> "Not enough stock: {item} in Production storage has 0 {unit}." Stock is never negative.
    /// </summary>
    [Fact]
    public async Task Write_off_waiting_behind_a_receive_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await LeftoverSceneAsync(race.App, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

        var (receive, writeOff) = await race.RunAsync(
            () => ReceiveReturnAsync(scene.Warehouse, number),
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, -5m));

        Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
        await ApiAssert.ProblemAsync(writeOff, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Production storage has 0 {scene.Cheese.UnitName}.");
        Assert.Single(await MovementsOfReturnAsync(race.App, number));
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 20m, production: 0m, pending: 0m, free: 0m);
    }

    /// <summary>
    /// AC-22.5, architecture 5.2, forced order: a second receive of the same return, arriving while the first holds the
    /// write lock, waits, then finds Received and gets <c>409</c> <c>not-pending</c>. One movement; the first receiver is
    /// recorded.
    /// </summary>
    [Fact]
    public async Task Receive_waiting_behind_a_receive_is_refused()
    {
        await using var race = new ForcedOrder("Returns");
        var scene = await LeftoverSceneAsync(race.App, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

        var (first, second) = await race.RunAsync(
            () => ReceiveReturnAsync(scene.Warehouse, number),
            () => ReceiveReturnAsync(scene.Manager, number));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ApiAssert.ProblemAsync(second, HttpStatusCode.Conflict, "not-pending", $"Return #{number} is Received. {OnlyPending}");
        Assert.Single(await MovementsOfReturnAsync(race.App, number));
        Assert.Equal(scene.WarehouseUser.Id, (await StoredReturnAsync(race.App, number)).ReceivedById);
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 20m, production: 0m, pending: 0m, free: 0m);
    }

    /// <summary>
    /// AC-22.5, architecture 5.2: two Warehouse users receive the same return at the same moment. Exactly one gets
    /// <c>200</c>; the other gets <c>409</c> "Return #N is Received. …". One movement, the material moved once, and the
    /// stored receiver is the winner. Three rounds.
    /// </summary>
    [Fact]
    public async Task Simultaneous_receives_move_the_material_once()
    {
        var scene = await LeftoverSceneAsync(factory, 0m);
        var (first, firstUser) = await SignedInAsync(factory, Roles.Warehouse);
        var (second, secondUser) = await SignedInAsync(factory, Roles.Warehouse);

        for (var round = 1; round <= 3; round++)
        {
            await PutLeftoverAsync(factory, scene.Cheese.Id, 20m, scene.ManagerUser.Id);
            var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

            var responses = await Task.WhenAll(ReceiveReturnAsync(first, number), ReceiveReturnAsync(second, number));

            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(responses[1 - winner], HttpStatusCode.Conflict, "not-pending",
                $"Return #{number} is Received. {OnlyPending}");
            Assert.Single(await MovementsOfReturnAsync(factory, number));
            Assert.Equal(winner == 0 ? firstUser.Id : secondUser.Id, (await StoredReturnAsync(factory, number)).ReceivedById);
            AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 20m * round, production: 0m, pending: 0m, free: 0m);
        }
    }

    /// <summary>
    /// AC-22.5, forced order "receive, then cancel": the cancellation waits, then finds Received and gets <c>409</c>
    /// "Return #N is Received. …". The return stays Received with its one movement.
    /// </summary>
    [Fact]
    public async Task Cancel_waiting_behind_a_receive_is_refused()
    {
        await using var race = new ForcedOrder("Returns");
        var scene = await LeftoverSceneAsync(race.App, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

        var (receive, cancel) = await race.RunAsync(
            () => ReceiveReturnAsync(scene.Warehouse, number),
            () => CancelReturnAsync(scene.Manager, number));

        Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
        await ApiAssert.ProblemAsync(cancel, HttpStatusCode.Conflict, "not-pending", $"Return #{number} is Received. {OnlyPending}");
        var stored = await StoredReturnAsync(race.App, number);
        Assert.Equal((ReturnStatus.Received, (DateTime?)null), (stored.Status, stored.CancelledAt));
        Assert.Single(await MovementsOfReturnAsync(race.App, number));
    }

    /// <summary>
    /// AC-22.5, forced order "cancel, then receive": the receive waits, then finds Cancelled and gets <c>409</c> "Return #N
    /// is Cancelled. …". Nothing moves; the 20 kg is free leftover again.
    /// </summary>
    [Fact]
    public async Task Receive_waiting_behind_a_cancel_is_refused()
    {
        await using var race = new ForcedOrder("Returns");
        var scene = await LeftoverSceneAsync(race.App, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);

        var (cancel, receive) = await race.RunAsync(
            () => CancelReturnAsync(scene.Manager, number),
            () => ReceiveReturnAsync(scene.Warehouse, number));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await ApiAssert.ProblemAsync(receive, HttpStatusCode.Conflict, "not-pending", $"Return #{number} is Cancelled. {OnlyPending}");
        var stored = await StoredReturnAsync(race.App, number);
        Assert.Equal((ReturnStatus.Cancelled, (DateTime?)null), (stored.Status, stored.ReceivedAt));
        Assert.Empty(await MovementsOfReturnAsync(race.App, number));
        AssertStock(await StockAsync(scene.Manager, scene.Cheese.Id), warehouse: 0m, production: 20m, pending: 0m, free: 20m);
    }

    /// <summary>
    /// AC-22.5, PRD section 5: a receive and a cancel of the same return at the same moment end in one consistent outcome.
    /// Exactly one gets <c>200</c>. Either it was received (one movement, the Warehouse up by 20) and the cancel got
    /// "… is Received. …", or it was cancelled (no movement, the 20 kg free again) and the receive got "… is Cancelled.
    /// …". Three rounds.
    /// </summary>
    [Fact]
    public async Task Receive_racing_a_cancel_ends_consistently()
    {
        var scene = await LeftoverSceneAsync(factory, 0m);

        for (var round = 0; round < 3; round++)
        {
            await PutLeftoverAsync(factory, scene.Cheese.Id, 20m, scene.ManagerUser.Id);
            var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 20m);
            var warehouseBefore = Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse");

            var responses = await Task.WhenAll(ReceiveReturnAsync(scene.Warehouse, number), CancelReturnAsync(scene.Manager, number));

            var (receive, cancel) = (responses[0], responses[1]);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var stored = await StoredReturnAsync(factory, number);
            var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
            if (receive.StatusCode == HttpStatusCode.OK)
            {
                await ApiAssert.ProblemAsync(cancel, HttpStatusCode.Conflict, "not-pending", $"Return #{number} is Received. {OnlyPending}");
                Assert.Equal(ReturnStatus.Received, stored.Status);
                Assert.Single(await MovementsOfReturnAsync(factory, number));
                Assert.Equal(warehouseBefore + 20m, Figure(stock, "warehouse"));
            }
            else
            {
                await ApiAssert.ProblemAsync(receive, HttpStatusCode.Conflict, "not-pending", $"Return #{number} is Cancelled. {OnlyPending}");
                Assert.Equal(ReturnStatus.Cancelled, stored.Status);
                Assert.Empty(await MovementsOfReturnAsync(factory, number));
                Assert.Equal(warehouseBefore, Figure(stock, "warehouse"));
            }

            Assert.Equal(0m, Figure(stock, "pendingReturns"));

            // Leave nothing in production storage for the next round's exact figures.
            var leftover = Figure(stock, "productionStorage");
            if (leftover > 0m)
            {
                Assert.Equal(HttpStatusCode.Created, (await WriteOffAsync(scene.Manager, scene.Cheese.Id, -leftover)).StatusCode);
            }
        }
    }

    // ---- Access (PRD 4.2, AC-22.6) --------------------------------------------------------------------------------

    /// <summary>
    /// AC-01.5: every return endpoint without a token is <c>401</c> <c>not-authenticated</c> with
    /// <c>WWW-Authenticate: Bearer</c>, and nothing changes: the Pending return stays Pending with no movement, and no
    /// other return is created.
    /// </summary>
    [Fact]
    public async Task Return_endpoints_without_a_token_are_401()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 10m);
        using var anonymous = factory.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await InstructReturnAsync(anonymous, scene.Cheese.Id, 5m));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync(ReturnsPath, Cancel));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync($"{ReturnsPath}/{number}", Cancel));
        await ApiAssert.NotAuthenticatedAsync(await ReceiveReturnAsync(anonymous, number));
        await ApiAssert.NotAuthenticatedAsync(await CancelReturnAsync(anonymous, number));

        Assert.Equal(ReturnStatus.Pending, (await StoredReturnAsync(factory, number)).Status);
        Assert.Empty(await MovementsOfReturnAsync(factory, number));
        Assert.Equal(1, await ReturnCountAsync(factory, scene.Cheese.Id));
    }

    /// <summary>
    /// AC-22.6, AC-05.1, PRD 4.2: a Production-only user gets <c>403</c> for every return action, for a real return and
    /// for an unknown number (the role is checked first, design 2.2): instruct and cancel "It needs the Manager role.",
    /// receive "It needs the Warehouse or Manager role.". Nothing changes. Viewing is allowed to every role (PRD 4.2 "View
    /// … returns"). A user with Production and Warehouse may receive (AC-05.2).
    /// </summary>
    [Fact]
    public async Task Production_only_user_can_view_returns_but_not_act_on_them()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 10m);
        var production = await factory.CreateClientAsAsync(Roles.Production);

        await ApiAssert.ProblemAsync(await InstructReturnAsync(production, scene.Cheese.Id, 5m),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        foreach (var target in new[] { number, 999999 })
        {
            await ApiAssert.ProblemAsync(await ReceiveReturnAsync(production, target),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
            await ApiAssert.ProblemAsync(await CancelReturnAsync(production, target),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        }

        Assert.Equal(ReturnStatus.Pending, (await StoredReturnAsync(factory, number)).Status);
        Assert.Empty(await MovementsOfReturnAsync(factory, number));
        Assert.Equal(1, await ReturnCountAsync(factory, scene.Cheese.Id));
        Assert.Equal("Pending", (await ReadReturnAsync(production, number)).GetProperty("status").GetString());
        Assert.Contains(number, Numbers(await ListReturnsAsync(production, $"itemId={scene.Cheese.Id}")));

        var both = await factory.CreateClientAsAsync(Roles.Production, Roles.Warehouse);
        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(both, number)).StatusCode);
    }

    /// <summary>
    /// AC-22.6, PRD 4.2: a Warehouse-only user gets <c>403</c> "It needs the Manager role." for instructing and cancelling
    /// (a real return and an unknown number), may view returns, and may record receiving one (AC-22.4).
    /// </summary>
    [Fact]
    public async Task Warehouse_only_user_can_receive_but_not_instruct_or_cancel()
    {
        var scene = await LeftoverSceneAsync(factory, 20m);
        var number = await InstructedReturnAsync(scene.Manager, scene.Cheese.Id, 10m);

        await ApiAssert.ProblemAsync(await InstructReturnAsync(scene.Warehouse, scene.Cheese.Id, 5m),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        foreach (var target in new[] { number, 999999 })
        {
            await ApiAssert.ProblemAsync(await CancelReturnAsync(scene.Warehouse, target),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        }

        Assert.Equal(1, await ReturnCountAsync(factory, scene.Cheese.Id));
        Assert.Equal(ReturnStatus.Pending, (await StoredReturnAsync(factory, number)).Status);
        Assert.Equal(number, (await ReadReturnAsync(scene.Warehouse, number)).GetProperty("number").GetInt32());
        Assert.Contains(number, Numbers(await ListReturnsAsync(scene.Warehouse, "status=Pending")));
        Assert.Equal(HttpStatusCode.OK, (await ReceiveReturnAsync(scene.Warehouse, number)).StatusCode);
    }

    // ---- Scenes and helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// A Manager and a Warehouse user, signed in, and a new raw material with <paramref name="leftover"/> of free leftover
    /// in production storage (none when 0) and nothing in the Warehouse.
    /// </summary>
    private static async Task<LeftoverScene> LeftoverSceneAsync(TestApiFactory app, decimal leftover)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, warehouseUser) = await SignedInAsync(app, Roles.Warehouse);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        if (leftover > 0m)
        {
            await PutLeftoverAsync(app, cheese.Id, leftover, managerUser.Id);
        }

        return new LeftoverScene(manager, managerUser, warehouse, warehouseUser, cheese);
    }

    /// <summary>
    /// The instruct-versus-send scene: a new material with 1,000 in the Warehouse and 10 of free leftover, and a Created
    /// order planning 1,000 of it (leftover applied 10, so a send from 970 to 1,010 is in range). Needs a factory of its own, because
    /// the send may succeed and only one order can be InProduction (AC-15.10).
    /// </summary>
    private static async Task<SendScene> SendSceneAsync(TestApiFactory app)
    {
        var scene = await LeftoverSceneAsync(app, 10m);
        var product = await ProductWithRecipeAsync(scene.Manager, (scene.Cheese.Id, 1m));
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 1000m);
        var number = await CreateOrderAsync(scene.Manager, product.Id, 1000m);
        return new SendScene(scene.Manager, scene.Warehouse, scene.Cheese, number);
    }

    /// <summary>Posts a raw JSON body to <c>POST /returns</c>, for bodies a typed object cannot express.</summary>
    private static Task<HttpResponseMessage> PostReturnRawAsync(HttpClient client, string json) =>
        client.PostAsync(ReturnsPath, ApiJson.Content(json), Cancel);

    /// <summary>A write-off (or other adjustment) in production storage through <c>POST /adjustments</c> (T-015), unchecked.</summary>
    private static Task<HttpResponseMessage> WriteOffAsync(HttpClient manager, int itemId, decimal quantity) =>
        manager.PostAsJsonAsync("/api/v1/adjustments",
            new { itemId, location = "ProductionStorage", quantity, reason = "Spoiled" }, Cancel);

    /// <summary>One page of <c>GET /returns</c> with the given query string, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> ListReturnsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{ReturnsPath}?{query}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The <c>number</c> of every row of a page of returns, in order.</summary>
    private static List<int> Numbers(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("number").GetInt32()).ToList();

    /// <summary>How many returns of <paramref name="itemId"/> are stored, in any status.</summary>
    private static async Task<int> ReturnCountAsync(TestApiFactory app, int itemId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Returns
            .CountAsync(leftoverReturn => leftoverReturn.ItemId == itemId, Cancel);
    }

    /// <summary>One figure of a design 5.5 stock row.</summary>
    private static decimal Figure(JsonElement stock, string field) => stock.GetProperty(field).GetDecimal();

    /// <summary>
    /// AC-10.3, D-4, design 5.5: a raw material's stock row with no InProduction order using it (committed 0, no order
    /// number) and these Warehouse, production storage, Pending return and free leftover figures.
    /// </summary>
    private static void AssertStock(JsonElement stock, decimal warehouse, decimal production, decimal pending, decimal free)
    {
        Assert.Equal(warehouse, Figure(stock, "warehouse"));
        Assert.Equal(production, Figure(stock, "productionStorage"));
        Assert.Equal(0m, Figure(stock, "committedToOrder"));
        Assert.Equal(JsonValueKind.Null, stock.GetProperty("committedOrderNumber").ValueKind);
        Assert.Equal(pending, Figure(stock, "pendingReturns"));
        Assert.Equal(free, Figure(stock, "freeLeftover"));
    }

    /// <summary>AC-05.3, design 5.9: one history entry, exactly <c>{ status, at, by: { id, email }, note: null }</c>.</summary>
    private static void AssertHistoryEntry(JsonElement entry, string status, string at, AppUser by)
    {
        Assert.Equal(["status", "at", "by", "note"], entry.EnumerateObject().Select(field => field.Name));
        Assert.Equal(status, entry.GetProperty("status").GetString());
        Assert.Equal(at, entry.GetProperty("at").GetString());
        Assert.Equal(by.Id, entry.GetProperty("by").GetProperty("id").GetString());
        Assert.Equal(by.Email, entry.GetProperty("by").GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("note").ValueKind);
    }

    /// <summary>
    /// AC-22.2, design 7.2: <c>409</c> <c>insufficient-free-leftover</c> naming the item, the free leftover as the design
    /// writes numbers (6.1), and its unit.
    /// </summary>
    private static Task<JsonElement> AssertFreeLeftoverRefusedAsync(HttpResponseMessage response, CreatedItem item, string free) =>
        ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "insufficient-free-leftover",
            $"Not enough free leftover: {item.Name} has {free} {item.UnitName} free in production storage.");

    /// <summary>What <see cref="LeftoverSceneAsync"/> set up.</summary>
    private sealed record LeftoverScene(
        HttpClient Manager, AppUser ManagerUser, HttpClient Warehouse, AppUser WarehouseUser, CreatedItem Cheese);

    /// <summary>What <see cref="SendSceneAsync"/> set up.</summary>
    private sealed record SendScene(HttpClient Manager, HttpClient Warehouse, CreatedItem Cheese, int Number);
}
