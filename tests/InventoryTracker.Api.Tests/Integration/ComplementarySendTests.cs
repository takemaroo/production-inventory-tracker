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
/// T-018: sending an approved complementary order (US-18, D-3; design 3.7, 5.8 and 7.2):
/// <c>POST /api/v1/complementary-orders/{number}/send</c>.
/// </summary>
/// <remarks>
/// <para>
/// Complementary orders are requested and approved through the real T-017 endpoints. Statuses no endpoint can produce in
/// one step (a Cancelled one without ending a parent, a Sent one for the AC-18.3 checks) are inserted with
/// <see cref="OrderFlow.InsertComplementaryOrderAsync"/>.
/// </para>
/// <para>
/// Only one order can be InProduction per database (AC-15.10). The shared <see cref="RunningOrderApp"/> holds one
/// InProduction order for the whole class, with plenty of Warehouse stock; its tests compare figures before and after,
/// because earlier tests have already added complementary sends. Tests that need exact numbers, names or times, or that
/// end the parent, create their own factory.
/// </para>
/// </remarks>
public class ComplementarySendTests(ComplementarySendTests.RunningOrderApp factory)
    : IClassFixture<ComplementarySendTests.RunningOrderApp>
{
    /// <summary>The reason of design 5.8's example.</summary>
    private const string SpillReason = "Hopper spill at start of run";

    /// <summary>AC-18.3, design 7.2: the end of the <c>not-approved</c> message.</summary>
    private const string OnlyApproved = "Only an Approved complementary order can be sent.";

    /// <summary>AC-17.3, design 7.2: the end of the <c>not-pending-approval</c> message.</summary>
    private const string OnlyPending = "Only a PendingApproval complementary order can be approved or rejected.";

    /// <summary>AC-18.2, design 7.2: the range message for 20 kg of Cheese at t = 2%.</summary>
    private const string CheeseRange = "Cheese: send must be between 19.6 and 20.4 kg (requested 20 kg).";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-18.1: the send ------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-18.1, AC-05.3, D-3, design 5.5, 5.6, 5.7 and 5.8. Order #1 is cancelled; order #2 (Cheese 1 kg and Potato starch
    /// 0.015 kg per bag, 1,000 bags) is sent with 1,000 and 15 kg. Complementary order #1 is requested at 11:20 (Potato
    /// starch 0.5 kg, then Cheese 20 kg) and approved at 11:30. At 11:45 the Warehouse user jorge.ibarra@example.com sends
    /// Cheese 20.4 and Potato starch 0.49 (body order differs from the lines). <c>200</c>: Sent, the lines in the order
    /// requested with their sent quantities and no send range any more, and the history PendingApproval, Approved, Sent
    /// (11:45, Jorge). <c>GET</c> returns the same body. Two SentToProduction movements at 11:45 by Jorge, each linked to
    /// complementary order #1 AND its parent #2, found by <c>?complementaryOrderNumber=1</c> and with the order's own
    /// sends by <c>?orderNumber=2</c>. The parent stays InProduction with an unchanged history, and its lines'
    /// <c>complementarySends</c> are 20.4 and 0.49; <c>GET /stock</c> shows the committed amounts grown to 1,020.4 and
    /// 15.49 (D-3) and the Warehouse down by the sent quantities. No notification is raised, and the send is logged.
    /// </summary>
    [Fact]
    public async Task Warehouse_user_sends_an_approved_complementary_order()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var jorge = await app.CreateUserAsync("jorge.ibarra@example.com", Roles.Warehouse);
        using var warehouse = app.CreateClientWithToken(await app.SignInAsync(jorge.Email!));
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 1200m);
        await ReceiveAsync(manager, starch.Id, 20m);
        await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 10m), OrderStatus.Cancelled, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(2, number);
        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m))).StatusCode);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(20));
        var complementary = await RequestedComplementaryAsync(production, number, SpillReason, (starch.Id, 0.5m), (cheese.Id, 20m));
        Assert.Equal(1, complementary);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(manager, complementary)).StatusCode);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(45));

        var response = await SendComplementaryAsync(warehouse, complementary, (cheese.Id, 20.4m), (starch.Id, 0.49m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        AssertShape(body);
        Assert.Equal(1, body.GetProperty("number").GetInt32());
        Assert.Equal(2, body.GetProperty("orderNumber").GetInt32());
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([starch.Id, cheese.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
        AssertSentLine(lines[0], "Potato starch", requested: 0.5m, sent: 0.49m);
        AssertSentLine(lines[1], "Cheese", requested: 20m, sent: 20.4m);

        // AC-05.3, design 5.8: each step with who and when, oldest first.
        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["PendingApproval", "Approved", "Sent"], history.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal(productionUser.Id, history[0].GetProperty("by").GetProperty("id").GetString());
        Assert.Equal("2026-11-11T11:30:00Z", history[1].GetProperty("at").GetString());
        Assert.Equal(managerUser.Id, history[1].GetProperty("by").GetProperty("id").GetString());
        Assert.Equal("2026-11-11T11:45:00Z", history[2].GetProperty("at").GetString());
        Assert.Equal(jorge.Id, history[2].GetProperty("by").GetProperty("id").GetString());
        Assert.Equal("jorge.ibarra@example.com", history[2].GetProperty("by").GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, history[2].GetProperty("note").ValueKind);
        Assert.Equal(body.GetRawText(), (await ReadComplementaryAsync(manager, complementary)).GetRawText());
        var stored = await StoredComplementaryOrderAsync(app, complementary);
        Assert.Equal(new DateTime(2026, 11, 11, 11, 45, 0, DateTimeKind.Utc), stored.SentAt);
        Assert.Equal(jorge.Id, stored.SentById);

        // AC-18.1, design 5.6: one movement per line, linked to the complementary order and to its parent.
        var sent = (await MovementsAsync(manager, $"complementaryOrderNumber={complementary}")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, sent.Count);
        AssertComplementaryMovement(sent, cheese.Id, 20.4m, number, complementary, jorge.Id, "2026-11-11T11:45:00Z");
        AssertComplementaryMovement(sent, starch.Id, 0.49m, number, complementary, jorge.Id, "2026-11-11T11:45:00Z");
        var ofOrder = await MovementsAsync(manager, $"orderNumber={number}");
        Assert.Equal(4, ofOrder.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, ofOrder.GetProperty("items").EnumerateArray()
            .Count(movement => movement.GetProperty("complementaryOrderNumber").ValueKind == JsonValueKind.Number));

        // D-3, design 5.7: the parent's lines carry the complementary sends; nothing else about the parent changes.
        var parent = await ReadOrderAsync(manager, number);
        Assert.Equal("InProduction", parent.GetProperty("status").GetString());
        Assert.Equal(["Created", "InProduction"],
            parent.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal(20.4m, Line(parent, cheese.Id).GetProperty("complementarySends").GetDecimal());
        Assert.Equal(1000m, Line(parent, cheese.Id).GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0.49m, Line(parent, starch.Id).GetProperty("complementarySends").GetDecimal());
        var linked = Assert.Single(parent.GetProperty("complementaryOrders").EnumerateArray());
        Assert.Equal("Sent", linked.GetProperty("status").GetString());

        // D-3, design 5.5: the committed amounts include the complementary sends, so none of it is free leftover (D-4).
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 179.6m, production: 1020.4m, committed: 1020.4m, number);
        AssertStock(await StockAsync(manager, starch.Id), warehouse: 4.51m, production: 15.49m, committed: 15.49m, number);

        // AC-23.2 has no notification for a send: only the request's ComplementaryAwaitingApproval exists.
        Assert.Equal([NotificationType.ComplementaryAwaitingApproval],
            (await NotificationsOfOrderAsync(app, number)).Select(notification => notification.Type));
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Complementary order 1 for order 2 sent to production by user {jorge.Id}.");
    }

    // ---- AC-18.2: the range ----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-18.2: requested 20 kg at t = 2%. Sends of 19.6, 20 and 20.4 kg (both limits included) are accepted: <c>200</c>
    /// Sent with that sent quantity, one movement of exactly that quantity linked to the complementary order, and the
    /// parent's Cheese line's complementary sends grown by exactly that much. A Manager sends the first (PRD 4.2).
    /// </summary>
    [Theory]
    [InlineData(19.6)]
    [InlineData(20)]
    [InlineData(20.4)]
    public async Task Sends_within_the_range_are_accepted(decimal quantity)
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var sendsBefore = await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id);
        var sender = quantity == 19.6m ? running.Manager : running.Warehouse;

        var response = await SendComplementaryAsync(sender, complementary, (running.Cheese.Id, quantity));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Equal(quantity, Assert.Single(body.GetProperty("lines").EnumerateArray()).GetProperty("sentQuantity").GetDecimal());
        var movement = Assert.Single(await MovementsOfComplementaryAsync(factory, complementary));
        Assert.Equal(-quantity, movement.WarehouseChange);
        Assert.Equal(quantity, movement.ProductionStorageChange);
        Assert.Equal(running.Number, movement.ProductionOrderNumber);
        Assert.Equal(sendsBefore + quantity, await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id));
    }

    /// <summary>
    /// AC-18.2, design 7.2: requested 20 kg; 19.599 and 20.401 kg are refused with <c>400</c> <c>send-out-of-range</c>
    /// "Cheese: send must be between 19.6 and 20.4 kg (requested 20 kg)." under <c>lines[0].quantity</c>. Nothing is
    /// saved: still Approved, no movement, the parent line and the Warehouse unchanged.
    /// </summary>
    [Theory]
    [InlineData(19.599)]
    [InlineData(20.401)]
    public async Task Sends_outside_the_range_are_rejected(decimal quantity)
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var before = await SnapshotAsync(running);

        var response = await SendComplementaryAsync(running.Warehouse, complementary, (running.Cheese.Id, quantity));

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "send-out-of-range", CheeseRange);
        Assert.Equal([CheeseRange], ApiJson.Messages(body, "lines[0].quantity"));
        await AssertNothingSentAsync(running, complementary, before);
    }

    /// <summary>
    /// AC-18.1, design 7.2, decision #96: every failing line is named with its own range, in body order, in one
    /// <c>400</c> <c>send-out-of-range</c>; a line in range is not mentioned. Requested Cheese 20 kg, Potato starch 0.5
    /// kg and Salt 2 kg; sent Potato starch 0.52, Salt 2 (in range) and Cheese 19. Nothing is saved.
    /// </summary>
    [Fact]
    public async Task Every_line_outside_its_range_is_named_in_body_order()
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason,
            (running.Cheese.Id, 20m), (running.Starch.Id, 0.5m), (running.Salt.Id, 2m));
        var before = await SnapshotAsync(running);

        var response = await SendComplementaryAsync(running.Warehouse, complementary,
            (running.Starch.Id, 0.52m), (running.Salt.Id, 2m), (running.Cheese.Id, 19m));

        const string starch = "Potato starch: send must be between 0.49 and 0.51 kg (requested 0.5 kg).";
        const string cheese = "Cheese: send must be between 19.6 and 20.4 kg (requested 20 kg).";
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "send-out-of-range", $"{starch} {cheese}");
        Assert.Equal([starch], ApiJson.Messages(body, "lines[0].quantity"));
        Assert.Equal([cheese], ApiJson.Messages(body, "lines[2].quantity"));
        Assert.Equal(2, body.GetProperty("errors").EnumerateObject().Count());
        await AssertNothingSentAsync(running, complementary, before);
    }

    /// <summary>
    /// AC-18.1 with t = 100% (D-1: the tolerance is a setting from 0 to 100): requested Cheese 20 kg and Potato starch
    /// 0.5 kg give ranges 0 to 40 and 0 to 1, so a send of 0 is in range. Sending Cheese 0 and Potato starch 1:
    /// <c>200</c> Sent, Cheese sent quantity 0 with no movement (PRD section 6 row 17: no movement for a zero quantity),
    /// one movement of 1 kg of starch, and the parent's complementary sends 0 and 1.
    /// </summary>
    [Fact]
    public async Task A_line_of_zero_moves_nothing()
    {
        await using var app = new TestApiFactory();
        app.Settings["Inventory:TolerancePercent"] = "100";
        var scene = await TwoMaterialSceneAsync(app, cheeseInWarehouse: 1100m, starchInWarehouse: 20m);
        var complementary = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason,
            (scene.Cheese.Id, 20m), (scene.Starch.Id, 0.5m));
        Assert.Equal(0m, Assert.Single((await ReadComplementaryAsync(scene.Manager, complementary)).GetProperty("lines")
            .EnumerateArray(), line => line.GetProperty("itemId").GetInt32() == scene.Cheese.Id).GetProperty("sendRangeMin").GetDecimal());

        var response = await SendComplementaryAsync(scene.Warehouse, complementary, (scene.Cheese.Id, 0m), (scene.Starch.Id, 1m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await ApiJson.ReadAsync(response)).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(0m, lines[0].GetProperty("sentQuantity").GetDecimal());
        Assert.Equal(1m, lines[1].GetProperty("sentQuantity").GetDecimal());
        var movement = Assert.Single(await MovementsOfComplementaryAsync(app, complementary));
        Assert.Equal(scene.Starch.Id, movement.ItemId);
        Assert.Equal(1m, movement.ProductionStorageChange);
        var parent = await ReadOrderAsync(scene.Manager, scene.Number);
        Assert.Equal(0m, Line(parent, scene.Cheese.Id).GetProperty("complementarySends").GetDecimal());
        Assert.Equal(1m, Line(parent, scene.Starch.Id).GetProperty("complementarySends").GetDecimal());
    }

    // ---- AC-18.3: only an Approved one, once -------------------------------------------------------------------------

    /// <summary>
    /// AC-18.3, design 7.2: a complementary order that is PendingApproval, Rejected, Sent or Cancelled is refused with
    /// <c>409</c> <c>not-approved</c> "Complementary order #N is {status}. Only an Approved complementary order can be
    /// sent.", with an in-range body. Nothing moves.
    /// </summary>
    [Theory]
    [InlineData(ComplementaryOrderStatus.PendingApproval)]
    [InlineData(ComplementaryOrderStatus.Rejected)]
    [InlineData(ComplementaryOrderStatus.Sent)]
    [InlineData(ComplementaryOrderStatus.Cancelled)]
    public async Task Only_an_approved_complementary_order_can_be_sent(ComplementaryOrderStatus status)
    {
        var running = await factory.RunningAsync();
        var complementary = await InsertComplementaryOrderAsync(factory, running.Number, status, running.Cheese.Id, running.ManagerUser.Id);
        var before = await SnapshotAsync(running);

        var response = await SendComplementaryAsync(running.Warehouse, complementary, (running.Cheese.Id, 20m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "not-approved",
            $"Complementary order #{complementary} is {status}. {OnlyApproved}");
        Assert.Equal(status, (await StoredComplementaryOrderAsync(factory, complementary)).Status);
        Assert.Empty(await MovementsOfComplementaryAsync(factory, complementary));
        Assert.Equal(before, await SnapshotAsync(running));
    }

    /// <summary>
    /// AC-18.3, AC-17.2, through the real endpoints: a request still waiting is refused ("… is PendingApproval. …"); a
    /// rejected one can never be sent ("… is Rejected. …"); an approved one is sent once, and sending it again is refused
    /// ("… is Sent. …"), leaving exactly one movement and the parent line grown by 20 once.
    /// </summary>
    [Fact]
    public async Task Pending_rejected_and_already_sent_orders_cannot_be_sent()
    {
        var running = await factory.RunningAsync();
        var pending = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var rejected = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(running.Manager, rejected, "Use the spare from order #11")).StatusCode);
        var approved = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var sendsBefore = await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id);

        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Warehouse, pending, (running.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "not-approved", $"Complementary order #{pending} is PendingApproval. {OnlyApproved}");
        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Warehouse, rejected, (running.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "not-approved", $"Complementary order #{rejected} is Rejected. {OnlyApproved}");
        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(running.Warehouse, approved, (running.Cheese.Id, 20m))).StatusCode);
        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Warehouse, approved, (running.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "not-approved", $"Complementary order #{approved} is Sent. {OnlyApproved}");

        Assert.Single(await MovementsOfComplementaryAsync(factory, approved));
        Assert.Empty(await MovementsOfComplementaryAsync(factory, pending));
        Assert.Empty(await MovementsOfComplementaryAsync(factory, rejected));
        Assert.Equal(sendsBefore + 20m, await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id));
    }

    /// <summary>
    /// AC-16.4, D-3: a parent's complementary orders each have their own send, and each send adds to the same parent
    /// lines. Two approved orders (Cheese 20; Cheese 10 and Potato starch 0.2) sent with 20.4, and 9.8 and 0.204: the
    /// parent's Cheese line grows by 30.2 and its starch line by 0.204, in the order detail and in <c>GET /stock</c>'s
    /// committed amount; the Warehouse falls by the same amounts.
    /// </summary>
    [Fact]
    public async Task Several_complementary_sends_add_up_on_the_parent_line()
    {
        var running = await factory.RunningAsync();
        var first = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var second = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason,
            (running.Cheese.Id, 10m), (running.Starch.Id, 0.2m));
        var cheeseBefore = await StockAsync(running.Manager, running.Cheese.Id);
        var starchBefore = await StockAsync(running.Manager, running.Starch.Id);
        var detailBefore = await ReadOrderAsync(running.Manager, running.Number);

        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(running.Warehouse, first, (running.Cheese.Id, 20.4m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendComplementaryAsync(running.Warehouse, second, (running.Starch.Id, 0.204m), (running.Cheese.Id, 9.8m))).StatusCode);

        var detail = await ReadOrderAsync(running.Manager, running.Number);
        Assert.Equal(Sends(detailBefore, running.Cheese.Id) + 30.2m, Sends(detail, running.Cheese.Id));
        Assert.Equal(Sends(detailBefore, running.Starch.Id) + 0.204m, Sends(detail, running.Starch.Id));
        var cheese = await StockAsync(running.Manager, running.Cheese.Id);
        Assert.Equal(Figure(cheeseBefore, "committedToOrder") + 30.2m, Figure(cheese, "committedToOrder"));
        Assert.Equal(Figure(cheeseBefore, "warehouse") - 30.2m, Figure(cheese, "warehouse"));
        Assert.Equal(Figure(cheeseBefore, "productionStorage") + 30.2m, Figure(cheese, "productionStorage"));
        Assert.Equal(Figure(starchBefore, "committedToOrder") + 0.204m, Figure(await StockAsync(running.Manager, running.Starch.Id), "committedToOrder"));

        static decimal Sends(JsonElement order, int itemId) => Line(order, itemId).GetProperty("complementarySends").GetDecimal();
    }

    // ---- AC-18.4: Warehouse stock -----------------------------------------------------------------------------------

    /// <summary>
    /// AC-18.4, design 7.2, decision #96. After the parent's send the Warehouse holds 0 kg of Cheese and 0.3 kg of Potato
    /// starch. Sending Potato starch 0.5 and Cheese 20 is refused with <c>409</c> <c>insufficient-stock</c> naming both
    /// short lines with what the Warehouse holds, in body order. After 20 kg of Cheese arrives, only the starch line is
    /// named. After 0.2 kg of starch arrives, the send succeeds and takes both to exactly 0 (allowed, as in AC-12.3).
    /// Nothing is saved by a refused send.
    /// </summary>
    [Fact]
    public async Task Short_warehouse_stock_names_every_short_line()
    {
        await using var app = new TestApiFactory();
        var scene = await TwoMaterialSceneAsync(app, cheeseInWarehouse: 1000m, starchInWarehouse: 15.3m);
        var complementary = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason,
            (scene.Cheese.Id, 20m), (scene.Starch.Id, 0.5m));

        await ApiAssert.ProblemAsync(
            await SendComplementaryAsync(scene.Warehouse, complementary, (scene.Starch.Id, 0.5m), (scene.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "insufficient-stock",
            "Not enough stock: Potato starch in Warehouse has 0.3 kg. Not enough stock: Cheese in Warehouse has 0 kg.");
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 20m);
        await ApiAssert.ProblemAsync(
            await SendComplementaryAsync(scene.Warehouse, complementary, (scene.Starch.Id, 0.5m), (scene.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "insufficient-stock", "Not enough stock: Potato starch in Warehouse has 0.3 kg.");
        Assert.Equal(ComplementaryOrderStatus.Approved, (await StoredComplementaryOrderAsync(app, complementary)).Status);
        Assert.Empty(await MovementsOfComplementaryAsync(app, complementary));
        Assert.Equal(0m, Line(await ReadOrderAsync(scene.Manager, scene.Number), scene.Cheese.Id).GetProperty("complementarySends").GetDecimal());

        await ReceiveAsync(scene.Manager, scene.Starch.Id, 0.2m);
        Assert.Equal(HttpStatusCode.OK,
            (await SendComplementaryAsync(scene.Warehouse, complementary, (scene.Starch.Id, 0.5m), (scene.Cheese.Id, 20m))).StatusCode);

        Assert.Equal(0m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse"));
        Assert.Equal(0m, Figure(await StockAsync(scene.Manager, scene.Starch.Id), "warehouse"));
    }

    // ---- The body (design 2.2 step 4) ------------------------------------------------------------------------------

    /// <summary>
    /// AC-18.1 ("a quantity for every line"), design 7.2, decisions #80, #95, #96. The complementary order requests Cheese
    /// 20 kg and Potato starch 0.5 kg. Refused with <c>400</c> <c>validation</c>, one message under its JSON path: no
    /// lines, <c>null</c> lines or an empty list: "Every order line needs a quantity. Missing: Cheese, Potato starch."
    /// (in the parent's recipe line order); one line left out: "… Missing: Potato starch."; a line without an item: "Item
    /// is required."; an item that is not on the complementary order, existing or not: "Item {id} is not on complementary
    /// order #{c}."; the same item twice: "Cheese appears on more than one line." under the repeat; a quantity missing or
    /// <c>null</c>: "Quantity is required."; negative: "Quantity cannot be negative."; more than 3 decimal places:
    /// "Quantity can have at most 3 decimal places."; above 999,999,999.999: "Quantity must be 999,999,999.999 or less.",
    /// a field error before any range check. Nothing is saved.
    /// </summary>
    /// <remarks>
    /// In the templates, <c>{cheese}</c>, <c>{starch}</c> and <c>{other}</c> are replaced by item ids and <c>{c}</c> by the
    /// complementary order number. <c>{other}</c> is on the parent order but not on this complementary order.
    /// </remarks>
    [Theory]
    [InlineData("""{ }""", "lines", "Every order line needs a quantity. Missing: Cheese, Potato starch.")]
    [InlineData("""{ "lines": null }""", "lines", "Every order line needs a quantity. Missing: Cheese, Potato starch.")]
    [InlineData("""{ "lines": [] }""", "lines", "Every order line needs a quantity. Missing: Cheese, Potato starch.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20 } ] }""", "lines", "Every order line needs a quantity. Missing: Potato starch.")]
    [InlineData("""{ "lines": [ { "quantity": 1 }, { "itemId": {cheese}, "quantity": 20 }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].itemId", "Item is required.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20 }, { "itemId": {starch}, "quantity": 0.5 }, { "itemId": {other}, "quantity": 1 } ] }""", "lines[2].itemId", "Item {other} is not on complementary order #{c}.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20 }, { "itemId": 999999, "quantity": 1 }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[1].itemId", "Item 999999 is not on complementary order #{c}.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20 }, { "itemId": {starch}, "quantity": 0.5 }, { "itemId": {cheese}, "quantity": 20 } ] }""", "lines[2].itemId", "Cheese appears on more than one line.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese} }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].quantity", "Quantity is required.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": null }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].quantity", "Quantity is required.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20 }, { "itemId": {starch}, "quantity": -0.5 } ] }""", "lines[1].quantity", "Quantity cannot be negative.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 20.0001 }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].quantity", "Quantity can have at most 3 decimal places.")]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 1000000000 }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].quantity", ExpectedMessages.QuantityAboveMax)]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "quantity": 999999999999999999999 }, { "itemId": {starch}, "quantity": 0.5 } ] }""", "lines[0].quantity", ExpectedMessages.QuantityAboveMax)]
    public async Task Invalid_send_is_rejected(string template, string path, string message)
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason,
            (running.Cheese.Id, 20m), (running.Starch.Id, 0.5m));
        string Fill(string text) => text
            .Replace("{cheese}", running.Cheese.Id.ToString(), StringComparison.Ordinal)
            .Replace("{starch}", running.Starch.Id.ToString(), StringComparison.Ordinal)
            .Replace("{other}", running.Salt.Id.ToString(), StringComparison.Ordinal)
            .Replace("{c}", complementary.ToString(), StringComparison.Ordinal);
        var before = await SnapshotAsync(running);

        var response = await SendRawAsync(running.Warehouse, complementary, Fill(template));

        await ApiAssert.ValidationAsync(response, path, Fill(message));
        await AssertNothingSentAsync(running, complementary, before);
    }

    /// <summary>
    /// Decision #80, AC-18.1, design 2.2: 999,999,999.999 passes the input check (it is the maximum), so the range decides:
    /// <c>400</c> <c>send-out-of-range</c>, not <c>validation</c>, even though the Warehouse could not cover it either
    /// (the range, step 6, comes before the stock, step 7).
    /// </summary>
    [Fact]
    public async Task The_maximum_passes_the_input_check_and_meets_the_range_check()
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));

        var response = await SendComplementaryAsync(running.Warehouse, complementary, (running.Cheese.Id, 999_999_999.999m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "send-out-of-range", CheeseRange);
    }

    /// <summary>
    /// Design 6, decisions #74, #75, #96: every body problem is reported in one <c>400</c>, one message per field, keyed by
    /// its JSON path, with <c>detail</c> joining them in the order found: a negative Cheese quantity, a line sent as
    /// <c>null</c> (both "required" messages), a repeated Cheese with too many decimals, and Potato starch missing.
    /// </summary>
    [Fact]
    public async Task Every_problem_is_reported_in_one_response()
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason,
            (running.Cheese.Id, 20m), (running.Starch.Id, 0.5m));
        var cheese = running.Cheese.Id;
        var json = $$"""
            { "lines": [ { "itemId": {{cheese}}, "quantity": -1 }, null, { "itemId": {{cheese}}, "quantity": 1.0001 } ] }
            """;

        var response = await SendRawAsync(running.Warehouse, complementary, json);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Quantity cannot be negative. Item is required. Quantity is required. Cheese appears on more than one line. " +
            "Quantity can have at most 3 decimal places. Every order line needs a quantity. Missing: Potato starch.");
        Assert.Equal(["Quantity cannot be negative."], ApiJson.Messages(body, "lines[0].quantity"));
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[1].itemId"));
        Assert.Equal(["Quantity is required."], ApiJson.Messages(body, "lines[1].quantity"));
        Assert.Equal(["Cheese appears on more than one line."], ApiJson.Messages(body, "lines[2].itemId"));
        Assert.Equal(["Quantity can have at most 3 decimal places."], ApiJson.Messages(body, "lines[2].quantity"));
        Assert.Equal(["Every order line needs a quantity. Missing: Potato starch."], ApiJson.Messages(body, "lines"));
        Assert.Equal(6, body.GetProperty("errors").EnumerateObject().Count());
    }

    /// <summary>
    /// Architecture 4.1 item 2: a missing body is <c>400</c> "A request body is required."; a body that is not JSON is
    /// "The request body is not valid JSON."; a quantity sent as a string is "lines[0].quantity has the wrong type."
    /// (design 2.4). Nothing is saved.
    /// </summary>
    [Fact]
    public async Task Unreadable_bodies_are_rejected()
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var before = await SnapshotAsync(running);

        await ApiAssert.ProblemAsync(await SendRawAsync(running.Warehouse, complementary, ""),
            HttpStatusCode.BadRequest, "validation", "A request body is required.");
        await ApiAssert.ProblemAsync(await SendRawAsync(running.Warehouse, complementary, "{ \"lines\": ["),
            HttpStatusCode.BadRequest, "validation", "The request body is not valid JSON.");
        await ApiAssert.ValidationAsync(
            await SendRawAsync(running.Warehouse, complementary,
                $$"""{ "lines": [ { "itemId": {{running.Cheese.Id}}, "quantity": "20" } ] }"""),
            "lines[0].quantity", "lines[0].quantity has the wrong type.");

        await AssertNothingSentAsync(running, complementary, before);
    }

    /// <summary>
    /// Design 2.2 check order: <c>403</c> before <c>404</c> (a Production user, unknown number); <c>404</c>
    /// "Complementary order #999999 not found." before the body; the body (<c>400</c>) before the state (<c>409</c>), so
    /// a Sent one with no lines is <c>400</c>; the state before the range, so a Sent one with an out-of-range quantity is
    /// <c>409</c>; the range before the stock (see <see cref="The_maximum_passes_the_input_check_and_meets_the_range_check"/>).
    /// </summary>
    [Fact]
    public async Task Checks_run_in_the_design_order()
    {
        var running = await factory.RunningAsync();
        var sent = await InsertComplementaryOrderAsync(factory, running.Number, ComplementaryOrderStatus.Sent, running.Cheese.Id, running.ManagerUser.Id);

        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Production, 999999),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Warehouse, 999999),
            HttpStatusCode.NotFound, "not-found", "Complementary order #999999 not found.");
        await ApiAssert.ValidationAsync(await SendComplementaryAsync(running.Warehouse, sent),
            "lines", "Every order line needs a quantity. Missing: Cheese.");
        await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Warehouse, sent, (running.Cheese.Id, 50m)),
            HttpStatusCode.Conflict, "not-approved", $"Complementary order #{sent} is Sent. {OnlyApproved}");
    }

    // ---- Worked examples A and B through the real endpoints (AC-20.5, AC-20.6) --------------------------------------

    /// <summary>
    /// AC-20.5 (worked example A) and AC-20.6 (worked example B), D-3, D-9, with every step through the real API: order
    /// #12, 1,000 bags at 1 kg Cheese per bag, 5 kg of free leftover (applied), 995 kg sent at 10:02. At 11:20 the
    /// Production user requests <paramref name="complementary"/> kg, a Manager approves it, and the Warehouse sends exactly
    /// that. The parent's line then shows complementary sends of that amount and production storage and committed material
    /// of 1,000 + it (design 5.5). The completion (1,001 bags, leftover <paramref name="leftover"/>) counts the
    /// complementary send in the actual usage: A: 5 + 995 + 20 − 4 = 1,016 kg, variance +15 (+1.50%, not above 2%), the
    /// OutputVariance and LeftoverAtCompletion notifications; B: 5 + 995 + 30 − 0 = 1,030 kg, variance +29 (+2.90%, above),
    /// the UsageVariance and OutputVariance notifications. The ConsumedInProduction movement is the actual usage.
    /// </summary>
    [Theory]
    [InlineData(20, 4, "Last bag line jammed", 1016, 15, 1.5, false, new[] { NotificationType.OutputVariance, NotificationType.LeftoverAtCompletion })]
    [InlineData(30, 0, null, 1030, 29, 2.9, true, new[] { NotificationType.UsageVariance, NotificationType.OutputVariance })]
    public async Task Worked_examples_A_and_B_count_the_complementary_send_in_the_usage(
        decimal complementary, decimal leftover, string? reason, decimal usage, decimal variance, decimal percent, bool above,
        NotificationType[] notifications)
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
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
        var number = await ApprovedComplementaryAsync(production, manager, 12, SpillReason, (cheese.Id, complementary));

        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(warehouse, number, (cheese.Id, complementary))).StatusCode);

        var running = Line(await ReadOrderAsync(manager, 12), cheese.Id);
        Assert.Equal(5m, running.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(995m, running.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(complementary, running.GetProperty("complementarySends").GetDecimal());
        AssertStock(await StockAsync(manager, cheese.Id), warehouse: 1200m - 995m - complementary,
            production: 1000m + complementary, committed: 1000m + complementary, orderNumber: 12);

        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(40));
        var completed = await CompleteAsync(production, 12, 1001m, reason, (cheese.Id, leftover));

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var line = Line(await ApiJson.ReadAsync(completed), cheese.Id);
        Assert.Equal(complementary, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(usage, line.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(1001m, line.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(variance, line.GetProperty("usageVariance").GetDecimal());
        Assert.Equal(percent, line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(above, line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
        var consumed = Assert.Single(await MovementsOfOrderAsync(app, 12), movement => movement.Type == MovementType.ConsumedInProduction);
        Assert.Equal(-usage, consumed.ProductionStorageChange);
        Assert.Equal(leftover, Figure(await StockAsync(manager, cheese.Id), "productionStorage"));

        // AC-20.5 and AC-20.6, "Total: 2 notifications" from the completion (the request's own notification is US-16's).
        Assert.Equal(notifications, (await NotificationsOfOrderAsync(app, 12))
            .Where(notification => notification.Type != NotificationType.ComplementaryAwaitingApproval)
            .Select(notification => notification.Type));
        Assert.Equal(ComplementaryOrderStatus.Sent, (await StoredComplementaryOrderAsync(app, number)).Status);
    }

    // ---- Concurrency (PRD section 5, architecture 5.2) -------------------------------------------------------------

    /// <summary>
    /// AC-18.3, architecture 5.2: two Warehouse users send the same complementary order at the same moment. Exactly one
    /// gets <c>200</c>; the other gets <c>409</c> "Complementary order #N is Sent. …". There is exactly one movement, the
    /// parent line grows by 20 once, and the stored sender is the winner. Three rounds.
    /// </summary>
    [Fact]
    public async Task Simultaneous_sends_succeed_once()
    {
        var running = await factory.RunningAsync();
        var (first, firstUser) = await SignedInAsync(factory, Roles.Warehouse);
        var (second, secondUser) = await SignedInAsync(factory, Roles.Warehouse);

        for (var round = 0; round < 3; round++)
        {
            var number = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
            var sendsBefore = await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id);

            var responses = await Task.WhenAll(
                SendComplementaryAsync(first, number, (running.Cheese.Id, 20m)),
                SendComplementaryAsync(second, number, (running.Cheese.Id, 20m)));

            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(responses[1 - winner], HttpStatusCode.Conflict, "not-approved",
                $"Complementary order #{number} is Sent. {OnlyApproved}");
            Assert.Single(await MovementsOfComplementaryAsync(factory, number));
            Assert.Equal(sendsBefore + 20m, await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id));
            Assert.Equal(winner == 0 ? firstUser.Id : secondUser.Id, (await StoredComplementaryOrderAsync(factory, number)).SentById);
        }
    }

    /// <summary>
    /// AC-18.3, architecture 5.2, forced order: a second send arriving while the first holds the write lock waits, then
    /// finds Sent and gets <c>409</c>; one movement, the parent line grown once.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_send_is_refused()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (first, second) = await race.RunAsync(
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)),
            () => SendComplementaryAsync(scene.Manager, number, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ApiAssert.ProblemAsync(second, HttpStatusCode.Conflict, "not-approved", $"Complementary order #{number} is Sent. {OnlyApproved}");
        Assert.Single(await MovementsOfComplementaryAsync(race.App, number));
        Assert.Equal(20m, await ComplementarySendsAsync(race.App, scene.Number, scene.Cheese.Id));
        Assert.Equal(80m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse"));
    }

    /// <summary>
    /// PRD section 5, D-9, forced order "send, then complete": a completion arriving while the send holds the write lock
    /// waits, then counts the send. The send got <c>200</c> Sent; the completion (1,000 units, no leftover) shows
    /// complementary sends 20, actual usage 1,000 + 20 = 1,020, variance +20 (+2.00%, not above: limits are inclusive);
    /// its ConsumedInProduction removes 1,020; the complementary order stays Sent.
    /// </summary>
    [Fact]
    public async Task Completion_waiting_behind_a_send_counts_it()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (send, complete) = await race.RunAsync(
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)),
            () => CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await ApiJson.ReadAsync(complete);
        var line = Line(completed, scene.Cheese.Id);
        Assert.Equal(20m, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(1020m, line.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(20m, line.GetProperty("usageVariance").GetDecimal());
        Assert.False(line.GetProperty("usageVarianceAboveTolerance").GetBoolean());
        Assert.Equal("Sent", Assert.Single(completed.GetProperty("complementaryOrders").EnumerateArray()).GetProperty("status").GetString());
        var consumed = Assert.Single(await MovementsOfOrderAsync(race.App, scene.Number), movement => movement.Type == MovementType.ConsumedInProduction);
        Assert.Equal(-1020m, consumed.ProductionStorageChange);
        Assert.Equal(0m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "productionStorage"));
    }

    /// <summary>
    /// AC-19.6, AC-18.3, forced order "complete, then send": a send arriving while the completion holds the write lock
    /// waits; by then the completion has cancelled the approved order ("Parent order completed"), so the send gets
    /// <c>409</c> "Complementary order #N is Cancelled. …". Nothing moves: the Warehouse keeps its 100, there is no
    /// complementary movement, and the completion's usage is 1,000 with complementary sends 0.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_completion_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (complete, send) = await race.RunAsync(
            () => CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)),
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var line = Line(await ApiJson.ReadAsync(complete), scene.Cheese.Id);
        Assert.Equal(0m, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(1000m, line.GetProperty("actualUsage").GetDecimal());
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "not-approved", $"Complementary order #{number} is Cancelled. {OnlyApproved}");
        var stored = await StoredComplementaryOrderAsync(race.App, number);
        Assert.Equal("Parent order completed", stored.CancellationNote);
        Assert.Null(stored.SentAt);
        Assert.Empty(await MovementsOfComplementaryAsync(race.App, number));
        Assert.Equal(100m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse"));
    }

    /// <summary>
    /// PRD section 5, AC-18.3, AC-19.6: a send and the completion of its parent at the same moment end in one consistent
    /// outcome. The completion always succeeds. Either the send ran first (<c>200</c>) and the completion counts it
    /// (complementary sends 20, usage 1,020), or the send got <c>409</c> "… is Cancelled. …" and nothing moved (usage
    /// 1,000, the Warehouse keeps its 100). Never a send recorded on a completed order. Three rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Send_racing_the_completion_of_its_parent_ends_consistently()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);

        for (var round = 0; round < 3; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1100m);
            var order = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, order, (cheese.Id, 1000m))).StatusCode);
            var number = await ApprovedComplementaryAsync(production, manager, order, SpillReason, (cheese.Id, 20m));

            var responses = await Task.WhenAll(
                SendComplementaryAsync(warehouse, number, (cheese.Id, 20m)),
                CompleteAsync(production, order, 1000m, null, (cheese.Id, 0m)));

            var (send, complete) = (responses[0], responses[1]);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            var line = Line(await ApiJson.ReadAsync(complete), cheese.Id);
            if (send.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(20m, line.GetProperty("complementarySends").GetDecimal());
                Assert.Equal(1020m, line.GetProperty("actualUsage").GetDecimal());
                Assert.Equal(80m, Figure(await StockAsync(manager, cheese.Id), "warehouse"));
            }
            else
            {
                await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "not-approved", $"Complementary order #{number} is Cancelled. {OnlyApproved}");
                Assert.Equal(0m, line.GetProperty("complementarySends").GetDecimal());
                Assert.Equal(1000m, line.GetProperty("actualUsage").GetDecimal());
                Assert.Empty(await MovementsOfComplementaryAsync(app, number));
                Assert.Equal(100m, Figure(await StockAsync(manager, cheese.Id), "warehouse"));
            }

            Assert.Equal(0m, Figure(await StockAsync(manager, cheese.Id), "productionStorage"));
        }
    }

    /// <summary>
    /// AC-21.3, forced order "send, then cancel": a cancellation arriving while the send holds the write lock waits, then
    /// cancels the parent. The send got <c>200</c>; the complementary order stays Sent (AC-21.4 cancels only unsent ones);
    /// the 1,020 kg stays in production storage, no longer committed (it is free leftover now, AC-21.3).
    /// </summary>
    [Fact]
    public async Task Cancellation_waiting_behind_a_send_leaves_it_sent()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (send, cancel) = await race.RunAsync(
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)),
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Sent", Assert.Single((await ApiJson.ReadAsync(cancel)).GetProperty("complementaryOrders").EnumerateArray())
            .GetProperty("status").GetString());
        var stock = await StockAsync(scene.Manager, scene.Cheese.Id);
        Assert.Equal(1020m, Figure(stock, "productionStorage"));
        Assert.Equal(0m, Figure(stock, "committedToOrder"));
        Assert.Equal(1020m, Figure(stock, "freeLeftover"));
    }

    /// <summary>
    /// AC-21.4, AC-18.3, forced order "cancel, then send": a send arriving while the cancellation holds the write lock
    /// waits, then finds the approved order Cancelled ("Parent order cancelled") and gets <c>409</c> "… is Cancelled. …".
    /// Nothing moves.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_cancellation_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (cancel, send) = await race.RunAsync(
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"),
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "not-approved", $"Complementary order #{number} is Cancelled. {OnlyApproved}");
        Assert.Equal("Parent order cancelled", (await StoredComplementaryOrderAsync(race.App, number)).CancellationNote);
        Assert.Empty(await MovementsOfComplementaryAsync(race.App, number));
        Assert.Equal(100m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse"));
        Assert.Equal(1000m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "productionStorage"));
    }

    /// <summary>
    /// AC-17.3, AC-18.3, forced orders on an Approved complementary order. "Send, then reject": the rejection waits, then
    /// gets <c>409</c> "… is Sent. Only a PendingApproval …". "Reject, then send": the rejection gets <c>409</c> "… is
    /// Approved. …" (an approved request can no longer be rejected), and the send that waited behind it succeeds. Either
    /// way the order is sent exactly once and never rejected.
    /// </summary>
    [Fact]
    public async Task Send_and_reject_of_an_approved_order_never_both_succeed()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var first = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (send, reject) = await race.RunAsync(
            () => SendComplementaryAsync(scene.Warehouse, first, (scene.Cheese.Id, 20m)),
            () => RejectAsync(scene.Manager, first, "No"));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        await ApiAssert.ProblemAsync(reject, HttpStatusCode.Conflict, "not-pending-approval", $"Complementary order #{first} is Sent. {OnlyPending}");

        await using var race2 = new ForcedOrder("ComplementaryOrders");
        var scene2 = await RaceSceneAsync(race2.App, send: true);
        await ReceiveAsync(scene2.Manager, scene2.Cheese.Id, 100m);
        var second = await ApprovedComplementaryAsync(scene2.Production, scene2.Manager, scene2.Number, SpillReason, (scene2.Cheese.Id, 20m));

        var (rejectFirst, sendSecond) = await race2.RunAsync(
            () => RejectAsync(scene2.Manager, second, "No"),
            () => SendComplementaryAsync(scene2.Warehouse, second, (scene2.Cheese.Id, 20m)));

        await ApiAssert.ProblemAsync(rejectFirst, HttpStatusCode.Conflict, "not-pending-approval", $"Complementary order #{second} is Approved. {OnlyPending}");
        Assert.Equal(HttpStatusCode.OK, sendSecond.StatusCode);
        foreach (var (app, number) in new[] { (race.App, first), (race2.App, second) })
        {
            var stored = await StoredComplementaryOrderAsync(app, number);
            Assert.Equal(ComplementaryOrderStatus.Sent, stored.Status);
            Assert.Null(stored.RejectedAt);
            Assert.Single(await MovementsOfComplementaryAsync(app, number));
        }
    }

    /// <summary>
    /// AC-17.2, AC-18.3, forced order "reject, then send" on a PendingApproval request: the rejection succeeds, and the
    /// send that waited behind it gets <c>409</c> "… is Rejected. …". A rejected request never moves material.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_rejection_is_refused()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (reject, send) = await race.RunAsync(
            () => RejectAsync(scene.Manager, number, "No"),
            () => SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "not-approved", $"Complementary order #{number} is Rejected. {OnlyApproved}");
        Assert.Empty(await MovementsOfComplementaryAsync(race.App, number));
    }

    // ---- Data that should never exist -------------------------------------------------------------------------------

    /// <summary>
    /// PRD section 6 row 6: a complementary order tops up a running order. An Approved one whose parent is no longer
    /// InProduction cannot arise through the API (completion and cancellation cancel it in the same transaction), so the
    /// test makes it directly: the parent is marked Completed without the automatic cancellation. The send is refused as a
    /// programming error (<c>500</c> <c>internal-error</c>) and saves nothing: still Approved, no movement, the completed
    /// order's line and the Warehouse unchanged.
    /// </summary>
    [Fact]
    public async Task Send_for_a_parent_that_is_not_in_production_writes_nothing()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var number = await ApprovedComplementaryAsync(scene.Production, scene.Manager, scene.Number, SpillReason, (scene.Cheese.Id, 20m));
        await SetStatusAsync(app, scene.Number, OrderStatus.Completed, scene.ProductionUser.Id);

        var response = await SendComplementaryAsync(scene.Warehouse, number, (scene.Cheese.Id, 20m));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        ApiAssert.Problem(response, await ApiJson.ReadAsync(response), HttpStatusCode.InternalServerError, ApiAssert.NoTitleCheck,
            "internal-error", "An unexpected error occurred.");
        Assert.Equal(ComplementaryOrderStatus.Approved, (await StoredComplementaryOrderAsync(app, number)).Status);
        Assert.Empty(await MovementsOfComplementaryAsync(app, number));
        Assert.Equal(0m, await ComplementarySendsAsync(app, scene.Number, scene.Cheese.Id));
        Assert.Equal(100m, Figure(await StockAsync(scene.Manager, scene.Cheese.Id), "warehouse"));
    }

    /// <summary>
    /// Decision #87, design 5.6 (the follow-up carried into T-018): the ledger refuses a movement whose
    /// <c>ComplementaryOrderNumber</c> is not a complementary order of its <c>ProductionOrderNumber</c>, and one naming a
    /// complementary order that does not exist, as programming errors (<c>ArgumentException</c>), and adds nothing. A
    /// movement can never be corrected, and <c>GET /movements?orderNumber=</c> relies on the pair.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_a_complementary_movement_naming_the_wrong_parent()
    {
        var running = await factory.RunningAsync();
        var (otherOrder, _, _) = await TwoLineOrderAsync(running.Manager, receive: false);
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var movementsBefore = await MovementCountAsync(factory);

        var wrongParent = await Assert.ThrowsAsync<ArgumentException>(() =>
            LedgerAsync(factory, ComplementaryMovement(running, otherOrder, complementary)));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() =>
            LedgerAsync(factory, ComplementaryMovement(running, running.Number, 999999)));

        Assert.Contains($"complementary order #{complementary} belongs to order #{running.Number}, not to order #{otherOrder}", wrongParent.Message);
        Assert.Contains("complementary order #999999 does not exist", unknown.Message);
        Assert.Equal(movementsBefore, await MovementCountAsync(factory));
    }

    // ---- Access (PRD 4.2) -----------------------------------------------------------------------------------------

    /// <summary>AC-01.5: the send without a token is <c>401</c> <c>not-authenticated</c>, and nothing is sent.</summary>
    [Fact]
    public async Task Send_without_a_token_is_401()
    {
        var running = await factory.RunningAsync();
        using var anonymous = factory.CreateClient();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));

        await ApiAssert.NotAuthenticatedAsync(await SendComplementaryAsync(anonymous, complementary, (running.Cheese.Id, 20m)));

        Assert.Equal(ComplementaryOrderStatus.Approved, (await StoredComplementaryOrderAsync(factory, complementary)).Status);
    }

    /// <summary>
    /// AC-18.5, AC-05.1, PRD 4.2 ("Send materials for an order or an approved complementary order": Warehouse or
    /// Manager): a Production-only user gets <c>403</c> "It needs the Warehouse or Manager role." for a real Approved order
    /// with a valid body and for an unknown number (the role is checked first, design 2.2), and nothing is sent. A user
    /// with Production and Warehouse may send (AC-05.2).
    /// </summary>
    [Fact]
    public async Task Production_only_user_cannot_send()
    {
        var running = await factory.RunningAsync();
        var complementary = await ApprovedComplementaryAsync(running.Production, running.Manager, running.Number, SpillReason, (running.Cheese.Id, 20m));

        foreach (var target in new[] { complementary, 999999 })
        {
            await ApiAssert.ProblemAsync(await SendComplementaryAsync(running.Production, target, (running.Cheese.Id, 20m)),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        }

        Assert.Equal(ComplementaryOrderStatus.Approved, (await StoredComplementaryOrderAsync(factory, complementary)).Status);
        Assert.Empty(await MovementsOfComplementaryAsync(factory, complementary));

        using var both = await factory.CreateClientAsAsync(Roles.Production, Roles.Warehouse);
        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(both, complementary, (running.Cheese.Id, 20m))).StatusCode);
    }

    // ---- Fixture and scenes ----------------------------------------------------------------------------------------

    /// <summary>
    /// The class's shared app: a <see cref="TestApiFactory"/> that holds one InProduction order, set up once, the first
    /// time a test asks for it.
    /// </summary>
    /// <remarks>
    /// xUnit creates one instance of a class fixture for all the tests of the class, and runs those tests one after
    /// another, so the lazily created order is shared safely.
    /// </remarks>
    public sealed class RunningOrderApp : TestApiFactory
    {
        private Task<RunningOrder>? _running;

        /// <summary>The running order, set up on the first call.</summary>
        public Task<RunningOrder> RunningAsync() => _running ??= SetUpAsync();

        /// <summary>
        /// A Manager, a Production and a Warehouse user; Cheese, Potato starch and Salt (named exactly that) in kg; an
        /// order of 1,000 bags at 1 kg Cheese, 0.015 kg starch and 0.002 kg Salt per bag, sent in full, so it is
        /// InProduction; and plenty left in the Warehouse for every complementary send the tests make.
        /// </summary>
        private async Task<RunningOrder> SetUpAsync()
        {
            var (manager, managerUser) = await SignedInAsync(this, Roles.Manager);
            var (production, _) = await SignedInAsync(this, Roles.Production);
            var (warehouse, _) = await SignedInAsync(this, Roles.Warehouse);
            var kg = await Seed.CreateUnitAsync(manager, "kg");
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
            var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
            var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Salt");
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m), (salt.Id, 0.002m));
            await ReceiveAsync(manager, cheese.Id, 100_000m);
            await ReceiveAsync(manager, starch.Id, 1_000m);
            await ReceiveAsync(manager, salt.Id, 1_000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK,
                (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m), (salt.Id, 2m))).StatusCode);
            return new RunningOrder(manager, managerUser, production, warehouse, number, cheese, starch, salt);
        }
    }

    /// <summary>What <see cref="RunningOrderApp"/> set up.</summary>
    public sealed record RunningOrder(
        HttpClient Manager, AppUser ManagerUser, HttpClient Production, HttpClient Warehouse, int Number,
        CreatedItem Cheese, CreatedItem Starch, CreatedItem Salt);

    /// <summary>
    /// An InProduction order in its own app: Cheese and Potato starch (named exactly that) in kg, 1 kg and 0.015 kg per
    /// bag, 1,000 bags, sent with 1,000 and 15 kg after the given amounts were received.
    /// </summary>
    private static async Task<TwoMaterialScene> TwoMaterialSceneAsync(
        TestApiFactory app, decimal cheeseInWarehouse, decimal starchInWarehouse)
    {
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, cheeseInWarehouse);
        await ReceiveAsync(manager, starch.Id, starchInWarehouse);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m))).StatusCode);
        return new TwoMaterialScene(manager, warehouse, production, cheese, starch, number);
    }

    /// <summary>What <see cref="TwoMaterialSceneAsync"/> set up.</summary>
    private sealed record TwoMaterialScene(
        HttpClient Manager, HttpClient Warehouse, HttpClient Production, CreatedItem Cheese, CreatedItem Starch, int Number);

    /// <summary>
    /// The figures a refused send must leave alone on the shared app: the parent's Cheese and starch complementary sends
    /// and the Warehouse stock of both.
    /// </summary>
    private sealed record Snapshot(decimal CheeseSends, decimal StarchSends, decimal CheeseWarehouse, decimal StarchWarehouse);

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>The send under test with exactly this JSON body (an empty string sends no body).</summary>
    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{ComplementaryPath}/{number}/send", ApiJson.Content(json), Cancel);

    /// <summary><c>GET /movements?{query}</c> (T-013), with the largest page, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> MovementsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/movements?pageSize=100&{query}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The stored complementary sends (D-3) of the parent's line for one material, read in a new scope.</summary>
    private static async Task<decimal?> ComplementarySendsAsync(TestApiFactory app, int parent, int itemId) =>
        (await StoredOrderAsync(app, parent)).Lines.Single(line => line.RawMaterialId == itemId).ComplementarySends;

    /// <summary>The movements linked to one complementary order, oldest first, read in a new scope.</summary>
    private static async Task<List<Movement>> MovementsOfComplementaryAsync(TestApiFactory app, int complementaryNumber)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ComplementaryOrderNumber == complementaryNumber)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>How many movements exist.</summary>
    private static async Task<int> MovementCountAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.CountAsync(Cancel);
    }

    /// <summary>The shared app's figures a refused send must not change.</summary>
    private async Task<Snapshot> SnapshotAsync(RunningOrder running) => new(
        (await ComplementarySendsAsync(factory, running.Number, running.Cheese.Id))!.Value,
        (await ComplementarySendsAsync(factory, running.Number, running.Starch.Id))!.Value,
        Figure(await StockAsync(running.Manager, running.Cheese.Id), "warehouse"),
        Figure(await StockAsync(running.Manager, running.Starch.Id), "warehouse"));

    /// <summary>PRD 4.1 "rejected": still Approved, no movement for it, and the parent lines and the Warehouse unchanged.</summary>
    private async Task AssertNothingSentAsync(RunningOrder running, int complementary, Snapshot before)
    {
        var stored = await StoredComplementaryOrderAsync(factory, complementary);
        Assert.Equal(ComplementaryOrderStatus.Approved, stored.Status);
        Assert.Null(stored.SentAt);
        Assert.Empty(await MovementsOfComplementaryAsync(factory, complementary));
        Assert.Equal(before, await SnapshotAsync(running));
    }

    /// <summary>
    /// A complementary send of 1 kg of Cheese as the ledger receives it, naming <paramref name="parent"/> and
    /// <paramref name="complementary"/>, for the ledger's own checks.
    /// </summary>
    private static Movement ComplementaryMovement(RunningOrder running, int parent, int complementary) => new()
    {
        Type = MovementType.SentToProduction, ItemId = running.Cheese.Id, WarehouseChange = -1m, ProductionStorageChange = 1m,
        OccurredAt = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc), UserId = running.ManagerUser.Id,
        ProductionOrderNumber = parent, ComplementaryOrderNumber = complementary,
    };

    /// <summary>One number field of a stock row (design 5.5).</summary>
    private static decimal Figure(JsonElement stock, string field) => stock.GetProperty(field).GetDecimal();

    /// <summary>D-3, design 5.5: a raw material's stock row while order <paramref name="orderNumber"/> runs.</summary>
    private static void AssertStock(JsonElement stock, decimal warehouse, decimal production, decimal committed, int orderNumber)
    {
        Assert.Equal(warehouse, Figure(stock, "warehouse"));
        Assert.Equal(production, Figure(stock, "productionStorage"));
        Assert.Equal(committed, Figure(stock, "committedToOrder"));
        Assert.Equal(orderNumber, stock.GetProperty("committedOrderNumber").GetInt32());
        Assert.Equal(0m, Figure(stock, "freeLeftover"));
    }

    /// <summary>Design 5.8: exactly the design's fields, in order, at the top level, on each line and on each history entry.</summary>
    private static void AssertShape(JsonElement body)
    {
        Assert.Equal(
            ["number", "orderNumber", "status", "reason", "requestedAt", "requestedBy", "decisionNote", "cancellationNote", "lines", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.All(body.GetProperty("lines").EnumerateArray(), line => Assert.Equal(
            ["itemId", "itemName", "unit", "requestedAmount", "sendRangeMin", "sendRangeMax", "sentQuantity"],
            line.EnumerateObject().Select(field => field.Name)));
        Assert.All(body.GetProperty("history").EnumerateArray(), entry => Assert.Equal(
            ["status", "at", "by", "note"], entry.EnumerateObject().Select(field => field.Name)));
    }

    /// <summary>
    /// Design 5.8: a Sent line, in kg, with its requested and sent quantities; the send range is <c>null</c>, because a
    /// Sent order can never be sent again.
    /// </summary>
    private static void AssertSentLine(JsonElement line, string itemName, decimal requested, decimal sent)
    {
        Assert.Equal(itemName, line.GetProperty("itemName").GetString());
        Assert.Equal("kg", line.GetProperty("unit").GetString());
        Assert.Equal(requested, line.GetProperty("requestedAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMax").ValueKind);
        Assert.Equal(sent, line.GetProperty("sentQuantity").GetDecimal());
    }

    /// <summary>
    /// AC-18.1, design 5.6: the one movement for <paramref name="itemId"/> in the list: SentToProduction, Warehouse −q and
    /// Production storage +q, linked to the parent order and the complementary order, by the given user at the given time.
    /// </summary>
    private static void AssertComplementaryMovement(
        IEnumerable<JsonElement> movements, int itemId, decimal quantity, int orderNumber, int complementary, string userId, string at)
    {
        var movement = Assert.Single(movements, candidate => candidate.GetProperty("itemId").GetInt32() == itemId);
        Assert.Equal("SentToProduction", movement.GetProperty("type").GetString());
        Assert.Equal(-quantity, movement.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(quantity, movement.GetProperty("productionStorageChange").GetDecimal());
        Assert.Equal(orderNumber, movement.GetProperty("orderNumber").GetInt32());
        Assert.Equal(complementary, movement.GetProperty("complementaryOrderNumber").GetInt32());
        Assert.Equal(userId, movement.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(at, movement.GetProperty("occurredAt").GetString());
    }
}
