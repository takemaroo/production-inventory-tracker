using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.ProductionOrders;
using InventoryTracker.Api.Features.Stock;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-010: sending the materials for an order (<c>POST /api/v1/production-orders/{number}/send</c>; US-15, D-5 to D-8,
/// PRD section 5 concurrent sends; design 5.7 and 7.2).
/// </summary>
/// <remarks>
/// <para>
/// Orders are created through the API. Stock reaches the Warehouse through receipts; free leftover reaches production
/// storage through a ledger Adjustment (the adjustment endpoint is T-015), so it is free leftover (D-4). States that later
/// tasks will produce (Completed, Cancelled, Pending and Cancelled returns) are written directly through the DbContext.
/// </para>
/// <para>
/// The database allows only one InProduction order (AC-15.10), so every test whose send succeeds creates its own factory.
/// Tests on the shared factory only ever see their send rejected, so they never take that one place.
/// </para>
/// </remarks>
public class SendOrderTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string OrdersPath = "/api/v1/production-orders";

    /// <summary>Design 7.2, AC-15.9 (PRD), typed out so a wrong message fails the test.</summary>
    private const string AlreadySent =
        "Materials have already been sent for this order. Raise a complementary order for more material.";

    /// <summary>The design 5.7 example's creation time, 08:00 UTC on 11 November 2026.</summary>
    private static readonly DateTimeOffset EightAm = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-15.1, AC-15.3: a successful send --------------------------------------------------------------------

    /// <summary>
    /// AC-15.1, AC-15.3, AC-05.3, D-6, D-8, design 5.7 (the two-material example). Production storage holds 5 kg of free
    /// Cheese; the order plans 1,000 kg of Cheese and 15 kg of Potato starch. A Warehouse user sends 995 kg and 15 kg.
    /// <c>200</c> with the detail, now InProduction: no estimate; Cheese leftover applied 5, warehouse send 995, carried
    /// leftover 0, complementary sends 0; starch 0, 15, 0, 0; the history ends with InProduction by the sender at the send
    /// time. Production storage then holds 1,000 kg of Cheese (AC-15.3), all of it committed to the order. One
    /// SentToProduction movement per line, Warehouse −S and Production storage +S, linked to the order and the user. The
    /// stored line and order fields match, and <c>GET</c> returns the same body.
    /// </summary>
    [Fact]
    public async Task Send_nets_the_leftover_moves_the_stock_and_starts_production()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, warehouseUser) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
        var starch = await CreateNamedItemAsync(manager, "Potato starch", kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 1200m);
        await ReceiveAsync(manager, starch.Id, 20m);
        await PutLeftoverAsync(app, cheese.Id, 5m, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(2));

        var response = await SendAsync(warehouse, number, (cheese.Id, 995m), (starch.Id, 15m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal(number, order.GetProperty("number").GetInt32());
        Assert.Equal("InProduction", order.GetProperty("status").GetString());
        AssertSentLine(Line(order, cheese.Id), leftoverApplied: 5m, warehouseSend: 995m, carriedLeftover: 0m);
        AssertSentLine(Line(order, starch.Id), leftoverApplied: 0m, warehouseSend: 15m, carriedLeftover: 0m);

        var history = order.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", "InProduction"], history.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("2026-11-11T10:02:00Z", history[1].GetProperty("at").GetString());
        Assert.Equal(warehouseUser.Id, history[1].GetProperty("by").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("note").ValueKind);

        // AC-15.3: "Production storage then holds 1,000 kg of Cheese". D-3: all of it is committed to this order, so none
        // of it is free leftover any more (D-4).
        var cheeseStock = await StockAsync(manager, cheese.Id);
        Assert.Equal(205m, cheeseStock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(1000m, cheeseStock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(1000m, cheeseStock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(number, cheeseStock.GetProperty("committedOrderNumber").GetInt32());
        Assert.Equal(0m, cheeseStock.GetProperty("freeLeftover").GetDecimal());
        var starchStock = await StockAsync(manager, starch.Id);
        Assert.Equal(5m, starchStock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(15m, starchStock.GetProperty("productionStorage").GetDecimal());

        // AC-15.1, D-2: one SentToProduction movement per line above zero, linked to the order and the sender.
        var sendTime = new DateTime(2026, 11, 11, 10, 2, 0, DateTimeKind.Utc);
        var movements = await MovementsOfOrderAsync(app, number);
        Assert.Equal(2, movements.Count);
        AssertSendMovement(movements.Single(movement => movement.ItemId == cheese.Id), 995m, warehouseUser.Id, sendTime);
        AssertSendMovement(movements.Single(movement => movement.ItemId == starch.Id), 15m, warehouseUser.Id, sendTime);

        // The stored fields completion will read (D-9): fixed on the lines, and who sent it and when on the order.
        var stored = await StoredOrderAsync(app, number);
        Assert.Equal(OrderStatus.InProduction, stored.Status);
        Assert.Equal(sendTime, stored.SentAt);
        Assert.Equal(warehouseUser.Id, stored.SentById);
        Assert.Equal(sendTime, stored.StatusChangedAt);
        AssertStoredLine(stored.Lines.Single(line => line.RawMaterialId == cheese.Id), 5m, 995m, 0m);
        AssertStoredLine(stored.Lines.Single(line => line.RawMaterialId == starch.Id), 0m, 15m, 0m);

        // Decision #92: the response is the detail every reader gets.
        Assert.Equal(order.GetRawText(), (await ReadOrderAsync(manager, number)).GetRawText());

        // Architecture 6, Logging: the state change is logged with the order number and the sender.
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Order {number} sent to production by user {warehouseUser.Id}.");
    }

    // ---- AC-15.2: the range without leftover ----------------------------------------------------------------------

    /// <summary>
    /// AC-15.2, D-7: planned 1,000 kg, leftover applied 0, tolerance 2%: sends of 980, 1,000 and 1,020 kg are accepted
    /// (both limits included), and so is 1,020.000 (trailing zeros do not count, design 2.4). The line stores the send,
    /// leftover applied 0 and carried leftover 0, and production storage holds exactly what was sent.
    /// </summary>
    [Theory]
    [InlineData("980", 980)]
    [InlineData("1000", 1000)]
    [InlineData("1020", 1020)]
    [InlineData("1020.000", 1020)]
    public async Task Sends_inside_the_range_are_accepted(string quantityJson, int expected)
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        var response = await SendRawAsync(warehouse, number,
            $$"""{ "lines": [ { "itemId": {{cheese.Id}}, "quantity": {{quantityJson}} } ] }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(response), cheese.Id), 0m, expected, 0m);
        Assert.Equal(expected, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-15.2, AC-15.6: with the same line, 979.999 and 1,020.001 kg are rejected, and so is 0 (zero is only allowed
    /// where the range starts at zero). <c>400</c> <c>send-out-of-range</c> "{item}: send must be between 980 and 1,020
    /// {unit} (planned 1,000 {unit}, leftover applied 0 {unit})." under <c>lines[0].quantity</c>; nothing is sent.
    /// </summary>
    [Theory]
    [InlineData("979.999")]
    [InlineData("1020.001")]
    [InlineData("0")]
    public async Task Sends_outside_the_range_without_leftover_are_rejected(string quantityText)
    {
        // Attributes cannot hold decimals, and a double such as 979.999 is binary and inexact, so the value is written
        // as text and parsed into an exact decimal.
        var quantity = decimal.Parse(quantityText, CultureInfo.InvariantCulture);
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        var response = await SendAsync(manager, number, (cheese.Id, quantity));

        var unit = cheese.UnitName;
        await AssertOutOfRangeAsync(response, ("lines[0].quantity",
            $"{cheese.Name}: send must be between 980 and 1,020 {unit} (planned 1,000 {unit}, leftover applied 0 {unit})."));
        await AssertNothingSentAsync(factory, number);
    }

    // ---- AC-15.3: leftover netting, rejections ----------------------------------------------------------------------

    /// <summary>
    /// AC-15.3, AC-15.6, D-7: with 5 kg of free leftover and 1,000 kg planned, the range is 975 to 1,015 kg, so 974.999
    /// and 1,015.001 kg are rejected with "{item}: send must be between 975 and 1,015 {unit} (planned 1,000 {unit},
    /// leftover applied 5 {unit})." and nothing is sent.
    /// </summary>
    [Theory]
    [InlineData("974.999")]
    [InlineData("1015.001")]
    public async Task Sends_outside_the_netted_range_are_rejected(string quantityText)
    {
        var quantity = decimal.Parse(quantityText, CultureInfo.InvariantCulture);
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        await PutLeftoverAsync(factory, cheese.Id, 5m, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        var response = await SendAsync(manager, number, (cheese.Id, quantity));

        var unit = cheese.UnitName;
        await AssertOutOfRangeAsync(response, ("lines[0].quantity",
            $"{cheese.Name}: send must be between 975 and 1,015 {unit} (planned 1,000 {unit}, leftover applied 5 {unit})."));
        await AssertNothingSentAsync(factory, number);
    }

    // ---- AC-15.4: leftover covers the need ----------------------------------------------------------------------

    /// <summary>
    /// AC-15.4, D-8 (the PRD's numbers). Production storage holds 50 kg of free Cheese; the order plans 30 kg. The range is
    /// 0 to 0.6 kg, so a send of 1 kg is rejected first. A send of 0 kg, by a Manager, is then accepted with no Warehouse
    /// stock at all: no movement is created, the order becomes InProduction, leftover applied 30, warehouse send 0, carried
    /// leftover 20. Production storage still holds 50 kg, of which 30 kg is committed to the order and the 20 kg carried
    /// leftover remains free leftover for later orders.
    /// </summary>
    [Fact]
    public async Task Send_of_zero_is_accepted_when_leftover_covers_the_need()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 30m);

        var rejected = await SendAsync(manager, number, (cheese.Id, 1m));

        await AssertOutOfRangeAsync(rejected, ("lines[0].quantity",
            "Cheese: send must be between 0 and 0.6 kg (planned 30 kg, leftover applied 30 kg)."));
        await AssertNothingSentAsync(app, number);

        var accepted = await SendAsync(manager, number, (cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var order = await ApiJson.ReadAsync(accepted);
        Assert.Equal("InProduction", order.GetProperty("status").GetString());
        AssertSentLine(Line(order, cheese.Id), leftoverApplied: 30m, warehouseSend: 0m, carriedLeftover: 20m);
        Assert.Empty(await MovementsOfOrderAsync(app, number));
        AssertStoredLine((await StoredOrderAsync(app, number)).Lines.Single(), 30m, 0m, 20m);

        var stock = await StockAsync(manager, cheese.Id);
        Assert.Equal(0m, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(50m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(20m, stock.GetProperty("freeLeftover").GetDecimal());
    }

    // ---- AC-15.5, AC-15.10, D-5: oldest order first, one order in production -------------------------------------

    /// <summary>
    /// AC-15.5 ("if #8 is sent before #7"), decision #10, D-5, D-6, D-8, AC-15.10. 50 kg of free Cheese; #7 (08:00,
    /// planned 30 kg), #8 (09:00, 1,000 kg) and #9 (10:00, 500 kg) are Created. #8 sent first applies only 20 kg, not 50:
    /// a send of 950 kg (right only if #8 took all 50) is rejected with its range 960 to 1,000 kg and "leftover applied
    /// 20 kg". A send of 980 kg is accepted: leftover applied 20, carried leftover 50 − 20 = 30 (worked example D, AC-20.8).
    /// The 30 kg stays free and stays allocated to #7, whose estimate is still leftover 30, nominal 0, range 0 to 0.6, while
    /// #9 still gets nothing. While #8 runs, sending #7 is <c>409</c> "Order #8 is in production. Only one order can be in
    /// production at a time." (AC-15.10), and #7 stays Created.
    /// </summary>
    [Fact]
    public async Task Newer_order_sent_first_applies_only_unallocated_leftover()
    {
        var time = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);

        var tooLittle = await SendAsync(scene.Warehouse, 8, (scene.Cheese.Id, 950m));

        await AssertOutOfRangeAsync(tooLittle, ("lines[0].quantity",
            "Cheese: send must be between 960 and 1,000 kg (planned 1,000 kg, leftover applied 20 kg)."));

        var accepted = await SendAsync(scene.Warehouse, 8, (scene.Cheese.Id, 980m));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(accepted), scene.Cheese.Id), 20m, 980m, carriedLeftover: 30m);
        AssertStoredLine((await StoredOrderAsync(app, 8)).Lines.Single(), 20m, 980m, 30m);

        // D-4 after the send: 50 + 980 = 1,030 kg in storage, 20 + 980 = 1,000 kg committed to #8, 30 kg free.
        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal(1030m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(1000m, stock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("freeLeftover").GetDecimal());

        // "The 30 kg remains free leftover and stays allocated to #7."
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 7), scene.Cheese.Id), 30m, 0m, 0m, 0.6m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 9), scene.Cheese.Id), 0m, 500m, 490m, 510m);

        // AC-15.10.
        var blocked = await SendAsync(scene.Warehouse, 7, (scene.Cheese.Id, 0m));

        await ApiAssert.ProblemAsync(blocked, HttpStatusCode.Conflict, "another-order-in-production",
            "Order #8 is in production. Only one order can be in production at a time.");
        await AssertNothingSentAsync(app, 7);
    }

    /// <summary>
    /// D-5, D-6, decision #5 (each unit of leftover is applied once), continuing AC-15.5 into worked example D (AC-20.8).
    /// #8 is sent first (leftover applied 20 kg, send 980 kg). Its completion, done here directly as T-011 will do it,
    /// consumes 1,000 kg and leaves 30 kg in production storage. #7 is then sent with 0 kg and applies exactly those 30 kg,
    /// with carried leftover 0. Together the two orders applied 20 + 30 = 50 kg, exactly the leftover that was there, and
    /// nothing is free any more: #9's estimate is leftover 0.
    /// </summary>
    [Fact]
    public async Task Each_unit_of_leftover_is_applied_to_one_order_only()
    {
        var time = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(scene.Warehouse, 8, (scene.Cheese.Id, 980m))).StatusCode);

        await CompleteDirectlyAsync(app, 8, scene.Cheese.Id, consumed: 1000m, scene.ManagerUser.Id);
        var response = await SendAsync(scene.Warehouse, 7, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(response), scene.Cheese.Id), 30m, 0m, carriedLeftover: 0m);
        var applied = (await StoredOrderAsync(app, 8)).Lines.Single().LeftoverApplied
            + (await StoredOrderAsync(app, 7)).Lines.Single().LeftoverApplied;
        Assert.Equal(50m, applied);

        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal(30m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(0m, stock.GetProperty("freeLeftover").GetDecimal());
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 9), scene.Cheese.Id), 0m, 500m, 490m, 510m);
    }

    /// <summary>
    /// Decision #97, AC-13.2, D-6, AC-15.5: the estimate a Created order shows is exactly what its send applies. Each of
    /// #7, #8 and #9 shows its estimate first; #8 is then sent with exactly its estimated nominal send (980 kg) and is
    /// accepted, and its fixed leftover applied (20 kg) equals its estimated leftover applied. Both come from the one
    /// shared <c>WaitingOrderQueue</c>; a send with its own queue could disagree with what the warehouse was shown.
    /// </summary>
    [Fact]
    public async Task Send_applies_exactly_the_leftover_the_estimate_showed()
    {
        var time = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);
        var estimate = Line(await ReadOrderAsync(scene.Warehouse, 8), scene.Cheese.Id).GetProperty("estimate");
        var estimatedLeftover = estimate.GetProperty("leftoverApplied").GetDecimal();
        var nominal = estimate.GetProperty("nominalSend").GetDecimal();
        Assert.Equal(20m, estimatedLeftover);
        Assert.Equal(980m, nominal);

        var response = await SendAsync(scene.Warehouse, 8, (scene.Cheese.Id, nominal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(response), scene.Cheese.Id), estimatedLeftover, nominal, carriedLeftover: 30m);
    }

    /// <summary>
    /// Decision #97, D-5 tie-break (architecture 3.3), D-6: two orders created in the same second are queued by number, and
    /// the send uses the same queue as the estimate. 10 kg of free Cheese, orders A and B both planning 10 kg, same
    /// second: B's estimate is leftover 0, nominal 10, range 9.8 to 10.2. B sent first with its nominal 10 kg applies 0 kg
    /// (the 10 kg stays owed to A, carried leftover 10), and a send of 0 kg, right only if B took A's 10 kg, is rejected
    /// with B's estimated range.
    /// </summary>
    [Fact]
    public async Task Send_uses_the_same_same_second_queue_as_the_estimate()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(EightAm) };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 100m);
        await PutLeftoverAsync(app, cheese.Id, 10m, managerUser.Id);
        var older = await CreateOrderAsync(manager, product.Id, 10m);
        var younger = await CreateOrderAsync(manager, product.Id, 10m);
        AssertEstimate(Line(await ReadOrderAsync(manager, older), cheese.Id), 10m, 0m, 0m, 0.2m);
        AssertEstimate(Line(await ReadOrderAsync(manager, younger), cheese.Id), 0m, 10m, 9.8m, 10.2m);

        await AssertOutOfRangeAsync(await SendAsync(manager, younger, (cheese.Id, 0m)), ("lines[0].quantity",
            "Cheese: send must be between 9.8 and 10.2 kg (planned 10 kg, leftover applied 0 kg)."));
        var response = await SendAsync(manager, younger, (cheese.Id, 10m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(response), cheese.Id), 0m, 10m, carriedLeftover: 10m);
    }

    // ---- PRD section 5: concurrent sends ----------------------------------------------------------------------

    /// <summary>
    /// AC-15.9, architecture 5.2, PRD section 5: two simultaneous sends of the same order (separate clients) give exactly
    /// one <c>200</c> and one <c>409</c> <c>already-sent</c>. The Warehouse holds exactly one send's worth, and ends at 0,
    /// never below: one movement only, production storage 1,000 kg. Repeated over three rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Simultaneous_sends_of_the_same_order_succeed_once()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var token = await WarehouseTokenAsync(app);

        for (var round = 0; round < 3; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            using var first = app.CreateClientWithToken(token);
            using var second = app.CreateClientWithToken(token);

            var responses = await Task.WhenAll(
                SendAsync(first, number, (cheese.Id, 1000m)), SendAsync(second, number, (cheese.Id, 1000m)));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, "already-sent", AlreadySent);
            var movement = Assert.Single(await MovementsOfOrderAsync(app, number));
            Assert.Equal(-1000m, movement.WarehouseChange);
            var stock = await StockAsync(manager, cheese.Id);
            Assert.Equal(0m, stock.GetProperty("warehouse").GetDecimal());
            Assert.Equal(1000m, stock.GetProperty("productionStorage").GetDecimal());

            // Free the one InProduction place for the next round.
            await SetStatusAsync(app, number, OrderStatus.Cancelled, managerUser.Id);
        }
    }

    /// <summary>
    /// AC-15.10, decision #6, architecture 5.2: simultaneous sends of two different orders give exactly one <c>200</c> and
    /// one <c>409</c> <c>another-order-in-production</c> naming the winner. The Warehouse holds enough for only one of
    /// them and ends at 0, never below; the loser has nothing sent. Repeated over three rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Simultaneous_sends_of_two_orders_start_only_one()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var token = await WarehouseTokenAsync(app);

        for (var round = 0; round < 3; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 500m);
            int[] numbers = [await CreateOrderAsync(manager, product.Id, 500m), await CreateOrderAsync(manager, product.Id, 500m)];
            using var first = app.CreateClientWithToken(token);
            using var second = app.CreateClientWithToken(token);

            var responses = await Task.WhenAll(
                SendAsync(first, numbers[0], (cheese.Id, 500m)), SendAsync(second, numbers[1], (cheese.Id, 500m)));

            var winnerIndex = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var winner = numbers[winnerIndex];
            var loser = numbers[1 - winnerIndex];
            await ApiAssert.ProblemAsync(responses[1 - winnerIndex], HttpStatusCode.Conflict, "another-order-in-production",
                $"Order #{winner} is in production. Only one order can be in production at a time.");
            await AssertNothingSentAsync(app, loser);
            Assert.Equal(0m, (await StockAsync(manager, cheese.Id)).GetProperty("warehouse").GetDecimal());

            await SetStatusAsync(app, winner, OrderStatus.Cancelled, managerUser.Id);
        }
    }

    /// <summary>
    /// D-5, D-6 under concurrency (decision #5: each unit of leftover counted once). 50 kg of free Cheese and five Created
    /// orders A to E, each planning 30 kg: oldest first, A is owed 30 kg, B 20 kg, C to E nothing. All five are sent at the
    /// same moment, each with a quantity right for its own share (A 0, B 10, C to E 30 kg). Exactly one succeeds, the others
    /// get <c>another-order-in-production</c> naming it. The winner applied exactly its share, and the rest of the 50 kg is
    /// still free: committed + free = production storage, and free = 50 − the winner's leftover applied. No losing order has
    /// any leftover applied, and the Warehouse never goes below zero.
    /// </summary>
    [Fact]
    public async Task Simultaneous_sends_apply_each_unit_of_leftover_once()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 30m);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        var numbers = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            numbers.Add(await CreateOrderAsync(manager, product.Id, 30m));
        }

        decimal[] share = [30m, 20m, 0m, 0m, 0m];
        decimal[] send = [0m, 10m, 30m, 30m, 30m];
        var token = await WarehouseTokenAsync(app);
        var clients = numbers.Select(_ => app.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(numbers.Select((number, i) => SendAsync(clients[i], number, (cheese.Id, send[i]))));

        var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        AssertSentLine(Line(await ApiJson.ReadAsync(responses[winner]), cheese.Id),
            share[winner], send[winner], carriedLeftover: 50m - share[winner]);
        for (var i = 0; i < numbers.Count; i++)
        {
            if (i == winner)
            {
                continue;
            }

            await ApiAssert.ProblemAsync(responses[i], HttpStatusCode.Conflict, "another-order-in-production",
                $"Order #{numbers[winner]} is in production. Only one order can be in production at a time.");
            await AssertNothingSentAsync(app, numbers[i]);
        }

        var stock = await StockAsync(manager, cheese.Id);
        var storage = stock.GetProperty("productionStorage").GetDecimal();
        var committed = stock.GetProperty("committedToOrder").GetDecimal();
        var free = stock.GetProperty("freeLeftover").GetDecimal();
        Assert.Equal(50m + send[winner], storage);
        Assert.Equal(share[winner] + send[winner], committed);
        Assert.Equal(50m - share[winner], free);
        Assert.Equal(storage, committed + free);
        Assert.Equal(30m - send[winner], stock.GetProperty("warehouse").GetDecimal());
        clients.ForEach(client => client.Dispose());
    }

    /// <summary>
    /// Decision #46, architecture 4.1 item 11 (the AC-15.10 backstop). Another order is made InProduction inside the send's
    /// own transaction, after its checks and just before its first save, which no real request can do. The unique filtered
    /// index then refuses the second InProduction order: <c>409</c> <c>concurrent-change</c>, logged at Warning, and the
    /// whole transaction rolls back: both orders are still Created and no stock moved. The send is logged only after its
    /// commit, so no "sent to production" line is written for it.
    /// </summary>
    [Fact]
    public async Task Unique_in_production_index_is_the_backstop()
    {
        var other = 0;
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<ProductionOrder>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == OrderStatus.InProduction),
            work: (context, cancellationToken) => context.Database.ExecuteSqlAsync(
                $"UPDATE ProductionOrders SET Status = 'InProduction' WHERE Number = {other}", cancellationToken));
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        other = await CreateOrderAsync(manager, product.Id, 10m);

        var response = await SendAsync(manager, number, (cheese.Id, 1000m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "concurrent-change",
            "The data changed while your request was being processed. Read it again and retry.");
        Assert.True(interceptor.HasRun);
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is DbUpdateException);
        await AssertNothingSentAsync(app, number);
        Assert.Equal(OrderStatus.Created, (await StoredOrderAsync(app, other)).Status);
        Assert.Equal(1000m, (await StockAsync(manager, cheese.Id)).GetProperty("warehouse").GetDecimal());
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains("sent to production", StringComparison.Ordinal));
    }

    /// <summary>
    /// Architecture 6 (Logging), PRD 4.1 "rejected": the send is logged only after its transaction commits. Here the commit
    /// itself fails (an EF Core transaction interceptor throws as the send's commit starts, the last step of
    /// <c>InWriteTransactionAsync</c>), so the answer is <c>500</c> <c>internal-error</c>, nothing is saved, and no
    /// "sent to production" line is written, although every step of the send before the commit ran.
    /// </summary>
    [Fact]
    public async Task Send_whose_commit_fails_is_not_logged_as_sent()
    {
        var failCommit = new FailingCommitInterceptor();
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(failCommit)),
        };
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        failCommit.Arm();
        var response = await SendAsync(manager, number, (cheese.Id, 1000m));

        Assert.True(failCommit.HasRun);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        ApiAssert.Problem(response, await ApiJson.ReadAsync(response), HttpStatusCode.InternalServerError,
            ApiAssert.NoTitleCheck, "internal-error", "An unexpected error occurred.");
        await AssertNothingSentAsync(app, number);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains("sent to production", StringComparison.Ordinal));
    }

    // ---- AC-15.6, AC-15.7: every failing line is named --------------------------------------------------------

    /// <summary>
    /// AC-15.6, design 6 (the example body, word for word): Cheese (5 kg leftover, planned 1,000 kg) and Potato starch
    /// (planned 15 kg) both out of range give one <c>400</c> <c>send-out-of-range</c> naming both lines with their
    /// ranges, each under its own <c>lines[i].quantity</c>. With only starch out of range, only starch is named. Nothing
    /// is sent either time.
    /// </summary>
    [Fact]
    public async Task Every_line_outside_its_range_is_named()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
        var starch = await CreateNamedItemAsync(manager, "Potato starch", kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        await ReceiveAsync(manager, starch.Id, 100m);
        await PutLeftoverAsync(app, cheese.Id, 5m, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        const string cheeseMessage = "Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg).";
        const string starchMessage = "Potato starch: send must be between 14.7 and 15.3 kg (planned 15 kg, leftover applied 0 kg).";

        var both = await SendAsync(manager, number, (cheese.Id, 1020m), (starch.Id, 14m));

        await AssertOutOfRangeAsync(both, ("lines[0].quantity", cheeseMessage), ("lines[1].quantity", starchMessage));

        var one = await SendAsync(manager, number, (cheese.Id, 995m), (starch.Id, 16m));

        await AssertOutOfRangeAsync(one, ("lines[1].quantity", starchMessage));
        await AssertNothingSentAsync(app, number);
    }

    /// <summary>
    /// AC-15.7, design 7.2 (one sentence per short line), architecture 5.1: when the Warehouse is short for two lines, one
    /// <c>409</c> <c>insufficient-stock</c> names both, with what the Warehouse holds: "Not enough stock: {item} in Warehouse
    /// has 990 {unit}. Not enough stock: {item} in Warehouse has 0 {unit}." A line that has enough is not named. Nothing is
    /// sent.
    /// </summary>
    [Fact]
    public async Task Every_line_short_of_warehouse_stock_is_named()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (salt.Id, 0.01m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 990m);
        await ReceiveAsync(manager, salt.Id, 10m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        var response = await SendAsync(manager, number, (cheese.Id, 995m), (salt.Id, 10m), (starch.Id, 15m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {cheese.Name} in Warehouse has 990 {unit.Name}. " +
            $"Not enough stock: {starch.Name} in Warehouse has 0 {unit.Name}.");
        await AssertNothingSentAsync(factory, number);
        Assert.Equal(990m, (await StockAsync(manager, cheese.Id)).GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// AC-15.6, AC-15.7, decision #96, design 6: errors follow the body, not the recipe. The lines are sent in reverse
    /// recipe order (starch first). Out of range, starch's message is under <c>lines[0].quantity</c> and comes first in
    /// <c>detail</c>, cheese's under <c>lines[1].quantity</c>. With no Warehouse stock and both in range, the
    /// <c>insufficient-stock</c> detail names starch first too.
    /// </summary>
    [Fact]
    public async Task Errors_follow_the_order_of_the_body_not_the_recipe()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager, receive: false);
        var unit = cheese.UnitName;

        var outOfRange = await SendAsync(manager, number, (starch.Id, 14m), (cheese.Id, 1030m));

        await AssertOutOfRangeAsync(outOfRange,
            ("lines[0].quantity",
                $"{starch.Name}: send must be between 14.7 and 15.3 {unit} (planned 15 {unit}, leftover applied 0 {unit})."),
            ("lines[1].quantity",
                $"{cheese.Name}: send must be between 980 and 1,020 {unit} (planned 1,000 {unit}, leftover applied 0 {unit})."));

        var shortOfStock = await SendAsync(manager, number, (starch.Id, 15m), (cheese.Id, 1000m));

        await ApiAssert.ProblemAsync(shortOfStock, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {starch.Name} in Warehouse has 0 {unit}. Not enough stock: {cheese.Name} in Warehouse has 0 {unit}.");
        await AssertNothingSentAsync(factory, number);
    }

    // ---- AC-15.8, decision #80: the body's shape -----------------------------------------------------------------

    /// <summary>
    /// AC-15.8, design 5.7 and 7.2, decisions #75, #80, #95, #96: each bad body is <c>400</c> <c>validation</c> with one
    /// message under the field's JSON path, and nothing is sent. The order has two lines, Cheese (planned 1,000) and Potato
    /// starch (planned 15); each case changes one thing in an otherwise valid body: a missing line; an item not on the
    /// order, or no item at all with that id; a negative quantity; more than 3 decimal places; a quantity above
    /// 999,999,999.999; a missing quantity; a missing item; the same item twice; and no lines at all, or no list.
    /// </summary>
    [Theory]
    [InlineData("missing-line")]
    [InlineData("not-on-order")]
    [InlineData("unknown-item")]
    [InlineData("negative")]
    [InlineData("too-many-decimals")]
    [InlineData("above-maximum")]
    [InlineData("quantity-missing")]
    [InlineData("item-missing")]
    [InlineData("duplicate")]
    [InlineData("empty-list")]
    [InlineData("no-list")]
    public async Task Bad_send_bodies_are_rejected(string problem)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var cheeseLine = $$"""{ "itemId": {{cheese.Id}}, "quantity": 1000 }""";
        var starchLine = $$"""{ "itemId": {{starch.Id}}, "quantity": 15 }""";

        var (body, path, message) = problem switch
        {
            "missing-line" => ($$"""{ "lines": [ {{cheeseLine}} ] }""",
                "lines", $"Every order line needs a quantity. Missing: {starch.Name}."),
            "not-on-order" => ($$"""{ "lines": [ {{cheeseLine}}, {{starchLine}}, { "itemId": {{other.Id}}, "quantity": 1 } ] }""",
                "lines[2].itemId", $"Item {other.Id} is not on order #{number}."),
            "unknown-item" => ($$"""{ "lines": [ {{cheeseLine}}, {{starchLine}}, { "itemId": 999999, "quantity": 1 } ] }""",
                "lines[2].itemId", $"Item 999999 is not on order #{number}."),
            "negative" => ($$"""{ "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}}, "quantity": -1 } ] }""",
                "lines[1].quantity", "Quantity cannot be negative."),
            "too-many-decimals" => ($$"""{ "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}}, "quantity": 15.0001 } ] }""",
                "lines[1].quantity", "Quantity can have at most 3 decimal places."),
            "above-maximum" => ($$"""{ "lines": [ { "itemId": {{cheese.Id}}, "quantity": 1000000000 }, {{starchLine}} ] }""",
                "lines[0].quantity", ExpectedMessages.QuantityAboveMax),
            "quantity-missing" => ($$"""{ "lines": [ {{cheeseLine}}, { "itemId": {{starch.Id}} } ] }""",
                "lines[1].quantity", "Quantity is required."),
            "item-missing" => ($$"""{ "lines": [ {{cheeseLine}}, {{starchLine}}, { "quantity": 1 } ] }""",
                "lines[2].itemId", "Item is required."),
            "duplicate" => ($$"""{ "lines": [ {{cheeseLine}}, {{starchLine}}, {{cheeseLine}} ] }""",
                "lines[2].itemId", $"{cheese.Name} appears on more than one line."),
            "empty-list" => ("""{ "lines": [] }""",
                "lines", $"Every order line needs a quantity. Missing: {cheese.Name}, {starch.Name}."),
            "no-list" => ("{}",
                "lines", $"Every order line needs a quantity. Missing: {cheese.Name}, {starch.Name}."),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var response = await SendRawAsync(manager, number, body);

        await ApiAssert.ValidationAsync(response, path, message);
        await AssertNothingSentAsync(factory, number);
    }

    /// <summary>
    /// AC-15.8, design 6 and 2.2: every problem in a body is reported in one <c>400</c>, in body order with the missing
    /// lines last: a line sent as <c>null</c> gets both "required" messages (decision #74), a negative quantity its own,
    /// and the two order lines left without a quantity are named once, in recipe line order.
    /// </summary>
    [Fact]
    public async Task Every_body_problem_is_reported_at_once()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);

        var response = await SendRawAsync(manager, number, $$"""{ "lines": [ null, { "itemId": {{starch.Id}}, "quantity": -2 } ] }""");

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"Item is required. Quantity is required. Quantity cannot be negative. Every order line needs a quantity. Missing: {cheese.Name}.");
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[0].itemId"));
        Assert.Equal(["Quantity is required."], ApiJson.Messages(body, "lines[0].quantity"));
        Assert.Equal(["Quantity cannot be negative."], ApiJson.Messages(body, "lines[1].quantity"));
        Assert.Equal([$"Every order line needs a quantity. Missing: {cheese.Name}."], ApiJson.Messages(body, "lines"));
        await AssertNothingSentAsync(factory, number);
    }

    /// <summary>
    /// Decision #80, task T-010: the maximum is a field check, made before any range. 999,999,999.999 itself passes the
    /// field checks (it is the largest allowed input), so it reaches the range check and is <c>400</c>
    /// <c>send-out-of-range</c>, not <c>validation</c>; one more (1,000,000,000) is the field error of the theory above.
    /// </summary>
    [Fact]
    public async Task Largest_allowed_quantity_reaches_the_range_check()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);

        var response = await SendAsync(manager, number, (cheese.Id, 999_999_999.999m), (starch.Id, 15m));

        var unit = cheese.UnitName;
        await AssertOutOfRangeAsync(response, ("lines[0].quantity",
            $"{cheese.Name}: send must be between 980 and 1,020 {unit} (planned 1,000 {unit}, leftover applied 0 {unit})."));
    }

    /// <summary>
    /// Architecture 4.1 item 2, design 2.4: a quantity sent as a string is <c>400</c> <c>validation</c>
    /// "lines[0].quantity has the wrong type.", and nothing is sent.
    /// </summary>
    [Fact]
    public async Task Quantity_sent_as_a_string_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);

        var response = await SendRawAsync(manager, number,
            $$"""{ "lines": [ { "itemId": {{cheese.Id}}, "quantity": "1000" }, { "itemId": {{starch.Id}}, "quantity": 15 } ] }""");

        await ApiAssert.ValidationAsync(response, "lines[0].quantity", "lines[0].quantity has the wrong type.");
        await AssertNothingSentAsync(factory, number);
    }

    // ---- AC-15.9: one send per order ------------------------------------------------------------------------------

    /// <summary>
    /// AC-15.9, decision #4: a second send of an order that is already InProduction is <c>409</c> <c>already-sent</c>
    /// "Materials have already been sent for this order. Raise a complementary order for more material." Nothing more
    /// moves: still one movement, and the stored send figures are those of the first send.
    /// </summary>
    [Fact]
    public async Task Second_send_of_an_order_is_rejected()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 3000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m))).StatusCode);

        var second = await SendAsync(manager, number, (cheese.Id, 990m));

        await ApiAssert.ProblemAsync(second, HttpStatusCode.Conflict, "already-sent", AlreadySent);
        Assert.Single(await MovementsOfOrderAsync(app, number));
        AssertStoredLine((await StoredOrderAsync(app, number)).Lines.Single(), 0m, 1000m, 0m);
        Assert.Equal(2000m, (await StockAsync(manager, cheese.Id)).GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// AC-15.9: a Completed order gets <c>already-sent</c>; a Cancelled order gets <c>409</c> <c>order-cancelled</c>
    /// "This order is cancelled." The status is changed directly (completion and cancellation are T-011 and T-016).
    /// </summary>
    [Theory]
    [InlineData(OrderStatus.Completed, "already-sent", AlreadySent)]
    [InlineData(OrderStatus.Cancelled, "order-cancelled", "This order is cancelled.")]
    public async Task Orders_that_are_not_created_cannot_be_sent(OrderStatus status, string code, string message)
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);
        await SetStatusAsync(factory, number, status, managerUser.Id);

        var response = await SendAsync(manager, number, (cheese.Id, 1000m), (starch.Id, 15m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, code, message);
        Assert.Empty(await MovementsOfOrderAsync(factory, number));
    }

    // ---- AC-15.11, decision #11: Pending returns block the send ---------------------------------------------------

    /// <summary>
    /// AC-15.11, decision #11: a Pending return of one of the order's materials (inserted directly; returns are T-019)
    /// blocks the send with <c>409</c> <c>pending-return</c> "Return #R of {item} is waiting to be received by the
    /// warehouse. Record it first." A Pending return of a material that is not on the order does not block. Once the
    /// blocking return is no longer Pending (here: Cancelled), the same send succeeds.
    /// </summary>
    [Fact]
    public async Task Pending_return_of_a_material_blocks_the_send()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
        var unrelated = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        await PutLeftoverAsync(app, cheese.Id, 10m, managerUser.Id);
        await InsertReturnAsync(app, unrelated.Id, 5m, ReturnStatus.Pending, managerUser.Id);
        var returnNumber = await InsertReturnAsync(app, cheese.Id, 10m, ReturnStatus.Pending, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);

        var blocked = await SendAsync(manager, number, (cheese.Id, 1000m));

        await ApiAssert.ProblemAsync(blocked, HttpStatusCode.Conflict, "pending-return",
            $"Return #{returnNumber} of Cheese is waiting to be received by the warehouse. Record it first.");
        await AssertNothingSentAsync(app, number);

        await SetReturnStatusAsync(app, returnNumber, ReturnStatus.Cancelled, managerUser.Id);
        var accepted = await SendAsync(manager, number, (cheese.Id, 990m));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        AssertSentLine(Line(await ApiJson.ReadAsync(accepted), cheese.Id), 10m, 990m, 0m);
    }

    /// <summary>
    /// AC-15.11, decision #96: every blocking Pending return is named, in recipe line order (then return number), joined
    /// with a space, whatever order the returns were instructed in.
    /// </summary>
    [Fact]
    public async Task Every_pending_return_is_named_in_recipe_line_order()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);
        var starchReturn = await InsertReturnAsync(factory, starch.Id, 1m, ReturnStatus.Pending, managerUser.Id);
        var cheeseReturn = await InsertReturnAsync(factory, cheese.Id, 2m, ReturnStatus.Pending, managerUser.Id);

        var response = await SendAsync(manager, number, (cheese.Id, 1000m), (starch.Id, 15m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "pending-return",
            $"Return #{cheeseReturn} of {cheese.Name} is waiting to be received by the warehouse. Record it first. " +
            $"Return #{starchReturn} of {starch.Name} is waiting to be received by the warehouse. Record it first.");
        await AssertNothingSentAsync(factory, number);
    }

    // ---- Design 2.2: the order of checks ----------------------------------------------------------------------

    /// <summary>
    /// Design 2.2 step 3 before step 4, AC-14.3: an unknown order number is <c>404</c> "Order #999999 not found.", even
    /// with a body that is wrong too.
    /// </summary>
    [Fact]
    public async Task Unknown_order_is_404_before_the_body_is_checked()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await SendRawAsync(manager, 999999, """{ "lines": [ { "itemId": 1, "quantity": -1 } ] }""");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Order #999999 not found.");
    }

    /// <summary>
    /// Design 2.2: the body (step 4, <c>400</c>) is checked before the order's state (step 5, <c>409</c>), so a negative
    /// quantity on a Cancelled order is <c>400</c> <c>validation</c>; and the state before the ranges (step 6), so an
    /// out-of-range quantity on a Cancelled order is <c>409</c> <c>order-cancelled</c>.
    /// </summary>
    [Fact]
    public async Task Body_comes_before_state_and_state_before_ranges()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);
        await SetStatusAsync(factory, number, OrderStatus.Cancelled, managerUser.Id);

        await ApiAssert.ValidationAsync(await SendAsync(manager, number, (cheese.Id, -1m), (starch.Id, 15m)),
            "lines[0].quantity", "Quantity cannot be negative.");
        await ApiAssert.ProblemAsync(await SendAsync(manager, number, (cheese.Id, 5m), (starch.Id, 15m)),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
    }

    /// <summary>
    /// Design 2.2: a Pending return (step 5, <c>409</c>) is reported before an out-of-range line (step 6), and an
    /// out-of-range line (step 6, <c>400</c>) before Warehouse stock (step 7, <c>409</c>): with no stock at all and one
    /// line out of range, the answer is <c>send-out-of-range</c>.
    /// </summary>
    [Fact]
    public async Task State_comes_before_ranges_and_ranges_before_stock()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager, receive: false);

        var unit = cheese.UnitName;
        await AssertOutOfRangeAsync(await SendAsync(manager, number, (cheese.Id, 900m), (starch.Id, 15m)), ("lines[0].quantity",
            $"{cheese.Name}: send must be between 980 and 1,020 {unit} (planned 1,000 {unit}, leftover applied 0 {unit})."));

        var returnNumber = await InsertReturnAsync(factory, starch.Id, 1m, ReturnStatus.Pending, managerUser.Id);
        await ApiAssert.ProblemAsync(await SendAsync(manager, number, (cheese.Id, 900m), (starch.Id, 15m)),
            HttpStatusCode.Conflict, "pending-return",
            $"Return #{returnNumber} of {starch.Name} is waiting to be received by the warehouse. Record it first.");
        await AssertNothingSentAsync(factory, number);
    }

    /// <summary>
    /// Decisions #85 and #97: the shared waiting-orders queue refuses to run outside a transaction, so the queue and the
    /// free leftover it is given always come from one snapshot. Inside one, the same call works.
    /// </summary>
    [Fact]
    public async Task Waiting_order_queue_refuses_to_run_outside_a_transaction()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = scope.ServiceProvider.GetRequiredService<StockQueries>();
        var order = new ProductionOrder { Number = 999999, Status = OrderStatus.Created, CreatedAt = EightAm.UtcDateTime };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitingOrderQueue.AllocateAsync(db, order, new Dictionary<int, decimal>(), Cancel));
        Assert.Contains("decision #85", refused.Message);

        var allocation = await stock.InReadTransactionAsync(
            () => WaitingOrderQueue.AllocateAsync(db, order, new Dictionary<int, decimal>(), Cancel), Cancel);
        Assert.Equal(0m, allocation.For(order.Number, rawMaterialId: 1));
    }

    // ---- Access (PRD 4.2) -------------------------------------------------------------------------------------

    /// <summary>AC-01.5: the send without a token is <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Send_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await SendRawAsync(anonymous, 1, """{ "lines": [] }"""));
    }

    /// <summary>
    /// AC-15.12, AC-05.1, PRD 4.2 ("Send materials for an order": Warehouse or Manager): a Production-only user gets
    /// <c>403</c> "It needs the Warehouse or Manager role.", for a real order with a valid body and also for an unknown
    /// number (the role is checked before the record, design 2.2). Nothing is sent.
    /// </summary>
    [Fact]
    public async Task Production_only_user_cannot_send()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(factory, manager);
        var production = await factory.CreateClientAsAsync(Roles.Production);

        await ApiAssert.ProblemAsync(await SendAsync(production, number, (cheese.Id, 1000m), (starch.Id, 15m)),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        await ApiAssert.ProblemAsync(await SendAsync(production, 999999, (cheese.Id, 1000m)),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        await AssertNothingSentAsync(factory, number);
    }

    /// <summary>
    /// AC-05.2, PRD 4.2: a user who holds Production and Warehouse may send, because one of their roles allows it.
    /// </summary>
    [Fact]
    public async Task User_with_warehouse_and_production_roles_can_send()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(app, manager);
        var both = await app.CreateClientAsAsync(Roles.Production, Roles.Warehouse);

        var response = await SendAsync(both, number, (cheese.Id, 1000m), (starch.Id, 15m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The AC-15.5 starting point: Cheese in kg, a product using 1 kg per bag, 2,000 kg in the Warehouse, 50 kg of free
    /// leftover in production storage, and Created orders #7 (08:00, 30 bags), #8 (09:00, 1,000 bags) and #9 (10:00, 500
    /// bags). Orders #1 to #6 are created first and cancelled, so the next numbers are the PRD's.
    /// </summary>
    private static async Task<OldestFirstScene> OldestFirstSceneAsync(TestApiFactory app, FakeTimeProvider time)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", kg);
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
        return new OldestFirstScene(manager, managerUser, warehouse, cheese);
    }

    /// <summary>
    /// An EF Core transaction interceptor (called around EF's own transactions, such as the one
    /// <c>InWriteTransactionAsync</c> opens): once armed, it makes the next commit fail, once. Used to fail a send at the
    /// very end of its transaction.
    /// </summary>
    private sealed class FailingCommitInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _hasRun;

        /// <summary>True once a commit has been failed.</summary>
        public bool HasRun => Volatile.Read(ref _hasRun) == 1;

        /// <summary>From now on, the next commit fails.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <inheritdoc />
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.Exchange(ref _hasRun, 1) == 0)
            {
                throw new InvalidOperationException("Commit failed on purpose by the test.");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    /// <summary>What <see cref="OldestFirstSceneAsync"/> set up.</summary>
    private sealed record OldestFirstScene(HttpClient Manager, AppUser ManagerUser, HttpClient Warehouse, CreatedItem Cheese);

    /// <summary>
    /// A Created order for 1,000 bags of a new product whose recipe is 1 of a new material ("cheese", planned 1,000) and
    /// 0.015 of another ("starch", planned 15), both in one new unit. With <paramref name="receive"/>, the Warehouse gets
    /// 2,000 and 100 of them, enough for any in-range send.
    /// </summary>
    private static async Task<(int Number, CreatedItem Cheese, CreatedItem Starch)> TwoLineOrderAsync(
        TestApiFactory app, HttpClient manager, bool receive = true)
    {
        var unit = await Seed.CreateUnitAsync(manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        if (receive)
        {
            await ReceiveAsync(manager, cheese.Id, 2000m);
            await ReceiveAsync(manager, starch.Id, 100m);
        }

        return (await CreateOrderAsync(manager, product.Id, 1000m), cheese, starch);
    }

    /// <summary>A new user with the given roles, signed in, with the user record (for comparing ids).</summary>
    private static async Task<(HttpClient Client, AppUser User)> SignedInAsync(TestApiFactory app, params string[] roles)
    {
        var email = Seed.UniqueEmail("sender");
        var user = await app.CreateUserAsync(email, roles);
        return (app.CreateClientWithToken(await app.SignInAsync(email)), user);
    }

    /// <summary>The token of a new Warehouse user, for tests that send from several clients at once.</summary>
    private static async Task<string> WarehouseTokenAsync(TestApiFactory app)
    {
        var email = Seed.UniqueEmail("warehouse");
        await app.CreateUserAsync(email, Roles.Warehouse);
        return await app.SignInAsync(email);
    }

    /// <summary>A raw material with an exact name (such as "Cheese"), so messages can be checked word for word.</summary>
    private static async Task<CreatedItem> CreateNamedItemAsync(HttpClient manager, string name, CreatedUnit unit)
    {
        var response = await manager.PostAsJsonAsync("/api/v1/items",
            new { name, type = nameof(ItemType.RawMaterial), unitId = unit.Id }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        return new CreatedItem(body.GetProperty("id").GetInt32(), name, unit.Id, unit.Name, body.GetProperty("createdAt").GetString()!);
    }

    /// <summary>A new finished product with the given recipe (T-007), lines in the order given.</summary>
    private static async Task<CreatedItem> ProductWithRecipeAsync(HttpClient manager, params (int ItemId, decimal Amount)[] lines)
    {
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var response = await manager.PutAsJsonAsync($"/api/v1/items/{product.Id}/recipe",
            new { lines = lines.Select(line => new { itemId = line.ItemId, amountPerUnit = line.Amount }) }, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return product;
    }

    /// <summary>Creates an order through the API (T-009), checks the <c>201</c>, and returns its number.</summary>
    private static async Task<int> CreateOrderAsync(HttpClient manager, int productId, decimal quantity)
    {
        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = productId, orderedQuantity = quantity }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("number").GetInt32();
    }

    /// <summary>The send under test, with one <c>{ itemId, quantity }</c> per pair, in the order given.</summary>
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, int number, params (int ItemId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync($"{OrdersPath}/{number}/send",
            new { lines = lines.Select(line => new { itemId = line.ItemId, quantity = line.Quantity }) }, Cancel);

    /// <summary>The send under test with exactly this JSON body.</summary>
    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{OrdersPath}/{number}/send", ApiJson.Content(json), Cancel);

    private static async Task<JsonElement> ReadOrderAsync(HttpClient client, int number)
    {
        var response = await client.GetAsync($"{OrdersPath}/{number}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    private static async Task<JsonElement> StockAsync(HttpClient client, int itemId)
    {
        var response = await client.GetAsync($"/api/v1/stock/{itemId}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The order line for one raw material.</summary>
    private static JsonElement Line(JsonElement order, int itemId) =>
        order.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("itemId").GetInt32() == itemId);

    /// <summary>A receipt through <c>POST /receipts</c> (T-008).</summary>
    private static async Task ReceiveAsync(HttpClient client, int itemId, decimal quantity)
    {
        var response = await client.PostAsJsonAsync("/api/v1/receipts", new { itemId, quantity, receiptDate = "2026-11-01" }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Adds one movement through <see cref="StockLedger"/> in its own write transaction, as a service would.</summary>
    private static async Task LedgerAsync(TestApiFactory app, Movement movement)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
        await db.InWriteTransactionAsync(() => ledger.AddAsync(movement, Cancel), Cancel);
    }

    /// <summary>
    /// Puts free leftover into production storage with a ledger Adjustment (adjustments are T-015), so it is free leftover
    /// (D-4) unless an InProduction order commits it.
    /// </summary>
    private static async Task PutLeftoverAsync(TestApiFactory app, int itemId, decimal quantity, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
        await LedgerAsync(app, new Movement
        {
            Type = MovementType.Adjustment, ItemId = itemId, ProductionStorageChange = quantity,
            OccurredAt = now, UserId = userId, Reason = "Leftover from an earlier run",
        });
    }

    /// <summary>
    /// What completing an InProduction order will do to production storage (T-011, AC-19.2): one ConsumedInProduction
    /// movement through the ledger, then status Completed. Only the parts the send tests rely on.
    /// </summary>
    private static async Task CompleteDirectlyAsync(TestApiFactory app, int number, int itemId, decimal consumed, string userId)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
            await LedgerAsync(app, new Movement
            {
                Type = MovementType.ConsumedInProduction, ItemId = itemId, ProductionStorageChange = -consumed,
                OccurredAt = now, UserId = userId, ProductionOrderNumber = number,
            });
        }

        await SetStatusAsync(app, number, OrderStatus.Completed, userId);
    }

    /// <summary>Changes an order's status directly in the database, with the matching time and user, as T-011 and T-016 will.</summary>
    private static async Task SetStatusAsync(TestApiFactory app, int number, OrderStatus status, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
        var order = await db.ProductionOrders.SingleAsync(candidate => candidate.Number == number, Cancel);
        order.Status = status;
        order.StatusChangedAt = now;
        switch (status)
        {
            case OrderStatus.Completed:
                order.CompletedAt = now;
                order.CompletedById = userId;
                break;
            case OrderStatus.Cancelled:
                order.CancelledAt = now;
                order.CancelledById = userId;
                order.CancellationReason = "Customer order withdrawn";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        await db.SaveChangesAsync(Cancel);
    }

    /// <summary>Inserts a leftover return directly (returns are T-019) and returns its number.</summary>
    private static async Task<int> InsertReturnAsync(TestApiFactory app, int itemId, decimal quantity, ReturnStatus status, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
        var leftoverReturn = new LeftoverReturn
        {
            ItemId = itemId, Quantity = quantity, Reason = "Not needed for the next run", Status = status,
            CreatedAt = now, CreatedById = userId,
        };
        db.Returns.Add(leftoverReturn);
        await db.SaveChangesAsync(Cancel);
        return leftoverReturn.Number;
    }

    /// <summary>Changes a return's status directly, as receiving or cancelling it will (T-019).</summary>
    private static async Task SetReturnStatusAsync(TestApiFactory app, int number, ReturnStatus status, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
        var leftoverReturn = await db.Returns.SingleAsync(candidate => candidate.Number == number, Cancel);
        leftoverReturn.Status = status;
        leftoverReturn.CancelledAt = now;
        leftoverReturn.CancelledById = userId;
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task<List<Movement>> MovementsOfOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ProductionOrderNumber == number)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    private static async Task<ProductionOrder> StoredOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductionOrders.AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Number == number, Cancel);
    }

    /// <summary>
    /// PRD 4.1 "rejected": the order is still Created with no sender, none of its lines has a send figure, and no
    /// movement names it.
    /// </summary>
    private static async Task AssertNothingSentAsync(TestApiFactory app, int number)
    {
        var order = await StoredOrderAsync(app, number);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Null(order.SentAt);
        Assert.Null(order.SentById);
        Assert.All(order.Lines, line =>
        {
            Assert.Null(line.LeftoverApplied);
            Assert.Null(line.WarehouseSend);
            Assert.Null(line.CarriedLeftover);
            Assert.Null(line.ComplementarySends);
        });
        Assert.Empty(await MovementsOfOrderAsync(app, number));
    }

    /// <summary>
    /// AC-15.6, design 6: <c>400</c> <c>send-out-of-range</c> whose <c>detail</c> is every message joined with a space,
    /// and whose <c>errors</c> hold exactly these paths, one message each.
    /// </summary>
    private static async Task AssertOutOfRangeAsync(HttpResponseMessage response, params (string Path, string Message)[] expected)
    {
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "send-out-of-range",
            string.Join(" ", expected.Select(error => error.Message)));
        Assert.Equal(expected.Select(error => error.Path), body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        foreach (var (path, message) in expected)
        {
            Assert.Equal([message], ApiJson.Messages(body, path));
        }
    }

    /// <summary>Design 5.7: a line after the send: no estimate, the send figures fixed, completion figures still null.</summary>
    private static void AssertSentLine(JsonElement line, decimal leftoverApplied, decimal warehouseSend, decimal carriedLeftover)
    {
        Assert.Equal(JsonValueKind.Null, line.GetProperty("estimate").ValueKind);
        Assert.Equal(leftoverApplied, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(warehouseSend, line.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(carriedLeftover, line.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(0m, line.GetProperty("complementarySends").GetDecimal());
        foreach (var field in new[] { "leftoverEntered", "leftoverRemaining", "actualUsage", "requiredForActualOutput",
                     "usageVariance", "usageVariancePercent", "usageVarianceAboveTolerance" })
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
        }
    }

    /// <summary>The stored send figures of a line (architecture 3.2, "fixed when: send"), and no completion figures yet.</summary>
    private static void AssertStoredLine(ProductionOrderLine line, decimal leftoverApplied, decimal warehouseSend, decimal carriedLeftover)
    {
        Assert.Equal(leftoverApplied, line.LeftoverApplied);
        Assert.Equal(warehouseSend, line.WarehouseSend);
        Assert.Equal(carriedLeftover, line.CarriedLeftover);
        Assert.Equal(0m, line.ComplementarySends);
        Assert.Null(line.LeftoverEntered);
        Assert.Null(line.ActualUsage);
    }

    /// <summary>D-2, AC-15.1: a SentToProduction movement of <paramref name="quantity"/>, linked to its order and sender.</summary>
    private static void AssertSendMovement(Movement movement, decimal quantity, string userId, DateTime at)
    {
        Assert.Equal(MovementType.SentToProduction, movement.Type);
        Assert.Equal(-quantity, movement.WarehouseChange);
        Assert.Equal(quantity, movement.ProductionStorageChange);
        Assert.Equal(userId, movement.UserId);
        Assert.Equal(at, movement.OccurredAt);
        Assert.Null(movement.ComplementaryOrderNumber);
    }

    /// <summary>AC-13.2: a Created order line's estimate, at the default 2% tolerance.</summary>
    private static void AssertEstimate(JsonElement line, decimal leftover, decimal nominal, decimal min, decimal max)
    {
        var estimate = line.GetProperty("estimate");
        Assert.Equal(leftover, estimate.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(nominal, estimate.GetProperty("nominalSend").GetDecimal());
        Assert.Equal(min, estimate.GetProperty("sendRangeMin").GetDecimal());
        Assert.Equal(max, estimate.GetProperty("sendRangeMax").GetDecimal());
    }
}
