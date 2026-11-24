using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;
using static InventoryTracker.Api.Tests.Integration.Scenarios.ScenarioApi;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// (v1.1) T-031: the PRD's version 1.1 worked examples as a client sees them, each from an empty database and through
/// HTTP only, like the T-020 scenarios (<see cref="WorkedExampleScenarioTests"/>). One chain runs the shipment examples
/// (AC-26.2 to AC-26.4) with open demand at every step (AC-31.2, AC-28.4), then the December reports (AC-32.3, AC-33.3)
/// and the customer deletes (AC-29.5). A second scenario is a Seller's day (AC-30.4, AC-30.6 to AC-30.8).
/// </summary>
/// <remarks>
/// <para>
/// Each test has a factory of its own, so its database starts empty and its sales order numbers are the PRD's where
/// the order of creation allows it. The times are the PRD's and design 5.11 and 5.12's, set with a fake clock
/// (<see cref="FakeTimeProvider"/>). The clock is not data, so the scenario still writes nothing except through the API
/// (decision #109c). Tokens last 8 hours (AC-01.2) and are checked against that clock, so on each new day every actor
/// signs in again (<see cref="People.SignInAgainAsync"/>).
/// </para>
/// <para>
/// Every scenario ends with the two checks in <see cref="ScenarioApi"/>: AC-05.3 (every movement and every status
/// change, sales orders included, has a user and a UTC time) and AC-11.4 with AC-28.3 (for every item and location the
/// movements, ShippedToCustomer included, add up to the stock, which is never negative).
/// </para>
/// <para>
/// The people are design 5's: maria.lopez (Manager), jorge.ibarra (Warehouse), lucia.mendez (Production) and
/// sofia.vidal (Seller).
/// </para>
/// </remarks>
public class SalesScenarioTests
{
    /// <summary>The finished product of AC-26.2 and design 5.12.</summary>
    private const string ShreddedName = "Shredded cheese 1 kg bag";

    /// <summary>The second finished product of AC-26.2 and design 5.13.</summary>
    private const string GratedName = "Grated cheese 200 g bag";

    /// <summary>AC-26.3's example message, typed out (decision #88). Only the short line is named.</summary>
    private const string SecondIsShort =
        "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales order #2 needs 500 Bag.";

    /// <summary>Design 3.15's cancellation reason.</summary>
    private const string ChangedOrder = "Customer changed the order to 400 bags";

    /// <summary>The whole of December 2026, the period of AC-32.3 and AC-33.3.</summary>
    private const string December = "from=2026-12-01&to=2026-12-31";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- The chain: AC-26.2 to AC-26.4, AC-31.2, AC-28.4, then AC-32.3, AC-33.3 and AC-29.5 ------------------------

    /// <summary>
    /// AC-26.2, AC-26.3, AC-26.4, AC-31.2, AC-28.4, AC-28.2, AC-32.3, AC-32.4, AC-33.3, AC-29.5, AC-05.3, AC-11.4, AC-28.3,
    /// as one chain from an empty database through HTTP:
    /// <list type="bullet">
    /// <item>A Manager sets up kg, Bag, Cheese and the two products with their recipes; production orders #1 (1,000
    /// ordered, 1,001 produced) and #2 (80 Grated) are sent and completed, so the Warehouse holds 1,001 and 80 Bag.</item>
    /// <item>The Seller creates Deli Norte, Cafe Sur and Pizzeria Lago, then sales order #1 (Deli Norte, 600 Bag) and #2
    /// (Cafe Sur, 500 Bag and 50 Bag of Grated). The Warehouse stays 1,001; open demand is 1,100 (AC-31.2, AC-28.4).</item>
    /// <item>2026-12-02: #1 ships: 401 Bag, one −600 movement linked to #1, production storage unchanged, open demand 500
    /// (AC-26.2, AC-31.2). #2 is refused with AC-26.3's exact message; nothing moves. Production order #3 adds 200 Bag:
    /// 601.</item>
    /// <item>2026-12-05: #2 ships (101 and 30 Bag), then #3 (Deli Norte, exactly 101 Bag) ships and leaves 0 (AC-26.4).
    /// Sales order #4 (500 Bag) is created and cancelled: open demand 500, then 0 (AC-31.2). Deli Norte's 300 Bag is
    /// created and cancelled.</item>
    /// <item>The December reports are exactly AC-32.3's and AC-33.3's, and the December shipment movements add up to them
    /// (AC-28.2, AC-32.4).</item>
    /// <item>Pizzeria Lago, never used, is deleted (<c>204</c>); Deli Norte gives <c>409</c> with the PRD sentence
    /// (AC-29.5).</item>
    /// </list>
    /// Where the chain's numbers differ from the PRD's because of the order things are created in, the comment at that
    /// step says so (decision #109c).
    /// </summary>
    [Fact]
    public async Task Sales_chain_from_an_empty_database_end_to_end()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 30, 8, 0, 0, TimeSpan.Zero));
        await using var app = new TestApiFactory { FakeTime = time };
        var people = await PeopleAsync(app);
        var goods = await OpeningStockAsync(people);

        // ---- 2026-12-01: customers and the first two sales orders (AC-29.1, AC-24.2, AC-28.4, AC-31.2) ----
        people = await MorningAsync(app, time, people, Dec(1, 8, 30));
        var seller = people.Seller.Client;
        var deli = await CreateCustomerAsync(seller, "Deli Norte", "Deliveries before 10:00, ask for the kitchen.");
        var cafe = await CreateCustomerAsync(seller, "Cafe Sur");
        var lago = await CreateCustomerAsync(seller, "Pizzeria Lago");
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 1001m, openDemand: 0m);

        time.SetUtcNow(Dec(1, 9));
        Assert.Equal(1, await CreateSalesOrderAsync(seller, deli.Id, (goods.Shredded.Id, 600m)));

        // AC-28.4, AC-24.2: a Created sales order changes no figure but open demand; nothing is reserved (D-11).
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 1001m, openDemand: 600m);

        time.SetUtcNow(Dec(1, 11, 40));
        Assert.Equal(2, await CreateSalesOrderAsync(seller, cafe.Id, (goods.Shredded.Id, 500m), (goods.Grated.Id, 50m)));

        // AC-31.2: "Created sales orders #1 for 600 Bag and #4 for 500 Bag. Open demand is 1,100 Bag and Warehouse stock
        // is still 1,001 Bag." Decision #109c: in one chain the 500 Bag waiting beside #1 is #2's line, not #4's. #2 must
        // exist before #1 ships (AC-26.3 refuses it right after), and AC-26.3's message names it #2, so a separate 500 Bag
        // sales order created now would take number 2 and push #2 to #3. #4 itself is created and cancelled below, for
        // AC-31.2's last step. The figures are the PRD's at every step.
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 1001m, openDemand: 1100m);
        await AssertFinishedStockAsync(seller, goods.Grated, warehouse: 80m, openDemand: 50m);

        // ---- 2026-12-02: #1 ships, #2 is refused, production order #3 makes 200 Bag (AC-26.2 to AC-26.4) ----
        people = await MorningAsync(app, time, people, Dec(2, 9, 15));
        var warehouse = people.Warehouse.Client;
        var shippedFirst = await ShipAsync(warehouse, 1, "Delivery note 8812");

        // AC-26.2: 401 Bag left, one ShippedToCustomer movement of −600 Bag linked to #1, production storage unchanged.
        Assert.Equal(HttpStatusCode.OK, shippedFirst.StatusCode);
        AssertHistory(await ApiJson.ReadAsync(shippedFirst),
            ("Created", "2026-12-01T09:00:00Z", people.Seller, null),
            ("Shipped", "2026-12-02T09:15:00Z", people.Warehouse, "Delivery note 8812"));
        var movement = Assert.Single(await ShipmentMovementsAsync(warehouse, 1));
        AssertShipmentMovement(movement, goods.Shredded, -600m, salesOrder: 1, "2026-12-02T09:15:00Z", people.Warehouse);

        // AC-31.2: "After #1 is shipped, open demand is 500 Bag and Warehouse stock 401 Bag."
        await AssertFinishedStockAsync(warehouse, goods.Shredded, warehouse: 401m, openDemand: 500m);

        // AC-26.3: the whole shipment of #2 is refused, naming only the short line; nothing moves, #2 stays Created.
        time.SetUtcNow(Dec(2, 9, 30));
        var refused = await ShipAsync(warehouse, 2);

        var problem = await ApiAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "insufficient-stock", SecondIsShort);
        Assert.False(problem.TryGetProperty("errors", out _));
        Assert.Empty(await ShipmentMovementsAsync(warehouse, 2));
        Assert.Equal("Created", (await ReadSalesOrderAsync(warehouse, 2)).GetProperty("status").GetString());
        await AssertFinishedStockAsync(warehouse, goods.Shredded, warehouse: 401m, openDemand: 500m);
        await AssertFinishedStockAsync(warehouse, goods.Grated, warehouse: 80m, openDemand: 50m);

        // AC-26.4: "a production order for Shredded cheese 1 kg bag is completed with 200 Bag produced, so the Warehouse
        // holds 601 Bag".
        await ProduceAsync(people, goods.Shredded, goods.Cheese, ordered: 200m, send: 200m, produced: 200m, number: 3,
            time, sentAt: Dec(2, 10, 30), completedAt: Dec(2, 15));
        await AssertFinishedStockAsync(warehouse, goods.Shredded, warehouse: 601m, openDemand: 500m);

        // ---- 2026-12-05: #2 and #3 ship through to 0 Bag (AC-26.4); #4 and the PRD's #7 are cancelled ----
        people = await MorningAsync(app, time, people, Dec(5, 9, 10));
        (seller, warehouse) = (people.Seller.Client, people.Warehouse.Client);
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, 2)).StatusCode);
        await AssertFinishedStockAsync(warehouse, goods.Shredded, warehouse: 101m, openDemand: 0m);
        await AssertFinishedStockAsync(warehouse, goods.Grated, warehouse: 30m, openDemand: 0m);

        // AC-26.4: "Sales order #3 for exactly 101 Bag ... leaves 0 Bag: stock equal to the quantity is enough."
        time.SetUtcNow(Dec(5, 10));
        Assert.Equal(3, await CreateSalesOrderAsync(seller, deli.Id, (goods.Shredded.Id, 101m)));
        time.SetUtcNow(Dec(5, 10, 30));
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, 3)).StatusCode);
        await AssertFinishedStockAsync(warehouse, goods.Shredded, warehouse: 0m, openDemand: 0m);

        // AC-31.2's last step, "After #4 is cancelled, open demand is 0 Bag", with #4 itself (see the 1,100 step above).
        // Open demand above the stock is allowed (AC-31.3). Design 3.15's reason.
        time.SetUtcNow(Dec(5, 11));
        Assert.Equal(4, await CreateSalesOrderAsync(seller, cafe.Id, (goods.Shredded.Id, 500m)));
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 0m, openDemand: 500m);
        time.SetUtcNow(Dec(5, 11, 30));
        var cancelledFourth = await CancelSalesOrderAsync(seller, 4, ChangedOrder);

        Assert.Equal(HttpStatusCode.OK, cancelledFourth.StatusCode);
        AssertHistory(await ApiJson.ReadAsync(cancelledFourth),
            ("Created", "2026-12-05T11:00:00Z", people.Seller, null),
            ("Cancelled", "2026-12-05T11:30:00Z", people.Seller, ChangedOrder));
        Assert.Empty(await ShipmentMovementsAsync(warehouse, 4));
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 0m, openDemand: 0m);

        // AC-32.3: "Sales order #7 (Deli Norte, 300 Bag of Shredded cheese 1 kg bag) is Cancelled." Decision #109c: here it
        // is #5, because the PRD's #5 and #6 are AC-26.10's race example (1,000 Bag, two shipments of 600), which runs with
        // its own database in ShipSalesOrderTests.
        time.SetUtcNow(Dec(5, 12));
        Assert.Equal(5, await CreateSalesOrderAsync(seller, deli.Id, (goods.Shredded.Id, 300m)));
        time.SetUtcNow(Dec(5, 12, 30));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(seller, 5, "Customer no longer needs it")).StatusCode);
        await AssertFinishedStockAsync(seller, goods.Shredded, warehouse: 0m, openDemand: 0m);

        // ---- The December reports (AC-32.3, AC-33.3), read by the Seller and the Warehouse (AC-32.1, AC-33.1) ----
        time.SetUtcNow(Dec(5, 14));
        await AssertDecemberReportsAsync(seller, goods, deli, cafe);
        await AssertDecemberReportsAsync(warehouse, goods, deli, cafe);
        await AssertDecemberShipmentMovementsAsync(warehouse, goods);

        // ---- AC-29.5: an unused customer is deleted; a used one cannot be ----
        await AssertCustomerDeletesAsync(seller, deli, lago);

        // ---- End of scenario: AC-05.3, AC-11.4, AC-28.3 ----
        await AssertScenarioConsistentAsync(people.Admin, orders: 3, salesOrders: 5, items: 3);
    }

    // ---- A Seller's day: AC-30.4, AC-30.6 to AC-30.8 ----------------------------------------------------------------

    /// <summary>
    /// AC-30.4, AC-30.6, AC-30.7, AC-30.8, AC-28.7, AC-27.1, AC-29.5, AC-05.3, AC-11.4, through HTTP from an empty
    /// database. The start-up Manager creates Maria (Manager), Jorge (Warehouse), Sofia (Seller only) and Pablo
    /// (Production plus Seller); Maria sets up one product with 100 Bag in the Warehouse.
    /// <list type="bullet">
    /// <item>Sofia creates two customers and three sales orders, gets <c>403</c> shipping one (Jorge ships it), and
    /// cancels another.</item>
    /// <item>She reads stock (all items and one), units, items, customers, sales orders and both reports with
    /// <c>200</c>, and gets <c>403</c> with the exact sentence on receipts, adjustments, movements and production orders
    /// (list and create). The refused requests change nothing.</item>
    /// <item>Pablo reads movements (the shipment, with its sales order number), production orders, customers, sales
    /// orders and both reports with <c>200</c>.</item>
    /// <item>Cafe Sur, whose only sales order is Cancelled, cannot be deleted (AC-29.5: "even if every one is
    /// Cancelled"); Sofia deactivates it instead.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task A_sellers_day_end_to_end()
    {
        var time = new FakeTimeProvider(Dec(1, 8));
        await using var app = new TestApiFactory { FakeTime = time };
        var admin = await StartUpManagerAsync(app);
        var maria = await CreateStaffAsync(app, admin, "maria.lopez@example.com", Roles.Manager);
        var jorge = await CreateStaffAsync(app, admin, "jorge.ibarra@example.com", Roles.Warehouse);
        var sofia = await CreateStaffAsync(app, admin, "sofia.vidal@example.com", Roles.Seller);
        var pablo = await CreateStaffAsync(app, admin, "pablo.rios@example.com", Roles.Production, Roles.Seller);
        var bag = await Seed.CreateUnitAsync(maria.Client, "Bag");
        var shredded = await Seed.CreateItemAsync(maria.Client, ItemType.FinishedProduct, bag, name: ShreddedName);

        // A finished product cannot be received (AC-09.3); a Manager's count puts it into the Warehouse (US-12).
        await AdjustAsync(maria.Client, shredded.Id, "Warehouse", 100m, OpeningCount);

        // AC-29.1, AC-24.1: the Seller creates customers and sales orders.
        time.SetUtcNow(Dec(1, 8, 30));
        var sol = await CreateCustomerAsync(sofia.Client, "Panaderia Sol");
        var cafe = await CreateCustomerAsync(sofia.Client, "Cafe Sur");
        time.SetUtcNow(Dec(1, 9));
        Assert.Equal(1, await CreateSalesOrderAsync(sofia.Client, sol.Id, (shredded.Id, 30m)));
        Assert.Equal(2, await CreateSalesOrderAsync(sofia.Client, cafe.Id, (shredded.Id, 20m)));
        Assert.Equal(3, await CreateSalesOrderAsync(sofia.Client, sol.Id, (shredded.Id, 10m)));

        // AC-30.4, AC-26.9: a Seller does not ship; the Warehouse does.
        time.SetUtcNow(Dec(1, 10));
        await ApiAssert.ProblemAsync(await ShipAsync(sofia.Client, 1), HttpStatusCode.Forbidden, "forbidden",
            ExpectedMessages.WarehouseOrManager);
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(jorge.Client, 1)).StatusCode);

        // AC-27.1: the Seller cancels a Created sales order with a reason.
        time.SetUtcNow(Dec(1, 10, 30));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(sofia.Client, 2, "Ordered twice by mistake")).StatusCode);

        // AC-30.7: the reads a Seller has. Stock: 100 − 30 shipped; open demand 10 (#3 only; #2 is Cancelled).
        time.SetUtcNow(Dec(1, 11));
        await AssertFinishedStockAsync(sofia.Client, shredded, warehouse: 70m, openDemand: 10m);
        var stockRow = Assert.Single((await GetAsync(sofia.Client, $"{ApiPath}/stock")).GetProperty("items").EnumerateArray());
        Assert.Equal(70m, stockRow.GetProperty("warehouse").GetDecimal());
        Assert.Equal(1, (await GetAsync(sofia.Client, $"{ApiPath}/units")).GetProperty("totalCount").GetInt32());
        Assert.Equal("Bag", (await GetAsync(sofia.Client, $"{ApiPath}/units/{bag.Id}")).GetProperty("name").GetString());
        Assert.Equal(1, (await GetAsync(sofia.Client, $"{ApiPath}/items")).GetProperty("totalCount").GetInt32());
        Assert.Equal(ShreddedName, (await GetAsync(sofia.Client, $"{ApiPath}/items/{shredded.Id}")).GetProperty("name").GetString());
        await AssertSalesReadsAsync(sofia.Client, shredded, sol);

        // AC-30.4, AC-30.6: what a Seller-only user is refused, each with the design 7.1 sentence naming the roles.
        await AssertForbiddenAsync(sofia.Client.PostAsJsonAsync($"{ApiPath}/receipts",
            new { itemId = shredded.Id, quantity = 5m, receiptDate = "2026-12-01" }, Cancel), ExpectedMessages.WarehouseOrManager);
        await AssertForbiddenAsync(sofia.Client.PostAsJsonAsync($"{ApiPath}/adjustments",
            new { itemId = shredded.Id, location = "Warehouse", quantity = -5m, reason = "Count" }, Cancel), ExpectedMessages.ManagerOnly);
        await AssertForbiddenAsync(sofia.Client.GetAsync($"{ApiPath}/movements", Cancel), ExpectedMessages.WarehouseProductionOrManager);
        await AssertForbiddenAsync(sofia.Client.GetAsync(OrdersPath, Cancel), ExpectedMessages.WarehouseProductionOrManager);
        await AssertForbiddenAsync(sofia.Client.PostAsJsonAsync(OrdersPath,
            new { finishedProductId = shredded.Id, orderedQuantity = 10m }, Cancel), ExpectedMessages.ManagerOnly);

        // The refusals changed nothing: the stock is as before and #3 is still waiting.
        await AssertFinishedStockAsync(jorge.Client, shredded, warehouse: 70m, openDemand: 10m);
        Assert.Equal("Created", (await ReadSalesOrderAsync(jorge.Client, 3)).GetProperty("status").GetString());

        // AC-30.8, AC-28.7: Production plus Seller reads movements and production orders (from Production) and
        // customers, sales orders and both reports (from Seller).
        var shipment = Assert.Single(await ShipmentMovementsAsync(pablo.Client, 1));
        Assert.Equal("ShippedToCustomer", shipment.GetProperty("type").GetString());
        Assert.Equal(1, shipment.GetProperty("salesOrderNumber").GetInt32());
        Assert.Equal(0, (await GetAsync(pablo.Client, OrdersPath)).GetProperty("totalCount").GetInt32());
        await AssertSalesReadsAsync(pablo.Client, shredded, sol);

        // AC-29.5: Cafe Sur's only sales order is Cancelled, and it still cannot be deleted; it is deactivated instead.
        await ApiAssert.ProblemAsync(await sofia.Client.DeleteAsync($"{CustomersPath}/{cafe.Id}", Cancel),
            HttpStatusCode.Conflict, "customer-has-sales-orders", "Cafe Sur has sales orders and cannot be deleted. Deactivate it instead.");
        var deactivated = await sofia.Client.PostAsync($"{CustomersPath}/{cafe.Id}/deactivate", content: null, Cancel);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False((await ApiJson.ReadAsync(deactivated)).GetProperty("isActive").GetBoolean());

        // End of scenario: AC-05.3 (three sales orders), AC-11.4 and AC-28.3 (one item).
        var audit = await AssertEveryChangeIsAttributedAsync(admin);
        Assert.Equal((0, 0, 0, 3), (audit.Orders, audit.ComplementaryOrders, audit.Returns, audit.SalesOrders));
        Assert.Equal(2, audit.Movements);
        Assert.Equal(1, await AssertMovementsAddUpToStockAsync(admin));
    }

    // ---- Set-up through the API -------------------------------------------------------------------------------------

    /// <summary>
    /// Design 3.2 and 3.3 from an empty database: the start-up Manager signs in and creates Maria (Manager), Jorge
    /// (Warehouse), Lucia (Production) and Sofia (Seller) through <c>POST /users</c>.
    /// </summary>
    private static async Task<People> PeopleAsync(TestApiFactory app)
    {
        var admin = await StartUpManagerAsync(app);
        return new People(admin,
            await CreateStaffAsync(app, admin, "maria.lopez@example.com", Roles.Manager),
            await CreateStaffAsync(app, admin, "jorge.ibarra@example.com", Roles.Warehouse),
            await CreateStaffAsync(app, admin, "lucia.mendez@example.com", Roles.Production),
            await CreateStaffAsync(app, admin, "sofia.vidal@example.com", Roles.Seller));
    }

    /// <summary>
    /// The stock AC-26.2 starts from, made the way the business makes it (design 3.4 to 3.6). Maria creates kg, Bag,
    /// Cheese and the two products, with recipes of 1 kg and 0.2 kg of Cheese per bag (AC-08.3). Jorge receives 1,500 kg
    /// of Cheese. Production order #1 (1,000 bags ordered, 1,000 kg sent) is completed with 1,001 bags, as in worked
    /// example A (AC-20.5), and #2 (80 bags of Grated, 16 kg sent) with 80. The Warehouse then holds 1,001 Bag of
    /// Shredded and 80 Bag of Grated cheese, and nothing else exists: these three items and five users.
    /// </summary>
    private static async Task<Goods> OpeningStockAsync(People people)
    {
        var manager = people.Manager.Client;
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var shredded = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: ShreddedName);
        var grated = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: GratedName);
        await PutRecipeAsync(manager, shredded.Id, (cheese.Id, 1m));
        await PutRecipeAsync(manager, grated.Id, (cheese.Id, 0.2m));
        await ReceiveAsync(people.Warehouse.Client, cheese.Id, 1500m, receiptDate: "2026-11-30");

        await ProduceAsync(people, shredded, cheese, ordered: 1000m, send: 1000m, produced: 1001m, number: 1);
        await ProduceAsync(people, grated, cheese, ordered: 80m, send: 16m, produced: 80m, number: 2);

        Assert.Equal(3, (await GetAsync(manager, $"{ApiPath}/items")).GetProperty("totalCount").GetInt32());
        Assert.Equal(5, (await GetAsync(people.Admin, $"{ApiPath}/users")).GetProperty("totalCount").GetInt32());
        await AssertStockAsync(manager, shredded.Id, warehouse: 1001m, productionStorage: 0m);
        await AssertStockAsync(manager, grated.Id, warehouse: 80m, productionStorage: 0m);
        await AssertStockAsync(manager, cheese.Id, warehouse: 1500m - 1000m - 16m, productionStorage: 0m);
        return new Goods(cheese, shredded, grated);
    }

    /// <summary>
    /// One production order through its whole life (US-13, US-15, US-19): Maria creates it for
    /// <paramref name="ordered"/> of <paramref name="product"/> (checked to get <paramref name="number"/>), Jorge sends
    /// <paramref name="send"/> of Cheese, Lucia completes it with <paramref name="produced"/> and no leftover, which puts
    /// the output into the Warehouse (ProducedOutput, D-2). With <paramref name="time"/>, the send and the completion
    /// happen at <paramref name="sentAt"/> and <paramref name="completedAt"/>.
    /// </summary>
    private static async Task ProduceAsync(People people, CreatedItem product, CreatedItem cheese, decimal ordered,
        decimal send, decimal produced, int number, FakeTimeProvider? time = null, DateTimeOffset sentAt = default,
        DateTimeOffset completedAt = default)
    {
        Assert.Equal(number, await CreateOrderAsync(people.Manager.Client, product.Id, ordered));
        time?.SetUtcNow(sentAt);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(people.Warehouse.Client, number, (cheese.Id, send))).StatusCode);
        time?.SetUtcNow(completedAt);
        var completed = await CompleteAsync(people.Production.Client, number, produced, reason: null, (cheese.Id, 0m));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("Completed", (await ApiJson.ReadAsync(completed)).GetProperty("status").GetString());
    }

    /// <summary>
    /// Moves the clock forward to <paramref name="at"/> and signs everyone in again, because the earlier tokens may have
    /// passed their 8 hours (AC-01.2). Returns the people with their new clients.
    /// </summary>
    private static async Task<People> MorningAsync(TestApiFactory app, FakeTimeProvider time, People people, DateTimeOffset at)
    {
        time.SetUtcNow(at);
        return await people.SignInAgainAsync(app);
    }

    /// <summary>A time in December 2026, UTC.</summary>
    private static DateTimeOffset Dec(int day, int hour, int minute = 0) => new(2026, 12, day, hour, minute, 0, TimeSpan.Zero);

    // ---- Assertions -------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-28.4, AC-31.1, design 5.5: a finished product's whole stock row through <c>GET /stock/{itemId}</c>: this
    /// Warehouse stock and open demand, production storage 0 (finished products never go there, D-2), and the four
    /// leftover figures <c>null</c>. So a sales order step that changes any figure other than open demand fails here.
    /// </summary>
    private static async Task AssertFinishedStockAsync(HttpClient client, CreatedItem item, decimal warehouse, decimal openDemand)
    {
        var row = await StockAsync(client, item.Id);
        Assert.Equal(item.Name, row.GetProperty("itemName").GetString());
        Assert.Equal(warehouse, row.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, row.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(openDemand, await OpenDemandAsync(client, item.Id));
        foreach (var field in new[] { "committedToOrder", "committedOrderNumber", "pendingReturns", "freeLeftover" })
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
        }
    }

    /// <summary>
    /// AC-05.3, AC-25.2, design 5.12: a sales order's <c>history</c> is exactly these entries, oldest first: status, UTC
    /// time, who (id and email) and note.
    /// </summary>
    private static void AssertHistory(JsonElement salesOrder, params (string Status, string At, Staff By, string? Note)[] expected)
    {
        var history = salesOrder.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(expected.Length, history.Count);
        foreach (var (entry, want) in history.Zip(expected))
        {
            Assert.Equal(want.Status, entry.GetProperty("status").GetString());
            Assert.Equal(want.At, entry.GetProperty("at").GetString());
            Assert.Equal(want.By.Id, entry.GetProperty("by").GetProperty("id").GetString());
            Assert.Equal(want.By.Email, entry.GetProperty("by").GetProperty("email").GetString());
            Assert.Equal(want.Note, entry.GetProperty("note").GetString());
        }
    }

    /// <summary>
    /// AC-28.1, design 5.6: a ShippedToCustomer movement: the item and unit, the negative Warehouse change, no production
    /// storage change, the sales order number and nothing else linked, the shipment time and the shipper.
    /// </summary>
    private static void AssertShipmentMovement(
        JsonElement movement, CreatedItem item, decimal change, int salesOrder, string at, Staff by)
    {
        Assert.Equal("ShippedToCustomer", movement.GetProperty("type").GetString());
        Assert.Equal(item.Id, movement.GetProperty("itemId").GetInt32());
        Assert.Equal(item.UnitName, movement.GetProperty("unit").GetString());
        Assert.Equal(change, movement.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(salesOrder, movement.GetProperty("salesOrderNumber").GetInt32());
        Assert.Equal(at, movement.GetProperty("occurredAt").GetString());
        Assert.Equal(by.Email, movement.GetProperty("user").GetProperty("email").GetString());
        foreach (var field in new[] { "productionStorageChange", "orderNumber", "complementaryOrderNumber", "returnNumber", "receiptDate", "note", "reason" })
        {
            Assert.Equal(JsonValueKind.Null, movement.GetProperty(field).ValueKind);
        }
    }

    /// <summary>
    /// AC-32.3 and AC-33.3, word for word, with #4 and Deli Norte's 300 Bag Cancelled (AC-32.2: they do not count).
    /// Shipments: #1 (Deli Norte, 600 Bag) on 2026-12-02; #2 (Cafe Sur, 500 Bag and 50 Bag of Grated) and #3 (Deli Norte,
    /// 101 Bag) on 2026-12-05.
    /// </summary>
    private static async Task AssertDecemberReportsAsync(HttpClient reader, Goods goods, CreatedCustomer deli, CreatedCustomer cafe)
    {
        // AC-32.3: all of December, A to Z by item name.
        AssertItemRows(await ShippedByItemAsync(reader, December),
            (goods.Grated, 50m, 1), (goods.Shredded, 1201m, 3));

        // AC-32.3: "The same with customer Deli Norte: Shredded cheese 1 kg bag 701 Bag, 2 sales orders. No Grated cheese row."
        AssertItemRows(await ShippedByItemAsync(reader, $"{December}&customerId={deli.Id}"),
            (goods.Shredded, 701m, 2));

        // AC-32.3: from 2026-12-03, #1's shipment on the 2nd is left out.
        AssertItemRows(await ShippedByItemAsync(reader, "from=2026-12-03&to=2026-12-31"),
            (goods.Grated, 50m, 1), (goods.Shredded, 601m, 2));

        // AC-33.3: one row per customer and item, A to Z by customer name, then item name.
        AssertCustomerRows(await ShippedByCustomerAsync(reader, December),
            (cafe, goods.Grated, 50m, 1), (cafe, goods.Shredded, 500m, 1), (deli, goods.Shredded, 701m, 2));

        // AC-33.3: "Filtered by item Grated cheese 200 g bag: only the Cafe Sur row."
        AssertCustomerRows(await ShippedByCustomerAsync(reader, $"{December}&itemId={goods.Grated.Id}"),
            (cafe, goods.Grated, 50m, 1));
    }

    /// <summary>
    /// AC-28.2's example and AC-32.4: the movements of "Shredded cheese 1 kg bag" of type ShippedToCustomer from
    /// 2026-12-01 to 2026-12-31 are exactly the three shipments, #1 to #3, and add up to −1,201 Bag, the report's
    /// shipped quantity with the sign reversed.
    /// </summary>
    private static async Task AssertDecemberShipmentMovementsAsync(HttpClient reader, Goods goods)
    {
        var movements = await AllRowsAsync(reader,
            $"{ApiPath}/movements?itemId={goods.Shredded.Id}&type=ShippedToCustomer&{December}");

        Assert.Equal([1, 2, 3], movements.Select(movement => movement.GetProperty("salesOrderNumber").GetInt32()).Order());
        Assert.Equal(-1201m, movements.Sum(movement => movement.GetProperty("warehouseChange").GetDecimal()));
    }

    /// <summary>
    /// AC-29.5's example. "Pizzeria Lago is created by mistake and never used; deleting it succeeds": <c>204</c>, then
    /// <c>404</c> on viewing it, gone from the list, and its name free for a new customer. "Deli Norte has one Cancelled
    /// sales order; deleting it is rejected with 409" and the PRD sentence. Decision #109c: in this chain Deli Norte also
    /// has #1 and #3, Shipped; the answer is the same for any status. (The Seller's day proves the only-Cancelled case.)
    /// </summary>
    private static async Task AssertCustomerDeletesAsync(HttpClient seller, CreatedCustomer deli, CreatedCustomer lago)
    {
        var deleted = await seller.DeleteAsync($"{CustomersPath}/{lago.Id}", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync($"{CustomersPath}/{lago.Id}", Cancel)).StatusCode);
        Assert.DoesNotContain(lago.Id, (await AllRowsAsync(seller, CustomersPath)).Select(row => row.GetProperty("id").GetInt32()));
        Assert.NotEqual(lago.Id, (await CreateCustomerAsync(seller, "Pizzeria Lago")).Id);

        await ApiAssert.ProblemAsync(await seller.DeleteAsync($"{CustomersPath}/{deli.Id}", Cancel),
            HttpStatusCode.Conflict, "customer-has-sales-orders", ExpectedMessages.DeliNorteHasSalesOrders);
        Assert.Equal(HttpStatusCode.OK, (await seller.GetAsync($"{CustomersPath}/{deli.Id}", Cancel)).StatusCode);
    }

    /// <summary>
    /// AC-30.7, AC-30.8, AC-29.6, AC-25.1, AC-32.1, AC-33.1: the Seller's reads in the Seller's day, all <c>200</c> with
    /// the day's records: two customers, three sales orders (#1 opened in full), and one shipped row in each report
    /// (Panaderia Sol, 30 Bag, one sales order).
    /// </summary>
    private static async Task AssertSalesReadsAsync(HttpClient reader, CreatedItem shredded, CreatedCustomer sol)
    {
        Assert.Equal(2, (await GetAsync(reader, CustomersPath)).GetProperty("totalCount").GetInt32());
        Assert.Equal("Panaderia Sol", (await GetAsync(reader, $"{CustomersPath}/{sol.Id}")).GetProperty("name").GetString());
        Assert.Equal(3, (await GetAsync(reader, SalesOrdersPath)).GetProperty("totalCount").GetInt32());
        Assert.Equal("Shipped", (await ReadSalesOrderAsync(reader, 1)).GetProperty("status").GetString());
        AssertItemRows(await ShippedByItemAsync(reader), (shredded, 30m, 1));
        AssertCustomerRows(await ShippedByCustomerAsync(reader), (sol, shredded, 30m, 1));
    }

    /// <summary>
    /// AC-32.1, design 5.13: a shipped-by-item page is exactly these rows, in this order: item, type, unit, shipped
    /// quantity and sales order count.
    /// </summary>
    private static void AssertItemRows(JsonElement page, params (CreatedItem Item, decimal Quantity, int Count)[] expected)
    {
        var rows = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected.Length, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Length, rows.Count);
        foreach (var (row, want) in rows.Zip(expected))
        {
            AssertItemFields(row, want.Item, want.Quantity, want.Count);
        }
    }

    /// <summary>
    /// AC-33.1, design 5.13: a shipped-by-customer page is exactly these rows, in this order: the customer (id, current
    /// name, active), then the item fields.
    /// </summary>
    private static void AssertCustomerRows(
        JsonElement page, params (CreatedCustomer Customer, CreatedItem Item, decimal Quantity, int Count)[] expected)
    {
        var rows = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected.Length, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Length, rows.Count);
        foreach (var (row, want) in rows.Zip(expected))
        {
            var customer = row.GetProperty("customer");
            Assert.Equal(want.Customer.Id, customer.GetProperty("id").GetInt32());
            Assert.Equal(want.Customer.Name, customer.GetProperty("name").GetString());
            Assert.True(customer.GetProperty("isActive").GetBoolean());
            AssertItemFields(row, want.Item, want.Quantity, want.Count);
        }
    }

    private static void AssertItemFields(JsonElement row, CreatedItem item, decimal quantity, int count)
    {
        Assert.Equal(item.Id, row.GetProperty("itemId").GetInt32());
        Assert.Equal(item.Name, row.GetProperty("itemName").GetString());
        Assert.Equal("FinishedProduct", row.GetProperty("itemType").GetString());
        Assert.Equal(item.UnitName, row.GetProperty("unit").GetString());
        Assert.Equal(quantity, row.GetProperty("shippedQuantity").GetDecimal());
        Assert.Equal(count, row.GetProperty("salesOrderCount").GetInt32());
    }

    /// <summary>AC-30.4, AC-30.6, design 7.1: the request is refused with <c>403</c> <c>forbidden</c> and this sentence.</summary>
    private static async Task AssertForbiddenAsync(Task<HttpResponseMessage> request, string message) =>
        await ApiAssert.ProblemAsync(await request, HttpStatusCode.Forbidden, "forbidden", message);

    /// <summary>
    /// The two end-of-scenario checks (AC-05.3 with every sales order's history; AC-11.4 and AC-28.3), with the number
    /// of records each kind of audit read, so a scenario that silently created nothing fails.
    /// </summary>
    private static async Task AssertScenarioConsistentAsync(HttpClient manager, int orders, int salesOrders, int items)
    {
        var audit = await AssertEveryChangeIsAttributedAsync(manager);
        Assert.Equal((orders, 0, 0, salesOrders), (audit.Orders, audit.ComplementaryOrders, audit.Returns, audit.SalesOrders));
        Assert.True(audit.Movements > 0);
        Assert.Equal(items, await AssertMovementsAddUpToStockAsync(manager));
    }

    /// <summary>The start-up Manager and the four design 5 staff, with clients signed in now.</summary>
    private sealed record People(HttpClient Admin, Staff Manager, Staff Warehouse, Staff Production, Staff Seller)
    {
        /// <summary>Everyone signs in again at the clock's current time (AC-01.2: tokens last 8 hours).</summary>
        public async Task<People> SignInAgainAsync(TestApiFactory app) => new(
            await StartUpManagerAsync(app),
            await AgainAsync(app, Manager), await AgainAsync(app, Warehouse),
            await AgainAsync(app, Production), await AgainAsync(app, Seller));

        private static async Task<Staff> AgainAsync(TestApiFactory app, Staff staff) =>
            staff with { Client = app.CreateClientWithToken(await app.SignInAsync(staff.Email)) };
    }

    /// <summary>The raw material and the two finished products of the chain.</summary>
    private sealed record Goods(CreatedItem Cheese, CreatedItem Shredded, CreatedItem Grated);
}
