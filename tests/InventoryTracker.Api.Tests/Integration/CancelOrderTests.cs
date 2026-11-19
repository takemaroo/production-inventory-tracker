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
/// T-016: cancelling an order (<c>POST /api/v1/production-orders/{number}/cancel</c>; US-21, the AC-15.5 cancel bullet,
/// the OrderCancelledWithMaterial notification of AC-23.2; design 3.8, 5.7, 5.10, 7.2 and 7.3).
/// </summary>
/// <remarks>
/// <para>
/// Orders are created, sent and completed through the API; the cancellation under test always goes through the API too.
/// Free leftover reaches production storage through <see cref="OrderFlow.PutLeftoverAsync"/>. Orders that only need to be
/// in a final state as set-up (Completed, Cancelled) have their status set directly. Complementary orders are inserted
/// through the DbContext, so one test can hold every status, including Sent (T-018); the same check through the T-017
/// endpoints is in <c>ComplementaryOrdersTests</c>.
/// </para>
/// <para>
/// The database allows only one InProduction order (AC-15.10), and the PRD's examples need exact names and order numbers,
/// so every test that sends an order or needs those creates its own factory. Tests on the shared factory only use
/// Created, Completed or Cancelled orders.
/// </para>
/// </remarks>
public class CancelOrderTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The reason of design 5.7's cancel example and design 7.3's notification example.</summary>
    private const string WithdrawnReason = "Customer order withdrawn";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-21.1, AC-21.2: cancelling a Created order ----------------------------------------------------------------

    /// <summary>
    /// AC-21.1, AC-21.2, AC-05.3, design 5.7 and 3.8. A Manager cancels a Created two-line order at 09:30 with the reason
    /// "  Customer order withdrawn  ", while 50 of its first material sit in production storage as free leftover.
    /// <c>200</c> with the detail: Cancelled, <c>cancellationReason</c> trimmed, every line figure still <c>null</c> and
    /// no estimate (design 5.7: estimate only while Created), history Created then Cancelled by the Manager at 09:30 with
    /// the reason as its note. The stored order matches. No movement is written and no stock changes (AC-21.2). No
    /// notification is created: AC-21.3's notification is for a cancelled InProduction order only (design 3.8). The
    /// cancellation is logged, <c>GET</c> returns the same body, and the order can no longer be sent or completed
    /// (AC-15.9, AC-19.5: <c>409</c> <c>order-cancelled</c>).
    /// </summary>
    [Fact]
    public async Task Created_order_is_cancelled_with_who_when_and_why_and_moves_no_stock()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (number, cheese, starch) = await TwoLineOrderAsync(manager);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        var movementsBefore = await MovementCountAsync(app);
        time.SetUtcNow(EightAm.AddMinutes(90));

        var response = await CancelAsync(manager, number, "  Customer order withdrawn  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        Assert.Equal(WithdrawnReason, order.GetProperty("cancellationReason").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("completionReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("producedQuantity").ValueKind);
        Assert.All(order.GetProperty("lines").EnumerateArray(), line =>
        {
            // Design 5.7: nothing was fixed yet, so every send and completion figure stays null, and there is no estimate.
            foreach (var field in new[] { "estimate", "leftoverApplied", "warehouseSend", "complementarySends", "carriedLeftover", "leftoverEntered", "actualUsage" })
            {
                Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
            }
        });

        // AC-05.3, design 5.7 "history": the cancellation is the last entry, with who, when and why.
        AssertHistory(order, "Created", "Cancelled");
        var cancelled = order.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("2026-11-11T09:30:00Z", cancelled.GetProperty("at").GetString());
        Assert.Equal(managerUser.Id, cancelled.GetProperty("by").GetProperty("id").GetString());
        Assert.Equal(managerUser.Email, cancelled.GetProperty("by").GetProperty("email").GetString());
        Assert.Equal(WithdrawnReason, cancelled.GetProperty("note").GetString());

        var stored = await StoredOrderAsync(app, number);
        var cancelTime = EightAm.AddMinutes(90).UtcDateTime;
        Assert.Equal(OrderStatus.Cancelled, stored.Status);
        Assert.Equal(cancelTime, stored.CancelledAt);
        Assert.Equal(managerUser.Id, stored.CancelledById);
        Assert.Equal(WithdrawnReason, stored.CancellationReason);
        Assert.Equal(cancelTime, stored.StatusChangedAt);

        // AC-21.2: no stock moves.
        Assert.Equal(movementsBefore, await MovementCountAsync(app));
        var cheeseStock = await StockAsync(manager, cheese.Id);
        Assert.Equal(2000m, cheeseStock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(50m, cheeseStock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(100m, (await StockAsync(manager, starch.Id)).GetProperty("warehouse").GetDecimal());

        // AC-21.3 is about a cancelled InProduction order; a Created order left nothing of its own behind.
        Assert.Empty(await NotificationsOfOrderAsync(app, number));

        // Decision #92 and architecture 6: the response is what every reader gets, and the state change is logged.
        Assert.Equal(order.GetRawText(), (await ReadOrderAsync(manager, number)).GetRawText());
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Order {number} cancelled by user {managerUser.Id}.");

        // AC-15.9, AC-19.5: a cancelled order can be neither sent nor completed.
        using var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        await ApiAssert.ProblemAsync(await SendAsync(warehouse, number, (cheese.Id, 950m), (starch.Id, 15m)),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
        await ApiAssert.ProblemAsync(await CompleteAsync(manager, number, 1000m, null, (cheese.Id, 0m), (starch.Id, 0m)),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
    }

    /// <summary>
    /// AC-15.5 (cancel bullet), AC-21.2, AC-13.2, through the API. 50 kg of free Cheese; Created orders #7 (08:00, 30 kg),
    /// #8 (09:00, 1,000 kg) and #9 (10:00, 500 kg). Before: #8's estimate is leftover 20, nominal 980, range 960 to 1,000.
    /// After cancelling #7 through the endpoint, its allocation is released: #8 shows leftover 50, nominal 950, range 930
    /// to 970, and #9 still gets nothing (0, 500, 490 to 510). Production storage still holds 50 kg and no movement was
    /// written. A send of #8 then applies exactly the 50 kg its estimate showed (D-6, decision #97).
    /// </summary>
    [Fact]
    public async Task Cancelling_order_7_releases_its_leftover_to_order_8()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);
        var cheese = scene.Cheese.Id;
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 8), cheese), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);
        var movementsBefore = await MovementCountAsync(app);

        var response = await CancelAsync(scene.Manager, 7, "Customer order withdrawn");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, Line(await ApiJson.ReadAsync(response), cheese).GetProperty("estimate").ValueKind);
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 8), cheese), leftover: 50m, nominal: 950m, min: 930m, max: 970m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 9), cheese), leftover: 0m, nominal: 500m, min: 490m, max: 510m);
        Assert.Equal(50m, (await StockAsync(scene.Manager, cheese)).GetProperty("productionStorage").GetDecimal());
        Assert.Equal(movementsBefore, await MovementCountAsync(app));
        Assert.Empty(await NotificationsOfOrderAsync(app, 7));

        var sent = await SendAsync(scene.Warehouse, 8, (cheese, 950m));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(50m, Line(await ApiJson.ReadAsync(sent), cheese).GetProperty("leftoverApplied").GetDecimal());
    }

    // ---- AC-21.3: cancelling an InProduction order --------------------------------------------------------------------

    /// <summary>
    /// AC-21.3, AC-21.1, AC-23.2, design 3.8, 5.7, 5.10 and 7.3 (the OrderCancelledWithMaterial example, word for word).
    /// Order #9 for 1 bag uses, in recipe line order, Cheese 50 kg, Potato starch 0.75 kg and Salt 0.0004 kg per bag
    /// (planned 0 kg). It is sent at 10:02 (50, 0.75 and 0 kg) and cancelled at 15:55, inside the 8-hour life of the
    /// Manager's 08:00 token, with "Customer order withdrawn". <c>200</c>: Cancelled, with the send figures still on the lines and the completion figures null (design 5.7);
    /// history Created, InProduction, Cancelled. No movement besides the two sends. The material is no longer committed
    /// to the order: Cheese and starch are free leftover (D-4). Exactly one notification, read through
    /// <c>GET /notifications</c>: "Order #9 was cancelled with material still in production storage: Cheese 50 kg, Potato
    /// starch 0.75 kg. Reason: "Customer order withdrawn"", at the cancellation time, with the design 5.10 data. Salt (0 in
    /// production storage) is left out, and the list follows recipe line order, not item ids or the send body.
    /// </summary>
    [Fact]
    public async Task In_production_order_is_cancelled_and_managers_are_told_what_material_is_left()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");

        // Potato starch is created first, so item ids do not follow recipe line order.
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Salt");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 50m), (starch.Id, 0.75m), (salt.Id, 0.0004m));
        for (var i = 0; i < 8; i++)
        {
            await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 1m), OrderStatus.Cancelled, managerUser.Id);
        }

        await ReceiveAsync(manager, cheese.Id, 50m);
        await ReceiveAsync(manager, starch.Id, 0.75m);
        Assert.Equal(9, await CreateOrderAsync(manager, product.Id, 1m));
        time.SetUtcNow(EightAm.AddHours(2).AddMinutes(2));
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(warehouse, 9, (salt.Id, 0m), (starch.Id, 0.75m), (cheese.Id, 50m))).StatusCode);
        Assert.Equal(9, (await StockAsync(manager, cheese.Id)).GetProperty("committedOrderNumber").GetInt32());
        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(55));

        var response = await CancelAsync(manager, 9, WithdrawnReason);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        Assert.Equal(WithdrawnReason, order.GetProperty("cancellationReason").GetString());
        AssertHistory(order, "Created", "InProduction", "Cancelled");
        Assert.Equal("2026-11-11T15:55:00Z", order.GetProperty("history").EnumerateArray().Last().GetProperty("at").GetString());

        // Design 5.7: "after cancellation, values fixed so far stay; the rest stay null".
        var cheeseLine = Line(order, cheese.Id);
        Assert.Equal(JsonValueKind.Null, cheeseLine.GetProperty("estimate").ValueKind);
        Assert.Equal(0m, cheeseLine.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(50m, cheeseLine.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0m, cheeseLine.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(0m, cheeseLine.GetProperty("carriedLeftover").GetDecimal());
        Assert.Equal(JsonValueKind.Null, cheeseLine.GetProperty("leftoverEntered").ValueKind);
        Assert.Equal(JsonValueKind.Null, cheeseLine.GetProperty("actualUsage").ValueKind);
        Assert.Equal(JsonValueKind.Null, cheeseLine.GetProperty("usageVariancePercent").ValueKind);

        // AC-21.3: no stock moves; everything sent stays in production storage, now free leftover (D-4).
        Assert.Equal([MovementType.SentToProduction, MovementType.SentToProduction],
            (await MovementsOfOrderAsync(app, 9)).Select(movement => movement.Type));
        var cheeseStock = await StockAsync(manager, cheese.Id);
        Assert.Equal(50m, cheeseStock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(0m, cheeseStock.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(JsonValueKind.Null, cheeseStock.GetProperty("committedOrderNumber").ValueKind);
        Assert.Equal(50m, cheeseStock.GetProperty("freeLeftover").GetDecimal());
        Assert.Equal(0.75m, (await StockAsync(manager, starch.Id)).GetProperty("freeLeftover").GetDecimal());

        // AC-21.3, AC-23.1, design 5.10 and 7.3: one notification, through the notifications API (T-012).
        var notification = Assert.Single((await NotificationsPageAsync(manager)).GetProperty("items").EnumerateArray());
        Assert.Equal("OrderCancelledWithMaterial", notification.GetProperty("type").GetString());
        Assert.Equal(9, notification.GetProperty("orderNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, notification.GetProperty("complementaryOrderNumber").ValueKind);
        Assert.Equal("2026-11-11T15:55:00Z", notification.GetProperty("createdAt").GetString());
        Assert.Equal(
            "Order #9 was cancelled with material still in production storage: Cheese 50 kg, Potato starch 0.75 kg. Reason: \"Customer order withdrawn\"",
            notification.GetProperty("message").GetString());
        var data = notification.GetProperty("data");
        Assert.Equal(["materials", "reason"], data.EnumerateObject().Select(field => field.Name));
        var materials = data.GetProperty("materials").EnumerateArray().ToList();
        Assert.Equal(2, materials.Count);
        AssertStorageMaterial(materials[0], cheese.Id, "Cheese", "kg", 50m);
        AssertStorageMaterial(materials[1], starch.Id, "Potato starch", "kg", 0.75m);
        Assert.Equal(WithdrawnReason, data.GetProperty("reason").GetString());
    }

    /// <summary>
    /// AC-21.3, AC-15.10, decision #6, D-4, D-5, design 3.8, continuing AC-15.5 into worked example D's start. #8 was sent
    /// first (leftover applied 20, send 980, carried leftover 30), so production storage holds 1,030 kg and #9 cannot be
    /// sent ("Order #8 is in production..."). #8 is cancelled. The notification names all 1,030 kg in production storage,
    /// carried leftover included (design 3.8: the material's production storage stock). The "one order in production"
    /// place is free and the material counts as free leftover: oldest first, #7 is owed 30 kg and #9 the next 500, so #9's
    /// estimate is leftover 500, nominal 0, range 0 to 10, and #9 can now be sent with 0 kg (leftover applied 500, carried
    /// leftover 530).
    /// </summary>
    [Fact]
    public async Task After_cancelling_the_order_in_production_another_order_can_be_sent_with_its_material()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await OldestFirstSceneAsync(app, time);
        var cheese = scene.Cheese.Id;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(scene.Warehouse, 8, (cheese, 980m))).StatusCode);
        await ApiAssert.ProblemAsync(await SendAsync(scene.Warehouse, 9, (cheese, 500m)), HttpStatusCode.Conflict,
            "another-order-in-production", "Order #8 is in production. Only one order can be in production at a time.");

        var response = await CancelAsync(scene.Manager, 8, "Line breakdown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1030m, (await StockAsync(scene.Manager, cheese)).GetProperty("freeLeftover").GetDecimal());
        var notification = Assert.Single(await NotificationsOfOrderAsync(app, 8));
        Assert.Equal(NotificationType.OrderCancelledWithMaterial, notification.Type);
        Assert.Equal(
            "Order #8 was cancelled with material still in production storage: Cheese 1,030 kg. Reason: \"Line breakdown\"",
            notification.Message);

        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 7), cheese), leftover: 30m, nominal: 0m, min: 0m, max: 0.6m);
        AssertEstimate(Line(await ReadOrderAsync(scene.Warehouse, 9), cheese), leftover: 500m, nominal: 0m, min: 0m, max: 10m);
        var sent = await SendAsync(scene.Warehouse, 9, (cheese, 0m));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var line = Line(await ApiJson.ReadAsync(sent), cheese);
        Assert.Equal(500m, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(530m, line.GetProperty("carriedLeftover").GetDecimal());
    }

    /// <summary>
    /// AC-21.3 ("if at least one material ... has stock above zero"), decision #59: an InProduction order whose only
    /// material has nothing in production storage (0.0004 kg per bag for 1 bag: planned 0, sent 0) is cancelled with
    /// <c>200</c> and no notification.
    /// </summary>
    [Fact]
    public async Task In_production_order_with_nothing_in_production_storage_raises_no_notification()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (starch.Id, 0.0004m));
        var number = await CreateOrderAsync(manager, product.Id, 1m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (starch.Id, 0m))).StatusCode);

        var response = await CancelAsync(manager, number, "Not needed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelled", (await ApiJson.ReadAsync(response)).GetProperty("status").GetString());
        Assert.Empty(await NotificationsOfOrderAsync(app, number));
    }

    // ---- AC-21.4: unsent complementary orders are cancelled ---------------------------------------------------------

    /// <summary>
    /// AC-21.4, PRD assumption 6, AC-05.3, architecture 3.2: on cancelling an InProduction order, its PendingApproval and
    /// Approved complementary orders (inserted directly, to include a Sent one) become Cancelled with the note "Parent order
    /// cancelled", recording the cancelling Manager and the cancellation time. Rejected and Sent ones are unchanged, and a
    /// complementary order of another order is not touched. The response lists the new statuses.
    /// </summary>
    [Fact]
    public async Task Unsent_complementary_orders_are_cancelled_with_the_order()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 100m);
        var number = await CreateOrderAsync(manager, product.Id, 100m);
        var other = await CreateOrderAsync(manager, product.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 100m))).StatusCode);
        var userId = managerUser.Id;
        var pending = await InsertComplementaryOrderAsync(app, number, ComplementaryOrderStatus.PendingApproval, cheese.Id, userId);
        var approved = await InsertComplementaryOrderAsync(app, number, ComplementaryOrderStatus.Approved, cheese.Id, userId);
        var rejected = await InsertComplementaryOrderAsync(app, number, ComplementaryOrderStatus.Rejected, cheese.Id, userId);
        var sent = await InsertComplementaryOrderAsync(app, number, ComplementaryOrderStatus.Sent, cheese.Id, userId);
        var otherPending = await InsertComplementaryOrderAsync(app, other, ComplementaryOrderStatus.PendingApproval, cheese.Id, userId);
        using var cancellingManager = await app.CreateClientAsAsync(Roles.Manager);
        time.SetUtcNow(EightAm.AddHours(5));

        var response = await CancelAsync(cancellingManager, number, "Line breakdown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal(["Cancelled", "Cancelled", "Rejected", "Sent"],
            order.GetProperty("complementaryOrders").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()));

        var cancellerId = order.GetProperty("history").EnumerateArray().Last().GetProperty("by").GetProperty("id").GetString();
        Assert.NotEqual(userId, cancellerId);
        foreach (var complementaryNumber in new[] { pending, approved })
        {
            var cancelled = await StoredComplementaryOrderAsync(app, complementaryNumber);
            Assert.Equal(ComplementaryOrderStatus.Cancelled, cancelled.Status);
            Assert.Equal("Parent order cancelled", cancelled.CancellationNote);
            Assert.Equal(EightAm.AddHours(5).UtcDateTime, cancelled.CancelledAt);
            Assert.Equal(cancellerId, cancelled.CancelledById);
        }

        foreach (var (complementaryNumber, status) in new[]
        {
            (rejected, ComplementaryOrderStatus.Rejected), (sent, ComplementaryOrderStatus.Sent),
            (otherPending, ComplementaryOrderStatus.PendingApproval),
        })
        {
            var unchanged = await StoredComplementaryOrderAsync(app, complementaryNumber);
            Assert.Equal(status, unchanged.Status);
            Assert.Null(unchanged.CancellationNote);
            Assert.Null(unchanged.CancelledAt);
            Assert.Null(unchanged.CancelledById);
        }
    }

    // ---- AC-21.5: rejected cancellations --------------------------------------------------------------------------

    /// <summary>
    /// AC-21.5, design 7.2: a Completed order is <c>409</c> <c>order-completed</c> "Order #N is completed and cannot be
    /// cancelled."; an already Cancelled order is <c>409</c> <c>order-cancelled</c> "This order is cancelled.". Neither
    /// changes: the Completed one has no cancellation fields, the Cancelled one keeps its first reason, user and time.
    /// </summary>
    [Fact]
    public async Task Completed_and_cancelled_orders_cannot_be_cancelled()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (completed, _, _) = await TwoLineOrderAsync(manager, receive: false);
        var (cancelled, _, _) = await TwoLineOrderAsync(manager, receive: false);
        await SetStatusAsync(factory, completed, OrderStatus.Completed, managerUser.Id);
        await SetStatusAsync(factory, cancelled, OrderStatus.Cancelled, managerUser.Id);
        var cancelledBefore = await StoredOrderAsync(factory, cancelled);
        using var otherManager = await factory.CreateClientAsAsync(Roles.Manager);

        await ApiAssert.ProblemAsync(await CancelAsync(otherManager, completed, "Too late"),
            HttpStatusCode.Conflict, "order-completed", $"Order #{completed} is completed and cannot be cancelled.");
        await ApiAssert.ProblemAsync(await CancelAsync(otherManager, cancelled, "Twice"),
            HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");

        var completedAfter = await StoredOrderAsync(factory, completed);
        Assert.Equal(OrderStatus.Completed, completedAfter.Status);
        Assert.Null(completedAfter.CancelledAt);
        Assert.Null(completedAfter.CancelledById);
        Assert.Null(completedAfter.CancellationReason);
        var cancelledAfter = await StoredOrderAsync(factory, cancelled);
        Assert.Equal(cancelledBefore.CancellationReason, cancelledAfter.CancellationReason);
        Assert.Equal(cancelledBefore.CancelledById, cancelledAfter.CancelledById);
        Assert.Equal(cancelledBefore.CancelledAt, cancelledAfter.CancelledAt);
    }

    /// <summary>
    /// AC-21.5 ("a missing or blank reason is rejected with 400"), AC-21.1 (1 to 500 characters), design 2.6 and 7.2: a
    /// body with no reason, a <c>null</c>, empty or all-spaces reason gives <c>400</c> <c>validation</c> "Reason is
    /// required." under <c>reason</c>; 501 characters gives "Reason must be 500 characters or fewer.". The order stays
    /// Created with no cancellation recorded.
    /// </summary>
    [Theory]
    [InlineData("""{ }""", "Reason is required.")]
    [InlineData("""{ "reason": null }""", "Reason is required.")]
    [InlineData("""{ "reason": "" }""", "Reason is required.")]
    [InlineData("""{ "reason": "   " }""", "Reason is required.")]
    [InlineData("too-long", "Reason must be 500 characters or fewer.")]
    public async Task Missing_blank_or_too_long_reason_is_rejected(string json, string message)
    {
        var (manager, _) = await SignedInAsync(factory, Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager, receive: false);
        var body = json == "too-long" ? $$"""{ "reason": "{{new string('r', 501)}}" }""" : json;

        await ApiAssert.ValidationAsync(await CancelRawAsync(manager, number, body), "reason", message);

        await AssertNotCancelledAsync(factory, number, OrderStatus.Created);
    }

    /// <summary>
    /// AC-21.1, design 2.6: the 500-character limit applies after trimming. Exactly 500 characters with spaces around them
    /// is accepted and stored as the 500 characters.
    /// </summary>
    [Fact]
    public async Task Reason_of_exactly_500_characters_is_accepted_after_trimming()
    {
        var (manager, _) = await SignedInAsync(factory, Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager, receive: false);
        var reason = new string('r', 500);

        var response = await CancelAsync(manager, number, $"  {reason}  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(reason, (await ApiJson.ReadAsync(response)).GetProperty("cancellationReason").GetString());
        Assert.Equal(reason, (await StoredOrderAsync(factory, number)).CancellationReason);
    }

    /// <summary>
    /// Architecture 4.1 item 2: no body at all is <c>400</c> "A request body is required."; a reason sent as a number is
    /// <c>400</c> "reason has the wrong type." under <c>reason</c>. The order is not cancelled.
    /// </summary>
    [Fact]
    public async Task Unreadable_bodies_are_rejected()
    {
        var (manager, _) = await SignedInAsync(factory, Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager, receive: false);

        await ApiAssert.ProblemAsync(await CancelRawAsync(manager, number, ""),
            HttpStatusCode.BadRequest, "validation", "A request body is required.");
        await ApiAssert.ValidationAsync(await CancelRawAsync(manager, number, """{ "reason": 5 }"""),
            "reason", "reason has the wrong type.");

        await AssertNotCancelledAsync(factory, number, OrderStatus.Created);
    }

    // ---- Design 2.2: the order of checks ------------------------------------------------------------------------

    /// <summary>
    /// Design 2.2: the record in the URL (step 3) comes before the body (step 4), and the body before the state (step 5).
    /// An unknown order with a blank reason is <c>404</c> "Order #999999 not found."; a Completed order with a blank
    /// reason is <c>400</c> "Reason is required.", not <c>409</c>; a Cancelled order with a 501-character reason is
    /// <c>400</c> too.
    /// </summary>
    [Fact]
    public async Task Unknown_order_comes_before_the_reason_and_the_reason_before_the_state()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var (completed, _, _) = await TwoLineOrderAsync(manager, receive: false);
        var (cancelled, _, _) = await TwoLineOrderAsync(manager, receive: false);
        await SetStatusAsync(factory, completed, OrderStatus.Completed, managerUser.Id);
        await SetStatusAsync(factory, cancelled, OrderStatus.Cancelled, managerUser.Id);

        await ApiAssert.ProblemAsync(await CancelAsync(manager, 999999, " "),
            HttpStatusCode.NotFound, "not-found", "Order #999999 not found.");
        await ApiAssert.ValidationAsync(await CancelAsync(manager, completed, " "), "reason", "Reason is required.");
        await ApiAssert.ValidationAsync(await CancelAsync(manager, cancelled, new string('r', 501)),
            "reason", "Reason must be 500 characters or fewer.");
    }

    // ---- Concurrency (PRD section 5, architecture 5.2) -------------------------------------------------------------

    /// <summary>
    /// Architecture 5.2, AC-21.5: two Managers cancel the same Created order at the same moment (separate clients, different
    /// reasons). Exactly one gets <c>200</c>; the other gets <c>409</c> <c>order-cancelled</c> "This order is cancelled.".
    /// The stored reason and user are the winner's. Repeated over three rounds.
    /// </summary>
    [Fact]
    public async Task Simultaneous_cancellations_succeed_once()
    {
        var (first, firstUser) = await SignedInAsync(factory, Roles.Manager);
        var (second, secondUser) = await SignedInAsync(factory, Roles.Manager);

        for (var round = 0; round < 3; round++)
        {
            var (number, _, _) = await TwoLineOrderAsync(first, receive: false);

            var responses = await Task.WhenAll(
                CancelAsync(first, number, "First reason"), CancelAsync(second, number, "Second reason"));

            var winnerIndex = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(responses[1 - winnerIndex], HttpStatusCode.Conflict, "order-cancelled",
                "This order is cancelled.");
            var stored = await StoredOrderAsync(factory, number);
            Assert.Equal(winnerIndex == 0 ? "First reason" : "Second reason", stored.CancellationReason);
            Assert.Equal(winnerIndex == 0 ? firstUser.Id : secondUser.Id, stored.CancelledById);
        }
    }

    /// <summary>
    /// PRD section 5, architecture 5.2: a cancellation and a send of the same Created order at the same moment end in one
    /// consistent outcome, whichever takes the write lock first. The cancellation always succeeds and the order ends
    /// Cancelled. If the send ran first (<c>200</c>), the cancellation found the order InProduction: one SentToProduction
    /// of 1,000 kg, the Warehouse at 0, production storage 1,000 kg of free leftover, the history passes through
    /// InProduction, and one OrderCancelledWithMaterial notification names the 1,000 kg. Otherwise the send got
    /// <c>409</c> <c>order-cancelled</c>: no movement, nothing fixed on the line, the Warehouse still holds 1,000 kg, no
    /// notification. Either way no order is left InProduction. Repeated over six rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Cancellation_racing_a_send_ends_in_one_consistent_outcome()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);

        for (var round = 0; round < 6; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);

            var responses = await Task.WhenAll(
                CancelAsync(manager, number, WithdrawnReason), SendAsync(warehouse, number, (cheese.Id, 1000m)));

            var (cancel, send) = (responses[0], responses[1]);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var order = await ApiJson.ReadAsync(cancel);
            Assert.Equal(OrderStatus.Cancelled, (await StoredOrderAsync(app, number)).Status);
            var stock = await StockAsync(manager, cheese.Id);
            var movements = await MovementsOfOrderAsync(app, number);
            var notifications = await NotificationsOfOrderAsync(app, number);
            if (send.StatusCode == HttpStatusCode.OK)
            {
                AssertHistory(order, "Created", "InProduction", "Cancelled");
                var movement = Assert.Single(movements);
                Assert.Equal(MovementType.SentToProduction, movement.Type);
                Assert.Equal(1000m, movement.ProductionStorageChange);
                Assert.Equal(0m, stock.GetProperty("warehouse").GetDecimal());
                Assert.Equal(1000m, stock.GetProperty("productionStorage").GetDecimal());
                Assert.Equal(1000m, stock.GetProperty("freeLeftover").GetDecimal());
                var notification = Assert.Single(notifications);
                Assert.Equal(NotificationType.OrderCancelledWithMaterial, notification.Type);
                Assert.Contains(" 1,000 ", notification.Message, StringComparison.Ordinal);
            }
            else
            {
                await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
                AssertHistory(order, "Created", "Cancelled");
                Assert.Empty(movements);
                Assert.Null((await StoredOrderAsync(app, number)).Lines.Single().WarehouseSend);
                Assert.Equal(1000m, stock.GetProperty("warehouse").GetDecimal());
                Assert.Equal(0m, stock.GetProperty("productionStorage").GetDecimal());
                Assert.Empty(notifications);
            }

            Assert.Equal(0, await InProductionCountAsync(app));
        }
    }

    /// <summary>
    /// PRD section 5, architecture 5.2, AC-19.5, AC-21.5: a cancellation and a completion of the same InProduction order at
    /// the same moment give exactly one <c>200</c>, and the order ends in the matching state. If the completion won, the
    /// cancellation gets <c>409</c> <c>order-completed</c> "Order #N is completed and cannot be cancelled.": the order is
    /// Completed with no cancellation fields, its ConsumedInProduction and ProducedOutput movements exist, production
    /// storage is 0 and there is no notification (output and usage exactly as planned, no leftover). If the cancellation
    /// won, the completion gets <c>409</c> <c>order-cancelled</c> "This order is cancelled.": the order is Cancelled with no
    /// produced quantity, there is no completion movement, the 1,000 kg is still in production storage, and one
    /// OrderCancelledWithMaterial notification exists. Repeated over six rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Cancellation_racing_a_completion_ends_in_one_consistent_outcome()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);

        for (var round = 0; round < 6; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m))).StatusCode);

            var responses = await Task.WhenAll(
                CancelAsync(manager, number, WithdrawnReason),
                CompleteAsync(production, number, 1000m, null, (cheese.Id, 0m)));

            var (cancel, complete) = (responses[0], responses[1]);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var stored = await StoredOrderAsync(app, number);
            var movementTypes = (await MovementsOfOrderAsync(app, number)).Select(movement => movement.Type).ToList();
            var notifications = await NotificationsOfOrderAsync(app, number);
            var storage = (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal();
            if (complete.StatusCode == HttpStatusCode.OK)
            {
                await ApiAssert.ProblemAsync(cancel, HttpStatusCode.Conflict, "order-completed",
                    $"Order #{number} is completed and cannot be cancelled.");
                Assert.Equal(OrderStatus.Completed, stored.Status);
                Assert.Null(stored.CancelledAt);
                Assert.Null(stored.CancellationReason);
                Assert.Equal(
                    [MovementType.SentToProduction, MovementType.ConsumedInProduction, MovementType.ProducedOutput],
                    movementTypes);
                Assert.Equal(0m, storage);
                Assert.Empty(notifications);
            }
            else
            {
                await ApiAssert.ProblemAsync(complete, HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
                Assert.Equal(OrderStatus.Cancelled, stored.Status);
                Assert.Null(stored.ProducedQuantity);
                Assert.Null(stored.CompletedAt);
                Assert.Equal([MovementType.SentToProduction], movementTypes);
                Assert.Equal(1000m, storage);
                Assert.Equal(NotificationType.OrderCancelledWithMaterial, Assert.Single(notifications).Type);
            }

            Assert.Equal(0, await InProductionCountAsync(app));
        }
    }

    // ---- Concurrency in a forced order (architecture 5.2) ----------------------------------------------------------
    //
    // The two race tests above fire both requests at once, so either may win and a run cannot show which branches it
    // took. The four tests below force each ordering with ForcedOrder: the second request starts while the first holds
    // the write lock, is shown to be waiting, and then runs on what the first committed.

    /// <summary>
    /// Architecture 5.2, AC-15.9, AC-21.2, forced order "cancel, then send": a send of the same Created order arriving
    /// while the cancellation holds the write lock waits, then gets <c>409</c> <c>order-cancelled</c> "This order is
    /// cancelled.". The order is Cancelled with nothing sent: no movement, no send figure, no sender, no notification, the
    /// Warehouse still holds 1,000 kg, and no order is InProduction.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_cancellation_is_refused_and_moves_nothing()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: false);

        var (cancel, send) = await race.RunAsync(
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"),
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m)));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
        var stored = await StoredOrderAsync(race.App, scene.Number);
        Assert.Equal(OrderStatus.Cancelled, stored.Status);
        Assert.Null(stored.SentAt);
        Assert.Null(stored.Lines.Single().WarehouseSend);
        Assert.Empty(await MovementsOfOrderAsync(race.App, scene.Number));
        Assert.Empty(await NotificationsOfOrderAsync(race.App, scene.Number));
        Assert.Equal(1000m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("warehouse").GetDecimal());
        Assert.Equal(0, await InProductionCountAsync(race.App));
    }

    /// <summary>
    /// Architecture 5.2, AC-21.3, forced order "send, then cancel": a cancellation arriving while the send holds the write
    /// lock waits, then finds the order InProduction and cancels it. Both get <c>200</c>. The cancellation's response is
    /// Cancelled with the send figures kept (leftover applied 0, warehouse send 1,000, complementary sends 0, carried
    /// leftover 0; design 5.7) and history Created, InProduction, Cancelled. The one SentToProduction movement stays, the
    /// 1,000 kg is free leftover, and exactly one OrderCancelledWithMaterial notification names the 1,000 kg sent.
    /// </summary>
    [Fact]
    public async Task Cancellation_waiting_behind_a_send_cancels_the_order_in_production()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: false);

        var (send, cancel) = await race.RunAsync(
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m)),
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("InProduction", (await ApiJson.ReadAsync(send)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var order = await ApiJson.ReadAsync(cancel);
        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        AssertHistory(order, "Created", "InProduction", "Cancelled");
        var line = Line(order, scene.Cheese.Id);
        Assert.Equal(0m, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(1000m, line.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0m, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(0m, line.GetProperty("carriedLeftover").GetDecimal());

        Assert.Equal([MovementType.SentToProduction],
            (await MovementsOfOrderAsync(race.App, scene.Number)).Select(movement => movement.Type));
        Assert.Equal(1000m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("freeLeftover").GetDecimal());
        var notification = Assert.Single(await NotificationsOfOrderAsync(race.App, scene.Number));
        Assert.Equal(NotificationType.OrderCancelledWithMaterial, notification.Type);
        Assert.Equal(
            $"Order #{scene.Number} was cancelled with material still in production storage: {scene.Cheese.Name} 1,000 {scene.Cheese.UnitName}. Reason: \"Line breakdown\"",
            notification.Message);
        Assert.Equal(0, await InProductionCountAsync(race.App));
    }

    /// <summary>
    /// Architecture 5.2, AC-19.5, forced order "cancel, then complete": a completion of the same InProduction order
    /// arriving while the cancellation holds the write lock waits, then gets <c>409</c> <c>order-cancelled</c> "This order
    /// is cancelled.". The order is Cancelled with no completion recorded: no ConsumedInProduction or ProducedOutput
    /// movement, the 1,000 kg still in production storage, and the cancellation's one OrderCancelledWithMaterial
    /// notification.
    /// </summary>
    [Fact]
    public async Task Completion_waiting_behind_a_cancellation_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (cancel, complete) = await race.RunAsync(
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"),
            () => CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await ApiAssert.ProblemAsync(complete, HttpStatusCode.Conflict, "order-cancelled", "This order is cancelled.");
        var stored = await StoredOrderAsync(race.App, scene.Number);
        Assert.Equal(OrderStatus.Cancelled, stored.Status);
        Assert.Null(stored.CompletedAt);
        Assert.Null(stored.ProducedQuantity);
        Assert.Equal([MovementType.SentToProduction],
            (await MovementsOfOrderAsync(race.App, scene.Number)).Select(movement => movement.Type));
        Assert.Equal(1000m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.Equal(NotificationType.OrderCancelledWithMaterial,
            Assert.Single(await NotificationsOfOrderAsync(race.App, scene.Number)).Type);
        Assert.Equal(0, await InProductionCountAsync(race.App));
    }

    /// <summary>
    /// Architecture 5.2, AC-21.5, forced order "complete, then cancel": a cancellation arriving while the completion holds
    /// the write lock waits, then gets <c>409</c> <c>order-completed</c> "Order #N is completed and cannot be cancelled.".
    /// The order is Completed with no cancellation fields, its completion movements exist, production storage is 0, and
    /// there is no notification at all (output and usage exactly as planned, no leftover, and no cancellation).
    /// </summary>
    [Fact]
    public async Task Cancellation_waiting_behind_a_completion_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (complete, cancel) = await race.RunAsync(
            () => CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)),
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"));

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await ApiAssert.ProblemAsync(cancel, HttpStatusCode.Conflict, "order-completed",
            $"Order #{scene.Number} is completed and cannot be cancelled.");
        var stored = await StoredOrderAsync(race.App, scene.Number);
        Assert.Equal(OrderStatus.Completed, stored.Status);
        Assert.Null(stored.CancelledAt);
        Assert.Null(stored.CancelledById);
        Assert.Null(stored.CancellationReason);
        Assert.Equal(
            [MovementType.SentToProduction, MovementType.ConsumedInProduction, MovementType.ProducedOutput],
            (await MovementsOfOrderAsync(race.App, scene.Number)).Select(movement => movement.Type));
        Assert.Equal(0m, (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.Empty(await NotificationsOfOrderAsync(race.App, scene.Number));
    }

    // ---- Access (PRD 4.2) -----------------------------------------------------------------------------------------

    /// <summary>AC-01.5: cancelling without a token is <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Cancel_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await CancelAsync(anonymous, 1, WithdrawnReason));
    }

    /// <summary>
    /// AC-21.6, AC-05.1, PRD 4.2 ("Create or cancel an order": Manager only): a Warehouse-only user, a Production-only
    /// user and a user with both of those roles each get <c>403</c> "It needs the Manager role.", for a real Created order
    /// with a valid reason and for an unknown number (the role is checked before the record, design 2.2). The order is
    /// not cancelled.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Warehouse, Roles.Production)]
    public async Task Users_without_the_manager_role_cannot_cancel(params string[] roles)
    {
        var (manager, _) = await SignedInAsync(factory, Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager, receive: false);
        using var client = await factory.CreateClientAsAsync(roles);

        await ApiAssert.ProblemAsync(await CancelAsync(client, number, WithdrawnReason),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        await ApiAssert.ProblemAsync(await CancelAsync(client, 999999, WithdrawnReason),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);

        await AssertNotCancelledAsync(factory, number, OrderStatus.Created);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------
    //
    // The forced-order tests set up with OrderFlow.RaceSceneAsync (shared with T-017, decision #101).

    /// <summary>The cancellation under test with exactly this JSON body (an empty string sends no body).</summary>
    private static Task<HttpResponseMessage> CancelRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{OrdersPath}/{number}/cancel", ApiJson.Content(json), Cancel);

    /// <summary>The first page of <c>GET /api/v1/notifications</c> (T-012) for the calling Manager, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> NotificationsPageAsync(HttpClient manager)
    {
        var response = await manager.GetAsync("/api/v1/notifications", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>How many movements the database holds in total: a cancellation must not change it (AC-21.2, AC-21.3).</summary>
    private static async Task<int> MovementCountAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.CountAsync(Cancel);
    }

    /// <summary>How many orders are InProduction (AC-15.10: never more than one).</summary>
    private static async Task<int> InProductionCountAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductionOrders
            .CountAsync(order => order.Status == OrderStatus.InProduction, Cancel);
    }

    /// <summary>
    /// PRD 4.1 "rejected": the order still has <paramref name="status"/> and no cancellation is recorded on it.
    /// </summary>
    private static async Task AssertNotCancelledAsync(TestApiFactory app, int number, OrderStatus status)
    {
        var order = await StoredOrderAsync(app, number);
        Assert.Equal(status, order.Status);
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancelledById);
        Assert.Null(order.CancellationReason);
    }

    /// <summary>AC-05.3, design 5.7: the statuses of the order's history, oldest first.</summary>
    private static void AssertHistory(JsonElement order, params string[] statuses) =>
        Assert.Equal(statuses, order.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()));

    /// <summary>
    /// Design 5.10: one entry of an OrderCancelledWithMaterial notification's <c>materials</c>: exactly the four fields,
    /// in order, with these values.
    /// </summary>
    private static void AssertStorageMaterial(JsonElement material, int itemId, string itemName, string unit, decimal quantity)
    {
        Assert.Equal(["itemId", "itemName", "unit", "productionStorageQuantity"], material.EnumerateObject().Select(field => field.Name));
        Assert.Equal(itemId, material.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, material.GetProperty("itemName").GetString());
        Assert.Equal(unit, material.GetProperty("unit").GetString());
        Assert.Equal(quantity, material.GetProperty("productionStorageQuantity").GetDecimal());
    }
}
