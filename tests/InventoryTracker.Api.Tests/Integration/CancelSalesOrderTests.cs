using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using InventoryTracker.Api.Tests.Integration.Scenarios;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-028: cancel a sales order, <c>POST /api/v1/sales-orders/{number}/cancel</c> (US-27 AC-27.1 to AC-27.4;
/// AC-26.10 third bullet, a ship racing a cancel; AC-29.4; AC-05.3; design 3.15, 5.12 and 7.2 "Sales orders";
/// architecture 5.1 check order and 5.2; decisions #142, #146, #163).
/// </summary>
/// <remarks>
/// <para>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>), so each makes its own items and
/// customer and reads only its own sales orders and stock. A test that controls the clock, or races two requests with
/// <see cref="ForcedOrder"/>, creates its own app.
/// </para>
/// <para>
/// Finished products cannot be received (AC-09.3), so tests put them into the Warehouse with a positive Warehouse
/// adjustment, the same movement a stock count would write (US-12).
/// </para>
/// </remarks>
public class CancelSalesOrderTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The design 3.15 example reason.</summary>
    private const string Reason = "Customer changed the order to 400 bags";

    private const string ReasonRequired = "Reason is required.";
    private const string ReasonTooLong = "Reason must be 500 characters or fewer.";

    /// <summary>A character outside the Basic Multilingual Plane: two UTF-16 code units, one code point (decision #116).</summary>
    private const string Emoji = "\U0001F9C0";

    private const int NoSuchRecord = 999_999;

    private static readonly DateTimeOffset NineAm = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-27.1: a cancellation ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-27.1 (PRD example), AC-05.3, design 3.15 and 5.12, decision #146: a sales order a Manager created is cancelled
    /// by a Seller-only user an hour later with the design's reason. <c>200</c> with the Cancelled detail:
    /// <c>cancellationReason</c> is the reason, <c>shipmentNote</c> stays <c>null</c>, and the history is Created (the
    /// Manager) then Cancelled (the Seller, at the cancel time, with the reason as its note). The response equals
    /// <c>GET</c>, the list row shows the Cancelled time as <c>statusChangedAt</c>, and the stored who and when match.
    /// </summary>
    /// <remarks>
    /// The app's clock moves one second forward every time it is read (<c>AutoAdvanceAmount</c>). So
    /// <c>CancelledAt</c> and <c>StatusChangedAt</c> can only be equal if the cancel reads the clock once.
    /// </remarks>
    [Fact]
    public async Task Seller_cancels_a_sales_order_a_Manager_created()
    {
        var time = new FakeTimeProvider(NineAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await OrderFlow.SignedInAsync(app, Roles.Manager);
        var customer = await CreateCustomerAsync(manager);
        var item = await ProductAsync(manager, 600m);
        var number = await CreateSalesOrderAsync(manager, customer.Id, (item.Id, 600m));

        time.SetUtcNow(NineAm.AddHours(1).AddMilliseconds(600));
        time.AutoAdvanceAmount = TimeSpan.FromSeconds(1);
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(app, Roles.Seller);

        var response = await CancelSalesOrderAsync(seller, number, Reason);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["number", "customer", "customerReference", "status", "createdAt", "createdBy", "shipmentNote", "cancellationReason", "lines", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.Equal(number, body.GetProperty("number").GetInt32());
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(Reason, body.GetProperty("cancellationReason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipmentNote").ValueKind);
        Assert.Equal(600m, Assert.Single(body.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());

        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", "Cancelled"], history.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("2026-12-01T09:00:00Z", history[0].GetProperty("at").GetString());
        AssertPerson(history[0].GetProperty("by"), managerUser);
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("note").ValueKind);
        AssertPerson(history[1].GetProperty("by"), sellerUser);
        Assert.Equal(Reason, history[1].GetProperty("note").GetString());

        // Decision #161: the cancel answers with exactly the detail GET shows.
        Assert.Equal(body.GetRawText(), (await ReadSalesOrderAsync(seller, number)).GetRawText());

        // AC-05.3: who and when are stored; one clock read gives CancelledAt and StatusChangedAt the same value, which is
        // the Cancelled entry's time and the list's statusChangedAt (design 5.12).
        var stored = await StoredSalesOrderAsync(app, number);
        Assert.Equal(SalesOrderStatus.Cancelled, stored.Status);
        Assert.Equal(sellerUser.Id, stored.CancelledById);
        Assert.Equal(Reason, stored.CancellationReason);
        Assert.Equal(stored.CancelledAt, stored.StatusChangedAt);
        Assert.Equal(ApiTime(stored.CancelledAt!.Value), history[1].GetProperty("at").GetString());
        Assert.StartsWith("2026-12-01T10:00:", history[1].GetProperty("at").GetString());
        Assert.Null(stored.ShippedAt);
        Assert.Null(stored.ShippedById);

        var row = Assert.Single(await ScenarioApi.AllRowsAsync(seller,
            $"{SalesOrdersPath}?status=Cancelled&customerId={customer.Id}"));
        Assert.Equal(number, row.GetProperty("number").GetInt32());
        Assert.Equal(history[1].GetProperty("at").GetString(), row.GetProperty("statusChangedAt").GetString());
    }

    /// <summary>
    /// AC-27.1, decision #146: both roles that may cancel can cancel a sales order someone else created (here the other
    /// role). The Cancelled history entry names the caller.
    /// </summary>
    [Theory]
    [InlineData(Roles.Manager, Roles.Seller)]
    [InlineData(Roles.Seller, Roles.Manager)]
    public async Task Manager_or_Seller_cancels_whoever_created_it(string creatorRole, string cancellerRole)
    {
        var (creator, _) = await OrderFlow.SignedInAsync(factory, creatorRole);
        var (canceller, cancellerUser) = await OrderFlow.SignedInAsync(factory, cancellerRole);
        var manager = creatorRole == Roles.Manager ? creator : canceller;
        var customer = await CreateCustomerAsync(creator);
        var item = await ProductAsync(manager, 0m);
        var number = await CreateSalesOrderAsync(creator, customer.Id, (item.Id, 10m));

        var response = await CancelSalesOrderAsync(canceller, number, Reason);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        AssertPerson(body.GetProperty("history")[1].GetProperty("by"), cancellerUser);
    }

    /// <summary>
    /// AC-27.1 ("No stock moves"), D-2: cancelling a two-line sales order whose items are both in the Warehouse creates no
    /// movement, neither for the sales order nor for either item, and leaves Warehouse and production storage as they
    /// were.
    /// </summary>
    [Fact]
    public async Task Cancel_moves_no_stock()
    {
        var scene = await SceneAsync();
        var cheese = await RawMaterialAsync(scene.Manager, 100m);
        var bags = await ProductAsync(scene.Manager, 30m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (cheese.Id, 60m), (bags.Id, 30m));
        var cheeseMovements = (await ItemMovementsAsync(scene.Manager, cheese.Id)).Count;
        var bagMovements = (await ItemMovementsAsync(scene.Manager, bags.Id)).Count;

        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(scene.Seller, number)).StatusCode);

        Assert.Empty(await ShipmentMovementsAsync(scene.Manager, number));
        Assert.Equal(cheeseMovements, (await ItemMovementsAsync(scene.Manager, cheese.Id)).Count);
        Assert.Equal(bagMovements, (await ItemMovementsAsync(scene.Manager, bags.Id)).Count);
        await AssertStockAsync(scene.Manager, cheese.Id, warehouse: 100m, productionStorage: 0m);
        await AssertStockAsync(scene.Manager, bags.Id, warehouse: 30m, productionStorage: 0m);
    }

    /// <summary>
    /// AC-27.1, AC-29.4: a Created sales order whose customer was deactivated after it was created can still be cancelled
    /// (<c>200</c>), and the detail shows the customer as inactive.
    /// </summary>
    [Fact]
    public async Task Sales_order_of_a_deactivated_customer_can_be_cancelled()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var deactivate = await scene.Seller.PostAsync($"{CustomersPath}/{scene.Customer.Id}/deactivate", content: null, Cancel);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await CancelSalesOrderAsync(scene.Seller, number);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("customer").GetProperty("isActive").GetBoolean());
    }

    // ---- AC-27.2: status --------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-27.2, decision #142, design 7.2: cancelling a sales order that has shipped gives <c>409</c>
    /// <c>already-shipped</c> "This sales order has been shipped and cannot be cancelled." (the ship's code, the cancel's
    /// sentence). The sales order is exactly as the shipment left it, and its movement stays.
    /// </summary>
    [Fact]
    public async Task Shipped_sales_order_cannot_be_cancelled()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 40m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 40m));
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(scene.Warehouse, number)).StatusCode);
        var shipped = (await ReadSalesOrderAsync(scene.Seller, number)).GetRawText();

        var response = await CancelSalesOrderAsync(scene.Seller, number);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.CancelShipped);
        Assert.Equal(shipped, (await ReadSalesOrderAsync(scene.Seller, number)).GetRawText());
        Assert.Single(await ShipmentMovementsAsync(scene.Manager, number));
        Assert.Equal(0m, await WarehouseAsync(scene.Manager, item.Id));
    }

    /// <summary>
    /// AC-27.2, design 7.2: cancelling a sales order that is already Cancelled gives <c>409</c>
    /// <c>sales-order-cancelled</c> "This sales order is already cancelled.". The first cancellation's reason, user and
    /// time stay.
    /// </summary>
    [Fact]
    public async Task Cancelled_sales_order_cannot_be_cancelled_again()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(scene.Seller, number, "First reason")).StatusCode);
        var cancelled = (await ReadSalesOrderAsync(scene.Seller, number)).GetRawText();

        var response = await CancelSalesOrderAsync(scene.Manager, number, "Second reason");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.AlreadyCancelled);
        Assert.Equal(cancelled, (await ReadSalesOrderAsync(scene.Seller, number)).GetRawText());
        Assert.Equal("First reason", (await StoredSalesOrderAsync(factory, number)).CancellationReason);
    }

    /// <summary>AC-25.3, design 7.2: an unknown number is <c>404</c> "Sales order #999999 not found.", for both roles that may cancel.</summary>
    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Seller)]
    public async Task Unknown_number_is_404(string role)
    {
        var client = await factory.CreateClientAsAsync(role);

        var response = await CancelSalesOrderAsync(client, NoSuchRecord);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"Sales order #{NoSuchRecord} not found.");
    }

    // ---- AC-27.2: the reason ------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-27.2, design 2.6 and 7.2: a reason that is missing, <c>null</c>, empty or only spaces is <c>400</c> "Reason is
    /// required." under <c>reason</c>. The sales order stays Created with no reason stored.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "reason": null }""")]
    [InlineData("""{ "reason": "" }""")]
    [InlineData("""{ "reason": "   " }""")]
    public async Task Missing_or_blank_reason_is_400(string json)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        var response = await CancelRawAsync(scene.Seller, number, json);

        await ApiAssert.ValidationAsync(response, "reason", ReasonRequired);
        await AssertStillCreatedAsync(number);
    }

    /// <summary>
    /// AC-27.2, design 7.2, decision #116: a reason of 501 characters is <c>400</c> "Reason must be 500 characters or
    /// fewer." under <c>reason</c>. Counted in code points after trimming, so 501 emoji are refused too.
    /// </summary>
    [Theory]
    [InlineData("r")]
    [InlineData(Emoji)]
    public async Task Reason_over_500_characters_is_400(string character)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        var response = await CancelSalesOrderAsync(scene.Seller, number, string.Concat(Enumerable.Repeat(character, 501)));

        await ApiAssert.ValidationAsync(response, "reason", ReasonTooLong);
        await AssertStillCreatedAsync(number);
    }

    /// <summary>
    /// AC-27.1 (1 to 500 characters), design 2.6, decision #116: a reason of exactly 500 characters, sent with spaces
    /// around it, is accepted and stored trimmed, as <c>cancellationReason</c> and as the Cancelled entry's note. 500
    /// emoji are 1,000 UTF-16 units but 500 characters, so they are accepted too; so is a one-character reason.
    /// </summary>
    [Theory]
    [InlineData("r", 500)]
    [InlineData(Emoji, 500)]
    [InlineData("r", 1)]
    public async Task Reason_of_1_to_500_characters_is_saved_trimmed(string character, int length)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var reason = string.Concat(Enumerable.Repeat(character, length));

        var response = await CancelSalesOrderAsync(scene.Seller, number, $"  {reason}  ");
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(reason, body.GetProperty("cancellationReason").GetString());
        Assert.Equal(reason, body.GetProperty("history")[1].GetProperty("note").GetString());
        Assert.Equal(reason, (await StoredSalesOrderAsync(factory, number)).CancellationReason);
    }

    /// <summary>
    /// AC-27.2 (reason before status), design 2.2, architecture 5.1: a blank or too long reason on a sales order that is
    /// Shipped or Cancelled is <c>400</c> under <c>reason</c>, not the status <c>409</c>.
    /// </summary>
    [Theory]
    [InlineData(SalesOrderStatus.Shipped, "   ", ReasonRequired)]
    [InlineData(SalesOrderStatus.Shipped, null, ReasonRequired)]
    [InlineData(SalesOrderStatus.Cancelled, "   ", ReasonRequired)]
    [InlineData(SalesOrderStatus.Shipped, "LONG", ReasonTooLong)]
    [InlineData(SalesOrderStatus.Cancelled, "LONG", ReasonTooLong)]
    public async Task Bad_reason_is_400_before_the_status_409(SalesOrderStatus status, string? reason, string message)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await InsertSalesOrderAsync(factory, scene.Customer.Id, item.Id, 5m, status, scene.SellerUser.Id);

        var response = await CancelSalesOrderAsync(scene.Seller, number, reason == "LONG" ? new string('r', 501) : reason);

        await ApiAssert.ValidationAsync(response, "reason", message);
        Assert.Equal(status, (await StoredSalesOrderAsync(factory, number)).Status);
    }

    /// <summary>
    /// Design 2.2 (the record in the URL before the body), architecture 5.1: an unknown number with a blank reason is
    /// <c>404</c>, not <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Unknown_number_is_404_before_the_reason_is_checked()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await CancelSalesOrderAsync(seller, NoSuchRecord, "   ");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"Sales order #{NoSuchRecord} not found.");
    }

    /// <summary>
    /// Architecture 4.1 item 2: unlike the ship's, the cancel's body is required. No body at all is <c>400</c> "A request
    /// body is required."; a reason sent as a number is <c>400</c> "reason has the wrong type." under <c>reason</c>. The
    /// sales order is not cancelled.
    /// </summary>
    [Fact]
    public async Task Unreadable_bodies_are_rejected()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        // An empty body sent as JSON. With no Content-Type at all the framework answers 415 first (decision #40).
        await ApiAssert.ProblemAsync(await CancelRawAsync(scene.Seller, number, ""),
            HttpStatusCode.BadRequest, "validation", "A request body is required.");
        await ApiAssert.ValidationAsync(await CancelRawAsync(scene.Seller, number, """{ "reason": 5 }"""),
            "reason", "reason has the wrong type.");

        await AssertStillCreatedAsync(number);
    }

    // ---- AC-27.3: a cancelled sales order never ships -------------------------------------------------------------------

    /// <summary>
    /// AC-27.3, AC-26.7, design 7.2: after a cancel through the API, shipping the sales order gives <c>409</c>
    /// <c>sales-order-cancelled</c> "This sales order is cancelled.", creates no movement and leaves the stock, which
    /// would have covered it, unchanged.
    /// </summary>
    [Fact]
    public async Task Cancelled_sales_order_never_ships()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 50m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 50m));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(scene.Seller, number)).StatusCode);

        var response = await ShipAsync(scene.Warehouse, number);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.ShipCancelled);
        Assert.Empty(await ShipmentMovementsAsync(scene.Manager, number));
        Assert.Equal(50m, await WarehouseAsync(scene.Manager, item.Id));
        Assert.Equal(SalesOrderStatus.Cancelled, (await StoredSalesOrderAsync(factory, number)).Status);
    }

    // ---- AC-27.4: roles ----------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-27.4 (the task's Warehouse-only example, and every other caller holding neither Seller nor Manager), design 7.1:
    /// <c>403</c> "Your roles do not allow this action. It needs the Manager or Seller role.". The sales order stays
    /// Created.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData("Warehouse,Production")]
    public async Task Users_without_Manager_or_Seller_cannot_cancel(string roles)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var client = await factory.CreateClientAsAsync(roles.Split(','));

        var response = await CancelSalesOrderAsync(client, number);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
        await AssertStillCreatedAsync(number);
    }

    /// <summary>
    /// Design 2.2 (the role check comes first): a Warehouse-only user cancelling a number that does not exist, with a
    /// blank reason, gets <c>403</c>, not <c>404</c> or <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Refused_role_gets_403_before_404_and_400()
    {
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await CancelSalesOrderAsync(warehouse, NoSuchRecord, "   ");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
    }

    /// <summary>AC-01.5: the cancel answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Fact]
    public async Task Cancel_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsync($"{SalesOrdersPath}/1/cancel", ApiJson.Content($$"""{ "reason": "{{Reason}}" }"""), Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- AC-26.10, third bullet: a ship and a cancel at the same time ------------------------------------------------

    /// <summary>
    /// AC-26.10 third bullet, design 3.15, architecture 5.2: a ship (Warehouse user) and a cancel (Seller) of the same
    /// two-line sales order, with each ordering forced by <see cref="ForcedOrder"/> (held at the first request's read of
    /// <c>SalesOrders</c>, inside its write transaction). Exactly one succeeds:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>ship first: the ship is <c>200</c>, the cancel <c>409</c> <c>already-shipped</c> "This sales order has been
    /// shipped and cannot be cancelled."; the sales order ends Shipped, with one movement per line, and the stock went
    /// down once;</item>
    /// <item>cancel first: the cancel is <c>200</c>, the ship <c>409</c> <c>sales-order-cancelled</c> "This sales order is
    /// cancelled."; the sales order ends Cancelled, with no movement, and the stock is unchanged.</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("ship")]
    [InlineData("cancel")]
    public async Task Ship_and_cancel_at_once_exactly_one_succeeds(string first)
    {
        await using var race = new ForcedOrder("SalesOrders");
        var manager = await race.App.CreateClientAsAsync(Roles.Manager);
        var warehouse = await race.App.CreateClientAsAsync(Roles.Warehouse);
        var seller = await race.App.CreateClientAsAsync(Roles.Seller);
        var cheese = await RawMaterialAsync(manager, 100m);
        var starch = await RawMaterialAsync(manager, 50m);
        var customer = await CreateCustomerAsync(seller);
        var number = await CreateSalesOrderAsync(seller, customer.Id, (cheese.Id, 60m), (starch.Id, 20m));
        Func<Task<HttpResponseMessage>> ship = () => ShipAsync(warehouse, number);
        Func<Task<HttpResponseMessage>> cancel = () => CancelSalesOrderAsync(seller, number);

        var (winner, loser) = first == "ship" ? await race.RunAsync(ship, cancel) : await race.RunAsync(cancel, ship);

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        var stored = await StoredSalesOrderAsync(race.App, number);
        var movements = await ShipmentMovementsAsync(manager, number);
        if (first == "ship")
        {
            await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.CancelShipped);
            Assert.Equal(SalesOrderStatus.Shipped, stored.Status);
            Assert.Null(stored.CancelledAt);
            Assert.Equal([cheese.Id, starch.Id], movements.Select(movement => movement.GetProperty("itemId").GetInt32()));
            Assert.Equal(40m, await WarehouseAsync(manager, cheese.Id));
            Assert.Equal(30m, await WarehouseAsync(manager, starch.Id));
        }
        else
        {
            await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.ShipCancelled);
            Assert.Equal(SalesOrderStatus.Cancelled, stored.Status);
            Assert.Null(stored.ShippedAt);
            Assert.Empty(movements);
            Assert.Equal(100m, await WarehouseAsync(manager, cheese.Id));
            Assert.Equal(50m, await WarehouseAsync(manager, starch.Id));
        }
    }

    /// <summary>
    /// AC-26.10 third bullet, PRD section 5: a ship and a cancel of the same sales order really arrive at the same moment,
    /// three rounds, each with its own sales order. Whichever wins, exactly one is <c>200</c>, and the outcome is
    /// consistent: Shipped with one movement per line and the cancel's <c>409</c>, or Cancelled with no movement and the
    /// ship's <c>409</c>.
    /// </summary>
    [Fact]
    public async Task Simultaneous_ship_and_cancel_never_both_succeed()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 1000m);
        var other = await RawMaterialAsync(scene.Manager, 1000m);

        for (var round = 0; round < 3; round++)
        {
            var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 10m), (other.Id, 10m));

            var shipTask = ShipAsync(scene.Warehouse, number);
            var cancelTask = CancelSalesOrderAsync(scene.Seller, number);
            await Task.WhenAll(shipTask, cancelTask);
            var (ship, cancel) = (await shipTask, await cancelTask);

            Assert.True((ship.StatusCode == HttpStatusCode.OK) != (cancel.StatusCode == HttpStatusCode.OK),
                $"Round {round}: ship {(int)ship.StatusCode}, cancel {(int)cancel.StatusCode}; exactly one must succeed.");
            var movements = await ShipmentMovementsAsync(scene.Manager, number);
            if (ship.StatusCode == HttpStatusCode.OK)
            {
                await ApiAssert.ProblemAsync(cancel, HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.CancelShipped);
                Assert.Equal(2, movements.Count);
            }
            else
            {
                await ApiAssert.ProblemAsync(ship, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.ShipCancelled);
                Assert.Empty(movements);
            }
        }
    }

    // ---- Two cancels at the same time ------------------------------------------------------------------------------------

    /// <summary>
    /// T-028, architecture 5.2: two cancels of the same sales order, by a Seller and a Manager with different reasons,
    /// with each ordering forced by <see cref="ForcedOrder"/>. The first is <c>200</c>; the second, which waited for the
    /// write lock, gets <c>409</c> <c>sales-order-cancelled</c> "This sales order is already cancelled.". The stored
    /// canceller and reason are the first's.
    /// </summary>
    [Theory]
    [InlineData(Roles.Seller)]
    [InlineData(Roles.Manager)]
    public async Task Two_cancels_at_once_exactly_one_succeeds(string first)
    {
        await using var race = new ForcedOrder("SalesOrders");
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(race.App, Roles.Seller);
        var (manager, managerUser) = await OrderFlow.SignedInAsync(race.App, Roles.Manager);
        var item = await ProductAsync(manager, 0m);
        var customer = await CreateCustomerAsync(seller);
        var number = await CreateSalesOrderAsync(seller, customer.Id, (item.Id, 5m));
        var (firstClient, firstUser, secondClient) = first == Roles.Seller
            ? (seller, sellerUser, manager)
            : (manager, managerUser, seller);

        var (winner, loser) = await race.RunAsync(
            () => CancelSalesOrderAsync(firstClient, number, "First reason"),
            () => CancelSalesOrderAsync(secondClient, number, "Second reason"));

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.AlreadyCancelled);
        var stored = await StoredSalesOrderAsync(race.App, number);
        Assert.Equal(firstUser.Id, stored.CancelledById);
        Assert.Equal("First reason", stored.CancellationReason);
    }

    /// <summary>
    /// T-028, PRD section 5: two cancels of the same sales order really arrive at the same moment, three rounds, each with
    /// its own sales order. Exactly one is <c>200</c> and the other <c>409</c> "This sales order is already cancelled.".
    /// </summary>
    [Fact]
    public async Task Simultaneous_cancels_never_both_succeed()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);

        for (var round = 0; round < 3; round++)
        {
            var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

            var cancelA = CancelSalesOrderAsync(scene.Seller, number);
            var cancelB = CancelSalesOrderAsync(scene.Manager, number);
            await Task.WhenAll(cancelA, cancelB);
            var (a, b) = (await cancelA, await cancelB);

            Assert.True((a.StatusCode == HttpStatusCode.OK) != (b.StatusCode == HttpStatusCode.OK),
                $"Round {round}: {(int)a.StatusCode} and {(int)b.StatusCode}; exactly one must succeed.");
            await ApiAssert.ProblemAsync(a.StatusCode == HttpStatusCode.OK ? b : a,
                HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.AlreadyCancelled);
        }
    }

    // ---- Architecture 6 Logging, decision #163 ---------------------------------------------------------------------------

    /// <summary>
    /// Architecture 6 Logging, decision #163: a cancellation is logged once, after the commit, as "Sales order {n}
    /// cancelled by user {userId}." at Information, and the reason (free text) is not in it. A refused second cancel of
    /// the same sales order (<c>409</c>) adds no such line.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_logged_once_with_number_and_user()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 0m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var reason = Seed.UniqueName("Reason");

        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(scene.Seller, number, reason)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelSalesOrderAsync(scene.Seller, number, reason)).StatusCode);

        var cancelled = factory.Logs.Entries
            .Where(entry => entry.Message.StartsWith($"Sales order {number} cancelled", StringComparison.Ordinal))
            .ToList();
        var entry = Assert.Single(cancelled);
        Assert.Equal($"Sales order {number} cancelled by user {scene.SellerUser.Id}.", entry.Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.DoesNotContain(factory.Logs.Entries, logged => logged.Message.Contains(reason, StringComparison.Ordinal));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The records most tests need, new for each test so tests never share them: a Manager, a Warehouse user and a
    /// Seller (with the Seller's user record), and an active customer, in the shared database.
    /// </summary>
    private async Task<Scene> SceneAsync()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(factory, Roles.Seller);
        var customer = await CreateCustomerAsync(seller);
        return new Scene(manager, warehouse, seller, sellerUser, customer);
    }

    /// <summary>Sends the cancel with exactly this JSON body (an empty string sends an empty body).</summary>
    private static Task<HttpResponseMessage> CancelRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{SalesOrdersPath}/{number}/cancel", ApiJson.Content(json), Cancel);

    /// <summary>The sales order is still Created, with nothing of a cancellation stored (a refused cancel saved nothing).</summary>
    private async Task AssertStillCreatedAsync(int number)
    {
        var stored = await StoredSalesOrderAsync(factory, number);
        Assert.Equal(SalesOrderStatus.Created, stored.Status);
        Assert.Null(stored.CancelledAt);
        Assert.Null(stored.CancelledById);
        Assert.Null(stored.CancellationReason);
    }

    /// <summary>Every movement of the item through <c>GET /movements?itemId=</c>.</summary>
    private static Task<List<JsonElement>> ItemMovementsAsync(HttpClient client, int itemId) =>
        ScenarioApi.AllRowsAsync(client, $"{ScenarioApi.ApiPath}/movements?itemId={itemId}");

    /// <summary>Design 2.3 "People": <c>{ id, email }</c> of <paramref name="user"/>.</summary>
    private static void AssertPerson(JsonElement person, AppUser user)
    {
        Assert.Equal(["id", "email"], person.EnumerateObject().Select(field => field.Name));
        Assert.Equal(user.Id, person.GetProperty("id").GetString());
        Assert.Equal(user.Email, person.GetProperty("email").GetString());
    }

    /// <summary>A stored time as the API writes it: UTC, whole seconds, with <c>Z</c> (architecture 4.1 item 9).</summary>
    private static string ApiTime(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>The per-test records of <see cref="SceneAsync"/>.</summary>
    private sealed record Scene(
        HttpClient Manager, HttpClient Warehouse, HttpClient Seller, AppUser SellerUser, CreatedCustomer Customer);
}
