using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using InventoryTracker.Api.Tests.Integration.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-027: ship a sales order, <c>POST /api/v1/sales-orders/{number}/ship</c> (US-26 AC-26.1 to AC-26.9, AC-26.10
/// first and second bullets, AC-26.11 to AC-26.13; AC-28.1 for the movements it writes; AC-27.3 and AC-29.4; AC-05.3;
/// design 3.13, 3.14, 5.12 and 7.2 "Sales orders"; architecture 5.1 check order, 5.2 and 5.3; decision #158).
/// </summary>
/// <remarks>
/// <para>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>), so each makes its own items and
/// customer and reads only its own sales orders and stock. A test that checks a PRD sentence word for word ("Shredded
/// cheese 1 kg bag", "sales order #2"), needs a sales order number from the PRD, or needs an InProduction production
/// order (only one may exist per database, AC-15.10) creates its own <see cref="TestApiFactory"/>.
/// </para>
/// <para>
/// Finished products cannot be received (AC-09.3), so tests put them into the Warehouse with a positive Warehouse
/// adjustment, the same movement a stock count would write (US-12). Raw materials are received.
/// </para>
/// </remarks>
public class ShipSalesOrderTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string ShreddedName = "Shredded cheese 1 kg bag";
    private const string GratedName = "Grated cheese 200 g bag";
    private const string NoteTooLong = "Note must be 500 characters or fewer.";

    /// <summary>A character outside the Basic Multilingual Plane: two UTF-16 code units, one code point (decision #116).</summary>
    private const string Emoji = "\U0001F9C0";

    private const int NoSuchRecord = 999_999;

    /// <summary>The design 5.12 example's creation time, 09:00 UTC on 1 December 2026.</summary>
    private static readonly DateTimeOffset NineAm = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-26.1, AC-26.2: a shipment -------------------------------------------------------------------------------

    /// <summary>
    /// AC-26.1, AC-26.2 (PRD example), AC-05.3, AC-28.1, design 5.12 and 5.6, decision #158: the Warehouse holds 1,001
    /// Bag of Shredded cheese 1 kg bag and 80 Bag of Grated cheese 200 g bag. A Warehouse user ships sales order #1 (600
    /// Bag for Deli Norte) the next morning with the note "Delivery note 8812". <c>200</c> with the design 5.12 Shipped
    /// detail: the note, and a Shipped history entry with the shipper and the time. The Warehouse is left with 401 and 80
    /// Bag, production storage is unchanged, and exactly one movement exists, the design 5.6 ShippedToCustomer example
    /// field for field, whose <c>occurredAt</c> is the Shipped entry's <c>at</c>.
    /// </summary>
    [Fact]
    public async Task Warehouse_user_ships_sales_order_1_with_the_design_detail_and_one_movement()
    {
        var time = new FakeTimeProvider(NineAm.AddMilliseconds(400));
        await using var app = new TestApiFactory { FakeTime = time };
        var example = await CheeseBagsAsync(app);
        var number = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id, (example.Shredded.Id, 600m));
        Assert.Equal(1, number);

        // Design 5.12: shipped the next morning at 09:15. A token lasts 8 hours (AC-01.2), so the Warehouse user signs
        // in at the new time.
        time.SetUtcNow(new DateTimeOffset(2026, 12, 2, 9, 15, 0, 700, TimeSpan.Zero));
        var (warehouse, shipper) = await OrderFlow.SignedInAsync(app, Roles.Warehouse);

        var response = await ShipAsync(warehouse, number, "Delivery note 8812");
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["number", "customer", "customerReference", "status", "createdAt", "createdBy", "shipmentNote", "cancellationReason", "lines", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, body.GetProperty("number").GetInt32());
        Assert.Equal("Shipped", body.GetProperty("status").GetString());
        Assert.Equal("Delivery note 8812", body.GetProperty("shipmentNote").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancellationReason").ValueKind);
        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal(600m, line.GetProperty("quantity").GetDecimal());

        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", "Shipped"], history.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("2026-12-01T09:00:00Z", history[0].GetProperty("at").GetString());
        AssertPerson(history[0].GetProperty("by"), example.SellerUser);
        Assert.Equal("2026-12-02T09:15:00Z", history[1].GetProperty("at").GetString());
        AssertPerson(history[1].GetProperty("by"), shipper);
        Assert.Equal("Delivery note 8812", history[1].GetProperty("note").GetString());

        // Decision #161: the ship answers with exactly the detail GET shows.
        Assert.Equal(body.GetRawText(), (await ReadSalesOrderAsync(warehouse, number)).GetRawText());

        // AC-26.2: 1,001 − 600 = 401; the Grated cheese and production storage are untouched.
        await AssertStockAsync(warehouse, example.Shredded.Id, warehouse: 401m, productionStorage: 0m);
        await AssertStockAsync(warehouse, example.Grated.Id, warehouse: 80m, productionStorage: 0m);

        // AC-28.1, design 5.6: the one movement, field for field.
        var movement = Assert.Single(await ShipmentMovementsAsync(warehouse, number));
        Assert.Equal(
            ["id", "type", "itemId", "itemName", "unit", "warehouseChange", "productionStorageChange", "occurredAt", "user",
             "orderNumber", "complementaryOrderNumber", "returnNumber", "salesOrderNumber", "receiptDate", "note", "reason"],
            movement.EnumerateObject().Select(field => field.Name));
        Assert.Equal("ShippedToCustomer", movement.GetProperty("type").GetString());
        Assert.Equal(example.Shredded.Id, movement.GetProperty("itemId").GetInt32());
        Assert.Equal(ShreddedName, movement.GetProperty("itemName").GetString());
        Assert.Equal("Bag", movement.GetProperty("unit").GetString());
        Assert.Equal(-600m, movement.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal("2026-12-02T09:15:00Z", movement.GetProperty("occurredAt").GetString());
        AssertPerson(movement.GetProperty("user"), shipper);
        Assert.Equal(1, movement.GetProperty("salesOrderNumber").GetInt32());
        foreach (var empty in new[] { "productionStorageChange", "orderNumber", "complementaryOrderNumber", "returnNumber", "receiptDate", "note", "reason" })
        {
            Assert.Equal(JsonValueKind.Null, movement.GetProperty(empty).ValueKind);
        }

        // Architecture 5.3 step 7, decision #158: one clock read for ShippedAt, StatusChangedAt and the movement.
        var stored = await StoredSalesOrderAsync(app, number);
        Assert.Equal(shipper.Id, stored.ShippedById);
        Assert.Equal(stored.ShippedAt, stored.StatusChangedAt);
        Assert.Equal("2026-12-02T09:15:00Z", ApiTime(stored.ShippedAt!.Value));
    }

    /// <summary>
    /// AC-26.1, AC-28.1, AC-24.8, decision #158: a Manager ships a sales order of three lines, raw materials and a
    /// finished product. One ShippedToCustomer movement per line, in line order, each the line's quantity as a negative
    /// Warehouse change with the sales order number; every movement's <c>occurredAt</c> equals the Shipped entry's
    /// <c>at</c> and the stored <c>ShippedAt</c> and <c>StatusChangedAt</c>; each item's Warehouse stock goes down by its
    /// line's quantity.
    /// </summary>
    /// <remarks>
    /// The app's clock moves one second forward every time it is read (<c>AutoAdvanceAmount</c>). So the times can only
    /// all be equal if the ship reads the clock once for the whole shipment, as decision #158 requires; a read per
    /// movement would give three different seconds.
    /// </remarks>
    [Fact]
    public async Task Every_line_becomes_one_movement_at_the_one_shipment_time()
    {
        var time = new FakeTimeProvider(NineAm) { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await SceneAsync(app);
        var cheese = await RawMaterialAsync(scene.Manager, 50m);
        var bags = await ProductAsync(scene.Manager, 30m);
        var starch = await RawMaterialAsync(scene.Manager, 10.5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id,
            (cheese.Id, 12.345m), (bags.Id, 30m), (starch.Id, 0.5m));

        var response = await ShipAsync(scene.Manager, number);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shipped = body.GetProperty("history")[1];
        Assert.Equal("Shipped", shipped.GetProperty("status").GetString());
        AssertPerson(shipped.GetProperty("by"), scene.ManagerUser);

        var movements = await ShipmentMovementsAsync(scene.Warehouse, number);
        Assert.Equal([cheese.Id, bags.Id, starch.Id], movements.Select(movement => movement.GetProperty("itemId").GetInt32()));
        Assert.Equal([-12.345m, -30m, -0.5m], movements.Select(movement => movement.GetProperty("warehouseChange").GetDecimal()));
        Assert.All(movements, movement =>
        {
            Assert.Equal("ShippedToCustomer", movement.GetProperty("type").GetString());
            Assert.Equal(number, movement.GetProperty("salesOrderNumber").GetInt32());
            Assert.Equal(shipped.GetProperty("at").GetString(), movement.GetProperty("occurredAt").GetString());
            AssertPerson(movement.GetProperty("user"), scene.ManagerUser);
        });

        var stored = await StoredSalesOrderAsync(app, number);
        Assert.Equal(shipped.GetProperty("at").GetString(), ApiTime(stored.ShippedAt!.Value));
        Assert.Equal(stored.ShippedAt, stored.StatusChangedAt);

        Assert.Equal(37.655m, await WarehouseAsync(scene.Manager, cheese.Id));
        Assert.Equal(0m, await WarehouseAsync(scene.Manager, bags.Id));
        Assert.Equal(10m, await WarehouseAsync(scene.Manager, starch.Id));
    }

    // ---- AC-26.3, AC-26.4: a refused shipment and the shortfall resolved ------------------------------------------------

    /// <summary>
    /// AC-26.3 (PRD example, continuing AC-26.2), design 3.14 and 7.2: after #1 has shipped, the Warehouse holds 401 Bag
    /// of Shredded cheese and 80 Bag of Grated cheese. Sales order #2 (500 Bag and 50 Bag) is refused with <c>409</c>
    /// <c>insufficient-stock</c> and exactly "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales
    /// order #2 needs 500 Bag.", with no <c>errors</c> field. The covered Grated cheese line is not named; no movement is
    /// created for either line; #2 stays Created; the stock stays 401 and 80.
    /// </summary>
    [Fact]
    public async Task Short_line_refuses_the_whole_shipment_and_names_only_that_line()
    {
        await using var app = new TestApiFactory();
        var example = await CheeseBagsAsync(app);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var first = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id, (example.Shredded.Id, 600m));
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, first)).StatusCode);
        var second = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id,
            (example.Shredded.Id, 500m), (example.Grated.Id, 50m));
        Assert.Equal(2, second);

        var response = await ShipAsync(warehouse, second, "Delivery note 8813");

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "insufficient-stock",
            "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales order #2 needs 500 Bag.");
        AssertNoErrorsField(body);
        Assert.Empty(await ShipmentMovementsAsync(warehouse, second));
        var detail = await ReadSalesOrderAsync(warehouse, second);
        Assert.Equal("Created", detail.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("shipmentNote").ValueKind);
        Assert.Single(detail.GetProperty("history").EnumerateArray());
        Assert.Equal(401m, await WarehouseAsync(warehouse, example.Shredded.Id));
        Assert.Equal(80m, await WarehouseAsync(warehouse, example.Grated.Id));
    }

    /// <summary>
    /// AC-26.3, design 6.1 and 7.2: with two short lines (lines 1 and 3) and a covered one between them, both short lines
    /// are named, one sentence each, in line order (not item id order), joined with a space, with the figures formatted as
    /// design 6.1 ("1,234.5"). The covered line holds exactly its quantity, so it is not named (AC-26.4). Nothing moves.
    /// </summary>
    [Fact]
    public async Task Two_short_lines_are_both_named_in_line_order()
    {
        var scene = await SceneAsync();

        // Created first, so it has the lower id, but it is the sales order's last line.
        var lastLine = await RawMaterialAsync(scene.Manager, 2m);
        var firstLine = await RawMaterialAsync(scene.Manager, 1234.5m);
        var covered = await ProductAsync(scene.Manager, 100m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id,
            (firstLine.Id, 2000.25m), (covered.Id, 100m), (lastLine.Id, 3m));

        var response = await ShipAsync(scene.Warehouse, number);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {firstLine.Name} in Warehouse has 1,234.5 {firstLine.UnitName}; sales order #{number} needs 2,000.25 {firstLine.UnitName}. " +
            $"Not enough stock: {lastLine.Name} in Warehouse has 2 {lastLine.UnitName}; sales order #{number} needs 3 {lastLine.UnitName}.");
        AssertNoErrorsField(body);
        Assert.Empty(await ShipmentMovementsAsync(scene.Warehouse, number));
        Assert.Equal("Created", await StatusAsync(scene.Warehouse, number));
        Assert.Equal(1234.5m, await WarehouseAsync(scene.Warehouse, firstLine.Id));
        Assert.Equal(100m, await WarehouseAsync(scene.Warehouse, covered.Id));
        Assert.Equal(2m, await WarehouseAsync(scene.Warehouse, lastLine.Id));
    }

    /// <summary>
    /// AC-26.4 (PRD example, continuing AC-26.3), design 3.14 step 2: a production order for Shredded cheese 1 kg bag is
    /// completed with 200 Bag produced (through the real send and complete endpoints), so the Warehouse holds 601 Bag.
    /// Sales order #2 now ships, leaving 101 Bag of Shredded cheese and 30 Bag of Grated cheese. Sales order #3 for
    /// exactly 101 Bag then ships and leaves 0: stock equal to the quantity is enough.
    /// </summary>
    [Fact]
    public async Task Shortfall_resolved_by_production_then_stock_equal_to_the_quantity_is_enough()
    {
        await using var app = new TestApiFactory();
        var example = await CheeseBagsAsync(app);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var first = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id, (example.Shredded.Id, 600m));
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, first)).StatusCode);
        var second = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id,
            (example.Shredded.Id, 500m), (example.Grated.Id, 50m));
        Assert.Equal(HttpStatusCode.Conflict, (await ShipAsync(warehouse, second)).StatusCode);

        // Design 3.14 step 2: production makes 200 more bags (1 kg of Cheese per bag, all of it used).
        await OrderFlow.ReceiveAsync(example.Manager, example.Cheese.Id, 200m);
        var order = await OrderFlow.CreateOrderAsync(example.Manager, example.Shredded.Id, 200m);
        Assert.Equal(HttpStatusCode.OK, (await OrderFlow.SendAsync(warehouse, order, (example.Cheese.Id, 200m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await OrderFlow.CompleteAsync(example.Manager, order, 200m, null, (example.Cheese.Id, 0m))).StatusCode);
        Assert.Equal(601m, await WarehouseAsync(warehouse, example.Shredded.Id));

        var shipSecond = await ShipAsync(warehouse, second);

        Assert.Equal(HttpStatusCode.OK, shipSecond.StatusCode);
        Assert.Equal(101m, await WarehouseAsync(warehouse, example.Shredded.Id));
        Assert.Equal(30m, await WarehouseAsync(warehouse, example.Grated.Id));
        Assert.Equal(2, (await ShipmentMovementsAsync(warehouse, second)).Count);

        var third = await CreateSalesOrderAsync(example.Seller, example.DeliNorte.Id, (example.Shredded.Id, 101m));
        Assert.Equal(3, third);

        var shipThird = await ShipAsync(warehouse, third);

        Assert.Equal(HttpStatusCode.OK, shipThird.StatusCode);
        Assert.Equal(0m, await WarehouseAsync(warehouse, example.Shredded.Id));
    }

    // ---- AC-26.5, AC-26.6, AC-26.11, AC-26.12: what a shipment does not wait for or protect -----------------------------

    /// <summary>
    /// AC-26.5: sales orders ship in any order. With 10 in the Warehouse and two Created sales orders of 10 each, the
    /// newer one ships first (<c>200</c>, Warehouse 0); the older one has no priority, stays Created, and its own ship
    /// then gets <c>409</c> naming it.
    /// </summary>
    [Fact]
    public async Task Newer_sales_order_ships_before_an_older_one()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 10m);
        var older = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 10m));
        var newer = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 10m));

        var response = await ShipAsync(scene.Warehouse, newer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0m, await WarehouseAsync(scene.Warehouse, item.Id));
        Assert.Equal("Created", await StatusAsync(scene.Warehouse, older));
        await ApiAssert.ProblemAsync(await ShipAsync(scene.Warehouse, older), HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {item.Name} in Warehouse has 0 {item.UnitName}; sales order #{older} needs 10 {item.UnitName}.");
    }

    /// <summary>
    /// AC-26.6, D-3, D-4, D-11: Cheese is a material of the InProduction order (30 kg committed, applied from 50 kg of
    /// leftover, so 20 kg stays in production storage, AC-15.4), a 5 kg return is Pending, and 500 kg is in the
    /// Warehouse. Shipping 200 kg of Cheese succeeds. <c>GET /stock/{itemId}</c> changes only <c>warehouse</c> (500 to
    /// 300): production storage, committed material and its order number, pending returns and free leftover are as
    /// before. The InProduction order's detail and a Created order's estimate are unchanged too.
    /// <para>
    /// The Pending return is written straight to the database (<c>OrderFlow.InsertPendingReturnAsync</c>), because the
    /// API can never produce it next to an InProduction order: a send is refused while a return of its material is
    /// Pending (AC-15.11), and a return is refused once the order is InProduction (AC-22.3). The test uses this
    /// unreachable state on purpose: with a non-zero pending return, "pending returns and free leftover are unchanged"
    /// is a real check instead of comparing zeros. The reachable Pending-return case is the AC-26.11 test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Shipping_a_material_of_the_running_order_changes_only_the_Warehouse()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await OrderFlow.SignedInAsync(app, Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await OrderFlow.ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await OrderFlow.PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        var running = await OrderFlow.CreateOrderAsync(manager, product.Id, 30m);
        var waiting = await OrderFlow.CreateOrderAsync(manager, product.Id, 100m);
        Assert.Equal(HttpStatusCode.OK, (await OrderFlow.SendAsync(warehouse, running, (cheese.Id, 0m))).StatusCode);
        await OrderFlow.InsertPendingReturnAsync(app, cheese.Id, 5m, managerUser.Id);
        await OrderFlow.ReceiveAsync(manager, cheese.Id, 500m);

        var stockBefore = await OrderFlow.StockAsync(manager, cheese.Id);
        var runningBefore = (await OrderFlow.ReadOrderAsync(manager, running)).GetRawText();
        var waitingBefore = (await OrderFlow.ReadOrderAsync(manager, waiting)).GetRawText();
        Assert.Equal(running, stockBefore.GetProperty("committedOrderNumber").GetInt32());
        Assert.Equal(15m, stockBefore.GetProperty("freeLeftover").GetDecimal()); // 50 − 30 committed − 5 pending
        var customer = await CreateCustomerAsync(seller);
        var number = await CreateSalesOrderAsync(seller, customer.Id, (cheese.Id, 200m));

        var response = await ShipAsync(warehouse, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stockAfter = await OrderFlow.StockAsync(manager, cheese.Id);
        Assert.Equal(300m, stockAfter.GetProperty("warehouse").GetDecimal());
        foreach (var field in stockBefore.EnumerateObject().Where(field => field.Name != "warehouse"))
        {
            Assert.True(field.Value.GetRawText() == stockAfter.GetProperty(field.Name).GetRawText(),
                $"{field.Name} changed from {field.Value.GetRawText()} to {stockAfter.GetProperty(field.Name).GetRawText()}.");
        }

        Assert.Equal(runningBefore, (await OrderFlow.ReadOrderAsync(manager, running)).GetRawText());
        Assert.Equal(waitingBefore, (await OrderFlow.ReadOrderAsync(manager, waiting)).GetRawText());
    }

    /// <summary>
    /// AC-26.11 (PRD example), D-11, decision #149: Cheese has 100 kg in the Warehouse and 50 kg of free leftover in
    /// production storage, with no order InProduction. Sales order #8 (Pizzeria Lago, 120 kg) is refused with exactly
    /// "Not enough stock: Cheese in Warehouse has 100 kg; sales order #8 needs 120 kg." (the 50 kg is not counted). It is
    /// refused the same way while a 20 kg return is Pending. Once the Warehouse records receiving the return (Warehouse
    /// 120, production storage 30), it ships: Warehouse 0, production storage 30, free leftover 30.
    /// </summary>
    [Fact]
    public async Task Raw_material_ships_from_the_Warehouse_only_and_a_pending_return_does_not_count()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var filler = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await OrderFlow.ReceiveAsync(manager, cheese.Id, 100m);
        await ScenarioApi.AdjustAsync(manager, cheese.Id, "ProductionStorage", 50m, "Leftover from an earlier run");
        var pizzeria = await CreateCustomerAsync(seller, "Pizzeria Lago");
        for (var i = 1; i <= 7; i++)
        {
            await CreateSalesOrderAsync(seller, pizzeria.Id, (filler.Id, 1m)); // so the PRD's sales order is #8
        }

        var number = await CreateSalesOrderAsync(seller, pizzeria.Id, (cheese.Id, 120m));
        Assert.Equal(8, number);
        const string Short = "Not enough stock: Cheese in Warehouse has 100 kg; sales order #8 needs 120 kg.";

        await ApiAssert.ProblemAsync(await ShipAsync(warehouse, number), HttpStatusCode.Conflict, "insufficient-stock", Short);

        var pendingReturn = await OrderFlow.InstructedReturnAsync(manager, cheese.Id, 20m);
        await ApiAssert.ProblemAsync(await ShipAsync(warehouse, number), HttpStatusCode.Conflict, "insufficient-stock", Short);

        Assert.Equal(HttpStatusCode.OK, (await OrderFlow.ReceiveReturnAsync(warehouse, pendingReturn)).StatusCode);
        await AssertStockAsync(warehouse, cheese.Id, warehouse: 120m, productionStorage: 30m);

        var response = await ShipAsync(warehouse, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stock = await OrderFlow.StockAsync(warehouse, cheese.Id);
        Assert.Equal(0m, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(30m, stock.GetProperty("freeLeftover").GetDecimal());
        Assert.Equal(0m, stock.GetProperty("pendingReturns").GetDecimal());
    }

    /// <summary>
    /// AC-26.12 (PRD example), decision #149: Cheese has 1,000 kg in the Warehouse and a Created production order plans
    /// 1,000 kg with no leftover. A sales order for 100 kg ships (no priority for production) and leaves 900 kg. The
    /// order's estimate is unchanged (0 leftover, 980 to 1,020 kg), and its send of 1,000 kg is then refused with
    /// <c>409</c> <c>insufficient-stock</c> "Not enough stock: Cheese in Warehouse has 900 kg." (AC-15.7).
    /// </summary>
    [Fact]
    public async Task Shipping_may_take_material_a_created_production_order_needs()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await OrderFlow.ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await OrderFlow.ReceiveAsync(manager, cheese.Id, 1000m);
        var order = await OrderFlow.CreateOrderAsync(manager, product.Id, 1000m);
        var before = await OrderFlow.ReadOrderAsync(manager, order);
        OrderFlow.AssertEstimate(OrderFlow.Line(before, cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);
        var customer = await CreateCustomerAsync(seller);
        var number = await CreateSalesOrderAsync(seller, customer.Id, (cheese.Id, 100m));

        var response = await ShipAsync(warehouse, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(900m, await WarehouseAsync(warehouse, cheese.Id));
        Assert.Equal(before.GetRawText(), (await OrderFlow.ReadOrderAsync(manager, order)).GetRawText());
        await ApiAssert.ProblemAsync(await OrderFlow.SendAsync(warehouse, order, (cheese.Id, 1000m)),
            HttpStatusCode.Conflict, "insufficient-stock", "Not enough stock: Cheese in Warehouse has 900 kg.");
    }

    /// <summary>
    /// AC-26.13, AC-29.4, decision #148: a sales order whose customer was deactivated after it was created still ships
    /// (<c>200</c>), and the detail shows the customer as inactive.
    /// </summary>
    [Fact]
    public async Task Sales_order_of_a_deactivated_customer_still_ships()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var deactivate = await scene.Seller.PostAsync($"{CustomersPath}/{scene.Customer.Id}/deactivate", content: null, Cancel);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await ShipAsync(scene.Warehouse, number);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Shipped", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("customer").GetProperty("isActive").GetBoolean());
        Assert.Equal(0m, await WarehouseAsync(scene.Warehouse, item.Id));
    }

    // ---- AC-26.7, AC-27.3: status ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-26.7, design 7.2: shipping a sales order that has already shipped gives <c>409</c> <c>already-shipped</c> "This
    /// sales order has already been shipped.". It still has exactly one movement per line, its detail is as the first
    /// ship left it, and the stock did not move again.
    /// </summary>
    [Fact]
    public async Task Shipped_sales_order_cannot_ship_again()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 100m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 40m));
        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(scene.Warehouse, number, "First")).StatusCode);
        var shipped = (await ReadSalesOrderAsync(scene.Warehouse, number)).GetRawText();

        var response = await ShipAsync(scene.Manager, number, "Second");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.AlreadyShipped);
        Assert.Single(await ShipmentMovementsAsync(scene.Warehouse, number));
        Assert.Equal(shipped, (await ReadSalesOrderAsync(scene.Warehouse, number)).GetRawText());
        Assert.Equal(60m, await WarehouseAsync(scene.Warehouse, item.Id));
    }

    /// <summary>
    /// AC-26.7, AC-27.3, design 7.2: a Cancelled sales order never ships: <c>409</c> <c>sales-order-cancelled</c> "This
    /// sales order is cancelled.", both when the Warehouse could cover it and when it could not (the status is checked
    /// before the stock, architecture 5.1). No movement, the status stays Cancelled, and the stock is unchanged. (The
    /// sales order is stored Cancelled directly until the cancel endpoint exists, T-028.)
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public async Task Cancelled_sales_order_never_ships(int stock)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, stock);
        var number = await InsertSalesOrderAsync(factory, scene.Customer.Id, item.Id, 10m, SalesOrderStatus.Cancelled, scene.SellerUser.Id);

        var response = await ShipAsync(scene.Warehouse, number);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "sales-order-cancelled", ExpectedMessages.ShipCancelled);
        Assert.Empty(await ShipmentMovementsAsync(scene.Warehouse, number));
        Assert.Equal("Cancelled", await StatusAsync(scene.Warehouse, number));
        Assert.Equal(stock, await WarehouseAsync(scene.Warehouse, item.Id));
    }

    /// <summary>AC-25.3, design 7.2: an unknown number is <c>404</c> "Sales order #999999 not found.", for both roles that may ship.</summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Manager)]
    public async Task Unknown_number_is_404(string role)
    {
        var client = await factory.CreateClientAsAsync(role);

        var response = await ShipAsync(client, NoSuchRecord, "Delivery note 1");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"Sales order #{NoSuchRecord} not found.");
    }

    // ---- AC-26.8: the note -------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-26.8, design 2.6 and 7.2: a note of 501 characters is <c>400</c> "Note must be 500 characters or fewer." under
    /// <c>note</c>, and nothing ships. Counted in code points after trimming (decision #116), so 501 emoji are refused too.
    /// </summary>
    [Theory]
    [InlineData("n")]
    [InlineData(Emoji)]
    public async Task Note_over_500_characters_is_400(string character)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        var response = await ShipAsync(scene.Warehouse, number, string.Concat(Enumerable.Repeat(character, 501)));

        await ApiAssert.ValidationAsync(response, "note", NoteTooLong);
        Assert.Equal("Created", await StatusAsync(scene.Warehouse, number));
        Assert.Empty(await ShipmentMovementsAsync(scene.Warehouse, number));
        Assert.Equal(5m, await WarehouseAsync(scene.Warehouse, item.Id));
    }

    /// <summary>
    /// AC-26.8, design 2.6, decision #116: a note of exactly 500 characters, sent with spaces around it, ships and is
    /// stored trimmed, on the sales order and as the Shipped history entry's note. 500 emoji are 1,000 UTF-16 units but
    /// 500 characters, so they are accepted too.
    /// </summary>
    [Theory]
    [InlineData("n")]
    [InlineData(Emoji)]
    public async Task Note_of_500_characters_is_saved_trimmed(string character)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var note = string.Concat(Enumerable.Repeat(character, 500));

        var response = await ShipAsync(scene.Warehouse, number, $"  {note}  ");
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(note, body.GetProperty("shipmentNote").GetString());
        Assert.Equal(note, body.GetProperty("history")[1].GetProperty("note").GetString());
        Assert.Equal(note, (await StoredSalesOrderAsync(factory, number)).ShipmentNote);
    }

    /// <summary>
    /// AC-26.8, design 2.2 (the body before the state), architecture 5.1: a 501-character note on a sales order that is
    /// Shipped or Cancelled is <c>400</c> under <c>note</c>, not the status <c>409</c>.
    /// </summary>
    [Theory]
    [InlineData(SalesOrderStatus.Shipped)]
    [InlineData(SalesOrderStatus.Cancelled)]
    public async Task Long_note_is_400_before_the_status_409(SalesOrderStatus status)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await InsertSalesOrderAsync(factory, scene.Customer.Id, item.Id, 5m, status, scene.SellerUser.Id);

        var response = await ShipAsync(scene.Warehouse, number, new string('n', 501));

        await ApiAssert.ValidationAsync(response, "note", NoteTooLong);
    }

    /// <summary>
    /// Design 2.2 (the record in the URL before the body), architecture 5.3 step 1: an unknown number with a 501-character
    /// note is <c>404</c>, not <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Unknown_number_is_404_before_the_note_is_checked()
    {
        var scene = await SceneAsync();

        var response = await ShipAsync(scene.Warehouse, NoSuchRecord, new string('n', 501));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"Sales order #{NoSuchRecord} not found.");
    }

    /// <summary>
    /// AC-26.8, design 5.12 and 2.6, architecture 4.1 item 15: no body at all, <c>{}</c>, a <c>null</c> note, an empty
    /// note and an all-spaces note each ship (<c>200</c>) with <c>shipmentNote: null</c>, a Shipped history entry whose
    /// note is <c>null</c>, and no note stored.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{ "note": null }""")]
    [InlineData("""{ "note": "" }""")]
    [InlineData("""{ "note": "     " }""")]
    public async Task Missing_empty_or_blank_note_ships_without_a_note(string? json)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        var response = await scene.Warehouse.PostAsync(
            $"{SalesOrdersPath}/{number}/ship", json is null ? null : ApiJson.Content(json), Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Shipped", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipmentNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("history")[1].GetProperty("note").ValueKind);
        Assert.Null((await StoredSalesOrderAsync(factory, number)).ShipmentNote);
        Assert.Single(await ShipmentMovementsAsync(scene.Warehouse, number));
    }

    // ---- AC-26.9: roles ----------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-26.9 (the PRD's three examples), design 7.1: a user holding neither Warehouse nor Manager gets <c>403</c>
    /// "Your roles do not allow this action. It needs the Warehouse or Manager role.". Nothing ships and no stock moves.
    /// </summary>
    [Theory]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Seller)]
    [InlineData("Production,Seller")]
    public async Task Users_without_Warehouse_or_Manager_cannot_ship(string roles)
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));
        var client = await factory.CreateClientAsAsync(roles.Split(','));

        var response = await ShipAsync(client, number);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        Assert.Equal("Created", await StatusAsync(scene.Warehouse, number));
        Assert.Equal(5m, await WarehouseAsync(scene.Warehouse, item.Id));
    }

    /// <summary>
    /// Design 2.2 (the role check comes first): a Seller-only user shipping a number that does not exist, with a note that
    /// is too long, gets <c>403</c>, not <c>404</c> or <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Refused_role_gets_403_before_404_and_400()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await ShipAsync(seller, NoSuchRecord, new string('n', 501));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
    }

    /// <summary>AC-01.5: the ship answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Fact]
    public async Task Ship_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsync($"{SalesOrdersPath}/1/ship", ApiJson.Content("""{ "note": "x" }"""), Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- AC-26.10, first bullet: two shipments of one sales order -------------------------------------------------------

    /// <summary>
    /// AC-26.10 first bullet, architecture 5.2: two shipments of the same two-line sales order, one by a Warehouse user
    /// and one by a Manager, with each ordering forced by <see cref="ForcedOrder"/> (held at the first ship's first read of
    /// <c>Movements</c>, its stock check). The first gets <c>200</c> and is recorded as the shipper; the second, which
    /// waited for the write lock, gets <c>409</c> <c>already-shipped</c>. Exactly one movement per line exists, and each
    /// item's stock went down once.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Manager)]
    public async Task Two_shipments_of_one_sales_order_exactly_one_succeeds(string first)
    {
        await using var race = new ForcedOrder("Movements");
        var (warehouse, warehouseUser) = await OrderFlow.SignedInAsync(race.App, Roles.Warehouse);
        var (manager, managerUser) = await OrderFlow.SignedInAsync(race.App, Roles.Manager);
        var seller = await race.App.CreateClientAsAsync(Roles.Seller);
        var cheese = await RawMaterialAsync(manager, 100m);
        var starch = await RawMaterialAsync(manager, 50m);
        var customer = await CreateCustomerAsync(seller);
        var number = await CreateSalesOrderAsync(seller, customer.Id, (cheese.Id, 60m), (starch.Id, 20m));
        var (firstClient, firstUser, secondClient) = first == Roles.Warehouse
            ? (warehouse, warehouseUser, manager)
            : (manager, managerUser, warehouse);

        var (winner, loser) = await race.RunAsync(() => ShipAsync(firstClient, number), () => ShipAsync(secondClient, number));

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.AlreadyShipped);
        var movements = await ShipmentMovementsAsync(manager, number);
        Assert.Equal([cheese.Id, starch.Id], movements.Select(movement => movement.GetProperty("itemId").GetInt32()));
        Assert.Equal(firstUser.Id, (await StoredSalesOrderAsync(race.App, number)).ShippedById);
        Assert.Equal(40m, await WarehouseAsync(manager, cheese.Id));
        Assert.Equal(30m, await WarehouseAsync(manager, starch.Id));
    }

    /// <summary>
    /// AC-26.10 first bullet, PRD section 5: the two shipments of one sales order really arrive at the same moment, three
    /// rounds, each with its own sales order. Whichever wins, exactly one is <c>200</c>, the other is <c>409</c>
    /// <c>already-shipped</c>, and there is exactly one movement per line.
    /// </summary>
    [Fact]
    public async Task Simultaneous_shipments_of_one_sales_order_never_both_succeed()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 1000m);
        var other = await RawMaterialAsync(scene.Manager, 1000m);

        for (var round = 0; round < 3; round++)
        {
            var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 10m), (other.Id, 10m));

            var shipA = ShipAsync(scene.Warehouse, number);
            var shipB = ShipAsync(scene.Manager, number);
            await Task.WhenAll(shipA, shipB);
            var (a, b) = (await shipA, await shipB);

            Assert.True((a.StatusCode == HttpStatusCode.OK) != (b.StatusCode == HttpStatusCode.OK),
                $"Round {round}: {(int)a.StatusCode} and {(int)b.StatusCode}; exactly one must succeed.");
            await ApiAssert.ProblemAsync(a.StatusCode == HttpStatusCode.OK ? b : a,
                HttpStatusCode.Conflict, "already-shipped", ExpectedMessages.AlreadyShipped);
            Assert.Equal(2, (await ShipmentMovementsAsync(scene.Warehouse, number)).Count);
        }

        Assert.Equal(970m, await WarehouseAsync(scene.Warehouse, item.Id));
        Assert.Equal(970m, await WarehouseAsync(scene.Warehouse, other.Id));
    }

    // ---- AC-26.10, second bullet: two shipments wanting the same stock --------------------------------------------------

    /// <summary>
    /// AC-26.10 second bullet (PRD example), architecture 5.2: the Warehouse holds 1,000 Bag; sales orders #5 and #6 want
    /// 600 Bag each. With each ordering forced (#5 first, then #6 first), the first ships (<c>200</c>) and the second, which
    /// waited for the write lock, gets <c>409</c> <c>insufficient-stock</c> naming itself: "Not enough stock: Shredded
    /// cheese 1 kg bag in Warehouse has 400 Bag; sales order #{n} needs 600 Bag.". The Warehouse holds 400, and the loser
    /// is still Created with no movement.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Two_shipments_wanting_the_same_stock_exactly_one_succeeds(int first)
    {
        await using var race = new ForcedOrder("Movements");
        var (bags, five, six, warehouse, manager) = await FiveAndSixAsync(race.App);
        var second = first == five ? six : five;

        var (winner, loser) = await race.RunAsync(() => ShipAsync(warehouse, first), () => ShipAsync(manager, second));

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {ShreddedName} in Warehouse has 400 Bag; sales order #{second} needs 600 Bag.");
        Assert.Equal(400m, await WarehouseAsync(manager, bags.Id));
        Assert.Single(await ShipmentMovementsAsync(manager, first));
        Assert.Empty(await ShipmentMovementsAsync(manager, second));
        Assert.Equal("Created", await StatusAsync(manager, second));
    }

    /// <summary>
    /// AC-26.10 second bullet, PRD section 5: the shipments of #5 and #6 (600 Bag each, 1,000 in the Warehouse) really
    /// arrive at the same moment. Exactly one is <c>200</c>; the other is <c>409</c> <c>insufficient-stock</c> naming its
    /// own number and the 400 Bag left; the Warehouse holds 400 and never goes below zero.
    /// </summary>
    [Fact]
    public async Task Simultaneous_shipments_wanting_the_same_stock_never_both_succeed()
    {
        await using var app = new TestApiFactory();
        var (bags, five, six, warehouse, manager) = await FiveAndSixAsync(app);

        var shipFive = ShipAsync(warehouse, five);
        var shipSix = ShipAsync(manager, six);
        await Task.WhenAll(shipFive, shipSix);
        var (fiveResponse, sixResponse) = (await shipFive, await shipSix);

        Assert.True((fiveResponse.StatusCode == HttpStatusCode.OK) != (sixResponse.StatusCode == HttpStatusCode.OK),
            $"#5 {(int)fiveResponse.StatusCode}, #6 {(int)sixResponse.StatusCode}; exactly one must succeed.");
        var (lost, lostNumber) = fiveResponse.StatusCode == HttpStatusCode.OK ? (sixResponse, six) : (fiveResponse, five);
        await ApiAssert.ProblemAsync(lost, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {ShreddedName} in Warehouse has 400 Bag; sales order #{lostNumber} needs 600 Bag.");
        Assert.Equal(400m, await WarehouseAsync(manager, bags.Id));
    }

    // ---- The ship's queries (T-031 review fix) -----------------------------------------------------------------------

    /// <summary>
    /// AC-26.1, PRD section 5 (performance), T-031 review: the ship reads the sales order's lines (to check stock, and
    /// again for the response) through <c>IX_SalesOrderLines_SalesOrderNumber_ItemId</c>, and no query it runs reads the
    /// whole <c>SalesOrderLines</c> table. That matters most here: the ship holds SQLite's single write lock while it
    /// reads, so a slow read delays every other change. The guard checks SQLite's query plan for each query the request
    /// really ran (see <see cref="QueryPlanRecorder"/>, and <c>SalesOrdersTests</c> for why a plan, not the SQL text or a
    /// timing).
    /// </summary>
    [Fact]
    public async Task Ship_finds_the_lines_through_the_index_without_reading_every_line()
    {
        var recorder = new QueryPlanRecorder();
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var product = await ProductAsync(manager, warehouse: 50m);
        var material = await RawMaterialAsync(manager, warehouse: 50m);
        var customer = await CreateCustomerAsync(seller);
        await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 1m));
        var number = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 5m), (material.Id, 2.5m));

        recorder.Arm();
        var shipped = await ShipAsync(warehouse, number);

        Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
        var plan = await recorder.AssertSalesOrderLinesNeverScannedAsync(app, Cancel);

        // The ship's own load and the response's detail: both look the lines up by sales order number.
        Assert.True(plan.Count(step => step.Contains("INDEX IX_SalesOrderLines_SalesOrderNumber_ItemId (SalesOrderNumber=?)",
            StringComparison.Ordinal)) >= 2, string.Join("\n", plan));
    }

    // ---- Architecture 5.3 step 8: logging ------------------------------------------------------------------------------

    /// <summary>
    /// Architecture 5.3 step 8, section 6 Logging, decision #163: a shipment is logged once, after the commit, as "Sales
    /// order {n} shipped by user {userId}." at Information. A refused second ship of the same sales order (<c>409</c>)
    /// adds no such line.
    /// </summary>
    [Fact]
    public async Task Shipment_is_logged_once_with_number_and_user()
    {
        var scene = await SceneAsync();
        var item = await ProductAsync(scene.Manager, 5m);
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (item.Id, 5m));

        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(scene.Warehouse, number)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ShipAsync(scene.Warehouse, number)).StatusCode);

        var shipped = factory.Logs.Entries
            .Where(entry => entry.Message.StartsWith($"Sales order {number} shipped", StringComparison.Ordinal))
            .ToList();
        var entry = Assert.Single(shipped);
        Assert.Equal($"Sales order {number} shipped by user {scene.WarehouseUser.Id}.", entry.Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The records most tests need, new for each test so tests never share them: a Manager, a Warehouse user and a
    /// Seller (with their user records), and an active customer. In the shared database unless <paramref name="app"/>
    /// is given.
    /// </summary>
    private async Task<Scene> SceneAsync(TestApiFactory? app = null)
    {
        app ??= factory;
        var (manager, managerUser) = await OrderFlow.SignedInAsync(app, Roles.Manager);
        var (warehouse, warehouseUser) = await OrderFlow.SignedInAsync(app, Roles.Warehouse);
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(app, Roles.Seller);
        var customer = await CreateCustomerAsync(seller);
        return new Scene(manager, managerUser, warehouse, warehouseUser, seller, sellerUser, customer);
    }

    /// <summary>
    /// The AC-26.2 starting point, in an app of its own (the names are exact): units "Bag" and "kg", the raw material
    /// "Cheese", the finished products "Shredded cheese 1 kg bag" (recipe: 1 kg of Cheese per bag, for AC-26.4) and
    /// "Grated cheese 200 g bag", 1,001 and 80 Bag of them in the Warehouse, and the customer "Deli Norte".
    /// </summary>
    private static async Task<CheeseBags> CheeseBagsAsync(TestApiFactory app)
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(app, Roles.Seller);
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var shredded = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: ShreddedName);
        var grated = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: GratedName);
        await OrderFlow.PutRecipeAsync(manager, shredded.Id, (cheese.Id, 1m));
        await ScenarioApi.AdjustAsync(manager, shredded.Id, "Warehouse", 1001m, OpeningCount);
        await ScenarioApi.AdjustAsync(manager, grated.Id, "Warehouse", 80m, OpeningCount);
        var deliNorte = await CreateCustomerAsync(seller, "Deli Norte");
        return new CheeseBags(manager, seller, sellerUser, cheese, shredded, grated, deliNorte);
    }

    /// <summary>
    /// The AC-26.10 second bullet starting point, in <paramref name="app"/> (its own database, so the numbers are the
    /// PRD's): 1,000 Bag of "Shredded cheese 1 kg bag" in the Warehouse, four unrelated sales orders, then #5 and #6 for
    /// 600 Bag each. Returns the product, the two numbers, and a Warehouse and a Manager client.
    /// </summary>
    private static async Task<(CreatedItem Bags, int Five, int Six, HttpClient Warehouse, HttpClient Manager)> FiveAndSixAsync(
        TestApiFactory app)
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var bags = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: ShreddedName);
        var filler = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await ScenarioApi.AdjustAsync(manager, bags.Id, "Warehouse", 1000m, OpeningCount);
        var customer = await CreateCustomerAsync(seller);
        for (var i = 1; i <= 4; i++)
        {
            await CreateSalesOrderAsync(seller, customer.Id, (filler.Id, 1m));
        }

        var five = await CreateSalesOrderAsync(seller, customer.Id, (bags.Id, 600m));
        var six = await CreateSalesOrderAsync(seller, customer.Id, (bags.Id, 600m));
        Assert.Equal((5, 6), (five, six));
        return (bags, five, six, warehouse, manager);
    }

    /// <summary>The sales order's current status through <c>GET /sales-orders/{number}</c>.</summary>
    private static async Task<string?> StatusAsync(HttpClient client, int number) =>
        (await ReadSalesOrderAsync(client, number)).GetProperty("status").GetString();

    /// <summary>
    /// Design 7.2: the shipment's <c>409</c> names the short lines in <c>detail</c> only. An <c>errors</c> field is for
    /// field problems of a <c>400</c> (design 6).
    /// </summary>
    private static void AssertNoErrorsField(JsonElement body) =>
        Assert.False(body.TryGetProperty("errors", out _), "The insufficient-stock 409 must have no errors field.");

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
        HttpClient Manager, AppUser ManagerUser, HttpClient Warehouse, AppUser WarehouseUser,
        HttpClient Seller, AppUser SellerUser, CreatedCustomer Customer);

    /// <summary>What <see cref="CheeseBagsAsync"/> set up.</summary>
    private sealed record CheeseBags(
        HttpClient Manager, HttpClient Seller, AppUser SellerUser, CreatedItem Cheese, CreatedItem Shredded,
        CreatedItem Grated, CreatedCustomer DeliNorte);
}
