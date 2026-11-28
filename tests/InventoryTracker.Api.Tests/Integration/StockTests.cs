using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-008: current stock (<c>GET /api/v1/stock</c>, <c>GET /api/v1/stock/{itemId}</c>, US-10, design 5.5) and the
/// <see cref="StockLedger"/>, the only writer of movements (D-2, architecture 5.1 and 5.2).
/// </summary>
/// <remarks>
/// <para>
/// Movements of every type, orders and returns are inserted directly through the DbContext, because the endpoints that
/// create them come in later tasks. Tests in this class share one database and each uses its own items. Tests that need
/// an InProduction order (the database allows only one) or an exact list create their own factory.
/// </para>
/// <para>
/// The ledger tests call it the way a service does: resolved from dependency injection in a scope of the running app
/// (one scope stands for one request), inside <c>InWriteTransactionAsync</c> on that scope's DbContext, through the
/// shared <see cref="OrderFlow.InTransactionAsync"/> and <see cref="OrderFlow.LedgerAsync"/> (this class had its own
/// copies until T-020). No endpoint takes stock away yet (adjustments arrive in T-015), so "refused" is
/// checked on the <see cref="ConflictException"/> itself; the exception handler that turns it into the <c>409</c> body
/// is tested in T-001.
/// </para>
/// <para>
/// (v1.1) T-026: shipment movements (<c>ShippedToCustomer</c>) link to real sales orders, created through
/// <c>POST /sales-orders</c> (T-025) with <see cref="SalesFlow"/>. No endpoint ships yet (T-027), so the shipments
/// themselves go in through the ledger, like every other movement here.
/// </para>
/// <para>
/// (v1.1) T-029: open demand (D-12, US-31) on both stock endpoints. Those tests create, ship and cancel sales orders
/// through the real endpoints (T-025, T-027, T-028), with the shared <see cref="SalesFlow"/> helpers (decision #166).
/// </para>
/// </remarks>
public class StockTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string StockPath = "/api/v1/stock";

    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A receipt date for movements the tests build by hand.</summary>
    private static readonly DateOnly Day = new(2026, 11, 1);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-10.1, AC-10.2: stock is the sum of movements ---------------------------------------------------------

    /// <summary>
    /// AC-10.2, AC-10.1, design 5.5: an item with no movements shows 0 in both locations, with its name, type and unit;
    /// a raw material also shows 0 committed with no order, 0 pending and 0 free. Every role may read it (PRD 4.2,
    /// AC-05.1). (v1.1) T-029, AC-31.1, design 5.5: exactly the eleven design fields, in the design's order, with
    /// <c>openDemand</c> right after <c>productionStorage</c> and 0 (a number, not null) when no sales order has the item;
    /// the Seller role reads it too (AC-31.4).
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Seller)]
    public async Task Item_without_movements_has_zero_stock_and_every_role_can_read_it(string role)
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var reader = await factory.CreateClientAsAsync(role);

        var response = await reader.GetAsync($"{StockPath}/{item.Id}", Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(item.Id, body.GetProperty("itemId").GetInt32());
        Assert.Equal(item.Name, body.GetProperty("itemName").GetString());
        Assert.Equal("RawMaterial", body.GetProperty("itemType").GetString());
        Assert.Equal(item.UnitName, body.GetProperty("unit").GetString());
        Assert.Equal(0m, body.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, body.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(0m, body.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("committedOrderNumber").ValueKind);
        Assert.Equal(0m, body.GetProperty("pendingReturns").GetDecimal());
        Assert.Equal(0m, body.GetProperty("freeLeftover").GetDecimal());
        Assert.Equal(0m, OpenDemand(body));
        Assert.Equal(StockFields, body.EnumerateObject().Select(field => field.Name));

        var list = await reader.GetAsync($"{StockPath}?pageSize=100", Cancel);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    /// <summary>
    /// AC-10.1, D-2, AC-11.4: each location's stock is exactly the sum of that location's changes over the item's
    /// movements of every type (Received, SentToProduction, ReturnedToWarehouse, ConsumedInProduction, Adjustment in
    /// either location), added as exact decimals (0.1 + 0.2 − 0.3 is 0). Another item's movements do not count.
    /// </summary>
    [Fact]
    public async Task Stock_is_the_sum_of_the_items_movements_in_each_location()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var other = await CreateItemAsync(ItemType.RawMaterial);
        await InsertMovementsAsync(factory,
            Change(MovementType.Received, item.Id, warehouse: 1000m),
            Change(MovementType.Received, item.Id, warehouse: 0.1m),
            Change(MovementType.Received, item.Id, warehouse: 0.2m),
            Change(MovementType.SentToProduction, item.Id, warehouse: -995m, production: 995m),
            Change(MovementType.Adjustment, item.Id, warehouse: -0.3m),
            Change(MovementType.ReturnedToWarehouse, item.Id, warehouse: 5m, production: -5m),
            Change(MovementType.ConsumedInProduction, item.Id, production: -900m),
            Change(MovementType.Adjustment, item.Id, production: -20m),
            Change(MovementType.Received, other.Id, warehouse: 777m));
        var reader = await factory.CreateClientAsAsync(Roles.Warehouse);

        var body = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{item.Id}", Cancel));

        // Warehouse: 1000 + 0.1 + 0.2 − 995 − 0.3 + 5 = 10. Production storage: 995 − 5 − 900 − 20 = 70.
        Assert.Equal(10m, body.GetProperty("warehouse").GetDecimal());
        Assert.Equal(70m, body.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(70m, body.GetProperty("freeLeftover").GetDecimal());
    }

    /// <summary>
    /// Design 5.5: a finished product shows its Warehouse stock (from ProducedOutput) and <c>null</c> for committed,
    /// committed order, pending returns and free leftover, which are about raw materials only (AC-10.3). (v1.1) T-029,
    /// AC-31.1: open demand is not one of them; a finished product shows it as 0, never null, and the row has the same
    /// eleven fields in the same order as a raw material's.
    /// </summary>
    [Fact]
    public async Task Finished_product_has_null_leftover_figures()
    {
        var product = await CreateItemAsync(ItemType.FinishedProduct);
        await InsertMovementsAsync(factory, Change(MovementType.ProducedOutput, product.Id, warehouse: 950m));
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var body = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{product.Id}", Cancel));

        Assert.Equal("FinishedProduct", body.GetProperty("itemType").GetString());
        Assert.Equal(950m, body.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, body.GetProperty("productionStorage").GetDecimal());
        foreach (var field in new[] { "committedToOrder", "committedOrderNumber", "pendingReturns", "freeLeftover" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }

        Assert.Equal(0m, OpenDemand(body));
        Assert.Equal(StockFields, body.EnumerateObject().Select(field => field.Name));
    }

    // ---- AC-10.3: committed, pending returns, free leftover -------------------------------------------------------

    /// <summary>
    /// AC-10.3, D-3, D-4, with an InProduction order and returns inserted through the DbContext:
    /// <list type="bullet">
    /// <item>Cheese: 1,050 kg in Production storage; the InProduction order's line has leftover applied 5, warehouse
    /// send 995, complementary sends 20, so 1,020 kg committed to that order (worked example A's figures). Pending returns
    /// of 10 and 5 kg give 15; a Received and a Cancelled return do not count. Free = 1,050 − 1,020 − 15 = 15 kg. A
    /// Created order's line for Cheese commits nothing.</item>
    /// <item>Starch: 10 kg in storage, 15 kg committed: free leftover is never below zero, so 0.</item>
    /// <item>Salt, on no running order: 40 kg in storage, 0 committed with no order number, 40 free.</item>
    /// </list>
    /// The list endpoint shows the same figures as the single-item endpoint.
    /// </summary>
    [Fact]
    public async Task Raw_materials_show_committed_pending_and_free_leftover()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var user = await app.CreateUserAsync(Seed.UniqueEmail("setup"), Roles.Manager);
        await InsertMovementsAsync(app,
            Change(MovementType.Received, cheese.Id, warehouse: 2000m),
            Change(MovementType.SentToProduction, cheese.Id, warehouse: -1050m, production: 1050m),
            Change(MovementType.Received, starch.Id, warehouse: 10m),
            Change(MovementType.SentToProduction, starch.Id, warehouse: -10m, production: 10m),
            Change(MovementType.Received, salt.Id, warehouse: 40m),
            Change(MovementType.SentToProduction, salt.Id, warehouse: -40m, production: 40m));

        var runningOrder = 0;
        await InTransactionAsync(app, async (db, _, _) =>
        {
            var running = NewOrder(product.Id, user.Id, OrderStatus.InProduction);
            running.Lines.Add(SentLine(1, cheese.Id, leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 20m));
            running.Lines.Add(SentLine(2, starch.Id, leftoverApplied: 0m, warehouseSend: 15m, complementarySends: 0m));
            var waiting = NewOrder(product.Id, user.Id, OrderStatus.Created);
            waiting.Lines.Add(new ProductionOrderLine { LineNo = 1, RawMaterialId = cheese.Id, AmountPerUnit = 1m, PlannedAmount = 1000m });
            db.ProductionOrders.AddRange(running, waiting);
            await db.SaveChangesAsync(Cancel);
            runningOrder = running.Number;
        });

        // Returns in each status, each in its own write transaction (OrderFlow). Received and Cancelled ones are written
        // directly too: receiving one through the endpoint would move stock this test's figures do not include.
        await InsertPendingReturnAsync(app, cheese.Id, 10m, user.Id);
        await InsertPendingReturnAsync(app, cheese.Id, 5m, user.Id);
        await InsertReturnAsync(app, cheese.Id, 7m, user.Id, ReturnStatus.Received);
        await InsertReturnAsync(app, cheese.Id, 3m, user.Id, ReturnStatus.Cancelled);

        var reader = await app.CreateClientAsAsync(Roles.Warehouse);
        var cheeseStock = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{cheese.Id}", Cancel));
        var starchStock = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{starch.Id}", Cancel));
        var saltStock = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{salt.Id}", Cancel));

        AssertLeftoverFigures(cheeseStock, warehouse: 950m, production: 1050m, committed: 1020m, orderNumber: runningOrder, pending: 15m, free: 15m);
        AssertLeftoverFigures(starchStock, warehouse: 0m, production: 10m, committed: 15m, orderNumber: runningOrder, pending: 0m, free: 0m);
        AssertLeftoverFigures(saltStock, warehouse: 0m, production: 40m, committed: 0m, orderNumber: null, pending: 0m, free: 40m);

        var list = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}?itemType=RawMaterial", Cancel));
        var rows = list.GetProperty("items").EnumerateArray().ToDictionary(row => row.GetProperty("itemId").GetInt32(), row => row.GetRawText());
        Assert.Equal(cheeseStock.GetRawText(), rows[cheese.Id]);
        Assert.Equal(starchStock.GetRawText(), rows[starch.Id]);
        Assert.Equal(saltStock.GetRawText(), rows[salt.Id]);
    }

    // ---- AC-10.4 and lists --------------------------------------------------------------------------------------

    /// <summary>AC-10.4, design 7.2: the stock of an item that does not exist is <c>404</c> "Item {id} not found.".</summary>
    [Fact]
    public async Task Stock_of_an_unknown_item_is_404()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var response = await reader.GetAsync($"{StockPath}/999999", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    /// <summary>
    /// AC-10.1, design 2.5 and architecture 4.1 item 10: <c>GET /stock</c> lists every item, newest item first (by
    /// creation time, then id), in pages with the total count; <c>?itemType=</c> filters by type in any letter case.
    /// Each row carries the item's stock.
    /// </summary>
    [Fact]
    public async Task Stock_is_listed_newest_item_first_and_filtered_by_type()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var oldest = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var sameSecond = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit);
        timed.FakeTime!.Advance(TimeSpan.FromMinutes(1));
        var newest = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        await InsertMovementsAsync(timed, Change(MovementType.Received, oldest.Id, warehouse: 12.5m));

        var all = await ApiJson.ReadAsync(await manager.GetAsync(StockPath, Cancel));
        Assert.Equal([newest.Id, sameSecond.Id, oldest.Id], ItemIds(all));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(12.5m, all.GetProperty("items")[2].GetProperty("warehouse").GetDecimal());

        var secondPage = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?page=2&pageSize=2", Cancel));
        Assert.Equal([oldest.Id], ItemIds(secondPage));
        Assert.Equal(3, secondPage.GetProperty("totalCount").GetInt32());

        var pastTheEnd = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?page=9", Cancel));
        Assert.Empty(ItemIds(pastTheEnd));

        var raw = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?itemType=rawmaterial", Cancel));
        Assert.Equal([newest.Id, oldest.Id], ItemIds(raw));
        Assert.Equal(2, raw.GetProperty("totalCount").GetInt32());

        var finished = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?itemType=FinishedProduct", Cancel));
        Assert.Equal([sameSecond.Id], ItemIds(finished));
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("items")[0].GetProperty("freeLeftover").ValueKind);
    }

    /// <summary>
    /// Design 2.5: an unknown <c>itemType</c> (numbers are not type names) and paging outside the limits are
    /// <c>400</c>, reported together.
    /// </summary>
    [Theory]
    [InlineData("Gadget")]
    [InlineData("1")]
    public async Task Invalid_list_query_is_rejected(string itemType)
    {
        var reader = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await reader.GetAsync($"{StockPath}?itemType={itemType}&page=0&pageSize=101", Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "page must be 1 or greater. pageSize must be between 1 and 100. itemType must be RawMaterial or FinishedProduct.");
        Assert.Equal(["itemType must be RawMaterial or FinishedProduct."], ApiJson.Messages(body, "itemType"));
    }

    /// <summary>
    /// AC-01.5: both stock endpoints answer <c>401</c> <c>not-authenticated</c> without a token. (There is no
    /// <c>403</c> case: PRD 4.2 lets every role view stock, which the role theory above proves.)
    /// </summary>
    [Theory]
    [InlineData(StockPath)]
    [InlineData(StockPath + "/1")]
    public async Task Stock_without_a_token_is_401(string path)
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(path, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// The performance test of this class, in a class of its own so it can join <see cref="PerformanceCollection"/>,
    /// which runs with nothing else beside it (T-020). xUnit sets collections per class, not per test. Nested inside
    /// <see cref="StockTests"/>, it can still use that class's private helpers (<c>SeedVolumeAsync</c>,
    /// <c>TimeAsync</c>), because a nested class may see the private members of the class around it.
    /// </summary>
    [Collection(nameof(PerformanceCollection))]
    public class Performance
    {
        /// <summary>
        /// PRD section 5 performance target, architecture 5.1: with 100,000 movements over 500 items and 1,000 orders in
        /// the database, a full page of <c>GET /stock</c> (100 items) and a single <c>GET /stock/{itemId}</c> each answer
        /// well within 1 second for 95% of requests (asserted on the 19th of 20 timings). The figures are still exact.
        /// </summary>
        [Fact]
        public async Task Stock_reads_meet_the_performance_target_with_100000_movements()
        {
            await using var app = new TestApiFactory();
            var manager = await app.CreateClientAsAsync(Roles.Manager);
            var unit = await Seed.CreateUnitAsync(manager);
            var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit);
            var user = await app.CreateUserAsync(Seed.UniqueEmail("perf"), Roles.Manager);
            await SeedVolumeAsync(app, unit.Id, product.Id, user.Id);
            var reader = await app.CreateClientAsAsync(Roles.Warehouse);

            var page = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}?pageSize=100&itemType=RawMaterial", Cancel));
            var firstId = page.GetProperty("items")[0].GetProperty("itemId").GetInt32();
            Assert.Equal(500, page.GetProperty("totalCount").GetInt32());

            // 100,000 receipts of 1.5 spread evenly over 500 items: 200 each, so 300 per item.
            Assert.All(page.GetProperty("items").EnumerateArray(), row => Assert.Equal(300m, row.GetProperty("warehouse").GetDecimal()));

            // (v1.1) T-029: every read now runs the fourth query, open demand, too. No sales order exists here, so it is 0.
            // The timings with a v1.1 volume of sales orders are T-031's (architecture 7).
            Assert.All(page.GetProperty("items").EnumerateArray(), row => Assert.Equal(0m, OpenDemand(row)));

            var pageTimes = await TimeAsync(reader, $"{StockPath}?pageSize=100");
            var itemTimes = await TimeAsync(reader, $"{StockPath}/{firstId}");

            // Written to the test's output (visible in a TRX report), so a slow machine can be spotted before it fails.
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"GET /stock page of 100: p95 {pageTimes[18]} ms; GET /stock/{{id}}: p95 {itemTimes[18]} ms.");
            Assert.True(pageTimes[18] < 1000, $"GET /stock p95 was {pageTimes[18]} ms.");
            Assert.True(itemTimes[18] < 1000, $"GET /stock/{{id}} p95 was {itemTimes[18]} ms.");
        }
    }

    // ---- (v1.1) Open demand (T-029, US-31, D-12) ----------------------------------------------------------------
    //
    // Sales orders are created, shipped and cancelled through the API with the shared SalesFlow helpers (decision #166).
    // Tests that read GET /stock as a list, or that need an InProduction order, have a factory of their own, so the list
    // holds only their items.

    /// <summary>
    /// AC-31.2, step by step, through the API: 1,001 Bag in the Warehouse and two Created sales orders, of 600 (the PRD's
    /// #1) and 500 (its #4). Open demand is 1,100 and the Warehouse still 1,001 (nothing is reserved, decision #140).
    /// After the 600 ships: 500 and 401. After the 500 is cancelled: 0, and the Warehouse stays 401. The list row agrees
    /// with the single-item read at every step.
    /// </summary>
    [Fact]
    public async Task Open_demand_follows_the_sales_orders_through_ship_and_cancel()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var bags = await ProductAsync(manager, 1001m);
        var customer = await CreateCustomerAsync(manager);
        var first = await CreateSalesOrderAsync(manager, customer.Id, (bags.Id, 600m));
        var fourth = await CreateSalesOrderAsync(manager, customer.Id, (bags.Id, 500m));

        await AssertStockAndDemandAsync(manager, bags.Id, warehouse: 1001m, openDemand: 1100m);

        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, first)).StatusCode);
        await AssertStockAndDemandAsync(manager, bags.Id, warehouse: 401m, openDemand: 500m);

        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(manager, fourth)).StatusCode);
        await AssertStockAndDemandAsync(manager, bags.Id, warehouse: 401m, openDemand: 0m);
    }

    /// <summary>
    /// AC-28.4 and the design 5.5 finished-product example: two Created sales orders of 600 Bag each leave the Warehouse
    /// at 1,001 and give open demand 1,200, larger than the stock (AC-31.3 allows it). The four leftover figures stay
    /// null and production storage 0: a Created sales order changes no figure but open demand.
    /// </summary>
    [Fact]
    public async Task Two_created_sales_orders_add_up_and_leave_the_warehouse_alone()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var bags = await ProductAsync(manager, 1001m);
        var deli = await CreateCustomerAsync(manager);
        var cafe = await CreateCustomerAsync(manager);
        await CreateSalesOrderAsync(manager, deli.Id, (bags.Id, 600m));
        await CreateSalesOrderAsync(manager, cafe.Id, (bags.Id, 600m));

        var body = await OrderFlow.StockAsync(manager, bags.Id);

        Assert.Equal(1001m, body.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, body.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(1200m, OpenDemand(body));
        foreach (var field in new[] { "committedToOrder", "committedOrderNumber", "pendingReturns", "freeLeftover" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }
    }

    /// <summary>
    /// D-12, AC-31.2: only Created sales orders count. The item is on a Created sales order (2.5 and, with another item,
    /// 0.1 on a second one) and on a Shipped (700) and a Cancelled (300) one written straight to the database; open
    /// demand is exactly 2.6, summed as an exact decimal. The other item's line counts only for that item (0.25), and an
    /// item on no Created sales order shows 0.
    /// </summary>
    [Fact]
    public async Task Only_created_sales_orders_count_in_open_demand()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var user = await CreateUserAsync();
        var item = await RawMaterialAsync(manager, warehouse: 0m);
        var other = await RawMaterialAsync(manager, warehouse: 0m);
        var unsold = await RawMaterialAsync(manager, warehouse: 0m);
        var customer = await CreateCustomerAsync(manager);
        await CreateSalesOrderAsync(manager, customer.Id, (item.Id, 2.5m));
        await CreateSalesOrderAsync(manager, customer.Id, (other.Id, 0.25m), (item.Id, 0.1m));
        await InsertSalesOrderAsync(factory, customer.Id, item.Id, 700m, SalesOrderStatus.Shipped, user);
        await InsertSalesOrderAsync(factory, customer.Id, item.Id, 300m, SalesOrderStatus.Cancelled, user);
        await InsertSalesOrderAsync(factory, customer.Id, unsold.Id, 40m, SalesOrderStatus.Cancelled, user);

        Assert.Equal(2.6m, OpenDemand(await OrderFlow.StockAsync(manager, item.Id)));
        Assert.Equal(0.25m, OpenDemand(await OrderFlow.StockAsync(manager, other.Id)));
        Assert.Equal(0m, OpenDemand(await OrderFlow.StockAsync(manager, unsold.Id)));
    }

    /// <summary>
    /// AC-31.3, decision #140, with a raw material that has every AC-10.3 figure: an InProduction order commits 1,000,
    /// a Pending return holds 10, and 40 is free leftover. Open demand never blocks anything:
    /// <list type="bullet">
    /// <item>a sales order for 5,000, more than the 2,000 in the Warehouse, is created first, and the order's send of
    /// 1,000 still succeeds;</item>
    /// <item>a second sales order (3,000) changes no field of the row except <c>openDemand</c> (5,000 to 8,000): the
    /// row is compared field by field before and after;</item>
    /// <item>a complementary send of 20 and a Warehouse adjustment of −100 then succeed as before, and the Warehouse goes
    /// down by exactly those amounts while open demand stays 8,000.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Open_demand_larger_than_stock_changes_no_other_figure_and_blocks_nothing()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var (number, cheese, starch) = await TwoLineOrderAsync(manager);
        var customer = await CreateCustomerAsync(manager);
        await CreateSalesOrderAsync(manager, customer.Id, (cheese.Id, 5000m));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m))).StatusCode);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);

        // The API cannot give one material both committed material and a Pending return: a return of a material on the
        // InProduction order is refused (AC-22.3), and a send with a Pending return of its material is refused too (AC-15.11). So
        // the return is written straight to the database, as the AC-10.3 test above does.
        await InsertPendingReturnAsync(app, cheese.Id, 10m, managerUser.Id);

        // Warehouse 2,000 − 1,000. Storage 1,000 + 50. Committed 1,000. Free 1,050 − 1,000 − 10.
        var before = await OrderFlow.StockAsync(manager, cheese.Id);
        AssertLeftoverFigures(before, warehouse: 1000m, production: 1050m, committed: 1000m, orderNumber: number, pending: 10m, free: 40m);
        Assert.Equal(5000m, OpenDemand(before));

        await CreateSalesOrderAsync(manager, customer.Id, (cheese.Id, 3000m));

        var after = await OrderFlow.StockAsync(manager, cheese.Id);
        Assert.Equal(8000m, OpenDemand(after));
        Assert.Equal(FieldsExceptOpenDemand(before), FieldsExceptOpenDemand(after));

        var complementary = await ApprovedComplementaryAsync(production, manager, number, "Hopper spill", (cheese.Id, 20m));
        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(warehouse, complementary, (cheese.Id, 20m))).StatusCode);
        await Scenarios.ScenarioApi.AdjustAsync(manager, cheese.Id, "Warehouse", -100m, "Count correction");

        var end = await OrderFlow.StockAsync(manager, cheese.Id);
        Assert.Equal(1000m - 20m - 100m, end.GetProperty("warehouse").GetDecimal());
        Assert.Equal(8000m, OpenDemand(end));
    }

    /// <summary>
    /// AC-31.4, AC-10.5, decision #147: every role, Production-only and Seller-only included, reads the item's open
    /// demand (7.5) with <c>200</c>, on <c>GET /stock/{itemId}</c> and in the <c>GET /stock</c> list. There is one stock
    /// view for all, so no role gets <c>403</c> for it.
    /// </summary>
    [Theory]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Seller)]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_sees_open_demand(string role)
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var item = await ProductAsync(manager, 0m);
        await CreateSalesOrderAsync(manager, (await CreateCustomerAsync(manager)).Id, (item.Id, 7.5m));
        var reader = await app.CreateClientAsAsync(role);

        var single = await reader.GetAsync($"{StockPath}/{item.Id}", Cancel);
        var list = await reader.GetAsync(StockPath, Cancel);

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal(7.5m, OpenDemand(await ApiJson.ReadAsync(single)));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(7.5m, OpenDemand(Assert.Single((await ApiJson.ReadAsync(list)).GetProperty("items").EnumerateArray())));
    }

    /// <summary>
    /// AC-31.1, design 5.5: the <c>itemType</c> filter still works with open demand. A raw material with open demand 3
    /// and a finished product with 1,200: each filter lists only its own type, every row carries its own
    /// <c>openDemand</c> (a number for both types), and each row is exactly the single-item read.
    /// </summary>
    [Fact]
    public async Task Item_type_filter_still_works_and_every_row_has_open_demand()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var raw = await RawMaterialAsync(manager, warehouse: 10m);
        var product = await ProductAsync(manager, 1001m);
        var customer = await CreateCustomerAsync(manager);
        await CreateSalesOrderAsync(manager, customer.Id, (raw.Id, 3m), (product.Id, 600m));
        await CreateSalesOrderAsync(manager, customer.Id, (product.Id, 600m));

        var raws = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?itemType=RawMaterial", Cancel));
        var products = await ApiJson.ReadAsync(await manager.GetAsync($"{StockPath}?itemType=finishedproduct", Cancel));
        var all = await ApiJson.ReadAsync(await manager.GetAsync(StockPath, Cancel));

        var rawRow = Assert.Single(raws.GetProperty("items").EnumerateArray());
        var productRow = Assert.Single(products.GetProperty("items").EnumerateArray());
        Assert.Equal(raw.Id, rawRow.GetProperty("itemId").GetInt32());
        Assert.Equal(3m, OpenDemand(rawRow));
        Assert.Equal(product.Id, productRow.GetProperty("itemId").GetInt32());
        Assert.Equal(1200m, OpenDemand(productRow));
        Assert.Equal((await OrderFlow.StockAsync(manager, raw.Id)).GetRawText(), rawRow.GetRawText());
        Assert.Equal((await OrderFlow.StockAsync(manager, product.Id)).GetRawText(), productRow.GetRawText());
        Assert.Equal([product.Id, raw.Id], ItemIds(all));
        Assert.All(all.GetProperty("items").EnumerateArray(),
            row => Assert.Equal(StockFields, row.EnumerateObject().Select(field => field.Name)));
    }

    /// <summary>
    /// Decisions #85 and #159, architecture 3.5: open demand is read in the same snapshot as the stock beside it. The test
    /// pauses <c>GET /stock/{itemId}</c> right after its first query (the movement sums, Warehouse 1,001) and, in that
    /// gap, ships the 600 sales order through the API. The paused read must still show one moment, before the ship
    /// (Warehouse 1,001, open demand 1,100), never the mix "1,001 in the Warehouse, 500 open". The next read shows the
    /// state after the ship (401, 500).
    /// </summary>
    [Fact]
    public async Task Open_demand_reads_the_same_snapshot_as_the_stock_even_when_a_ship_commits_in_between()
    {
        Func<Task> betweenQueries = () => Task.CompletedTask;
        var pause = new AfterFirstReadInterceptor(
            command => command.CommandText.Contains("FROM \"Movements\"", StringComparison.Ordinal)
                       && command.CommandText.Contains("GROUP BY", StringComparison.Ordinal),
            () => betweenQueries());
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(pause)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var bags = await ProductAsync(manager, 1001m);
        var customer = await CreateCustomerAsync(manager);
        var shipped = await CreateSalesOrderAsync(manager, customer.Id, (bags.Id, 600m));
        await CreateSalesOrderAsync(manager, customer.Id, (bags.Id, 500m));
        var shipStatus = HttpStatusCode.InternalServerError;

        // Another request, committed while the stock read is paused between its queries. The interceptor has already
        // fired once, so the ship's own queries pass straight through it.
        betweenQueries = async () => shipStatus = (await ShipAsync(warehouse, shipped)).StatusCode;
        var reader = await app.CreateClientAsAsync(Roles.Seller);

        pause.Arm();
        var during = await OrderFlow.StockAsync(reader, bags.Id);
        var after = await OrderFlow.StockAsync(reader, bags.Id);

        Assert.True(pause.HasRun);
        Assert.Equal(HttpStatusCode.OK, shipStatus);
        Assert.Equal((1001m, 1100m), (during.GetProperty("warehouse").GetDecimal(), OpenDemand(during)));
        Assert.Equal((401m, 500m), (after.GetProperty("warehouse").GetDecimal(), OpenDemand(after)));
    }

    // ---- StockLedger: never below zero --------------------------------------------------------------------------

    /// <summary>
    /// D-2, AC-12.3 example: with 12 kg in the Warehouse, a −13 kg movement is refused with <c>409</c>
    /// <c>insufficient-stock</c> and the design 7.2 message, and the request saves nothing (another change made in the
    /// same transaction is rolled back too); −12 kg is accepted and leaves exactly 0.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_a_movement_that_takes_the_warehouse_below_zero()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));
        var sideEffect = Seed.UniqueName("rolled-back");

        var refused = await Assert.ThrowsAsync<ConflictException>(() => InTransactionAsync(factory, async (db, ledger, _) =>
        {
            // Saved inside the transaction, as a service's earlier change would be; the refusal must undo it.
            db.Units.Add(new UnitOfMeasure { Name = sideEffect, NameNormalized = NameNormalizer.Normalize(sideEffect) });
            await db.SaveChangesAsync(Cancel);
            await ledger.AddAsync(Change(MovementType.Adjustment, item.Id, warehouse: -13m, userId: user), Cancel);
        }));

        Assert.Equal("insufficient-stock", refused.Code);
        Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 12 {item.UnitName}.", refused.Detail);
        Assert.Equal(12m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));
        Assert.False(await UnitExistsAsync(sideEffect));

        await LedgerAsync(factory, Change(MovementType.Adjustment, item.Id, warehouse: -12m, userId: user));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// D-2: Production storage cannot go below zero either. With 30.5 kg sent to Production storage and nothing left in
    /// the Warehouse: consuming 30.501 kg, returning 31 kg and a −31 kg Production storage adjustment are refused with
    /// the Production storage message (design 7.2, numbers in the design 6.1 format); sending 0.001 kg more is refused
    /// for the Warehouse. Consuming exactly 30.5 kg is accepted.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_movements_that_take_production_storage_below_zero()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await LedgerAsync(factory,
            Change(MovementType.Received, item.Id, warehouse: 30.5m, userId: user),
            Change(MovementType.SentToProduction, item.Id, warehouse: -30.5m, production: 30.5m, userId: user, links: links));
        var productionShort = $"Not enough stock: {item.Name} in Production storage has 30.5 {item.UnitName}.";

        foreach (var movement in new[]
                 {
                     Change(MovementType.ConsumedInProduction, item.Id, production: -30.501m, userId: user, links: links),
                     Change(MovementType.ReturnedToWarehouse, item.Id, warehouse: 31m, production: -31m, userId: user, links: links),
                     Change(MovementType.Adjustment, item.Id, production: -31m, userId: user),
                 })
        {
            var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerAsync(factory, movement));
            Assert.Equal("insufficient-stock", refused.Code);
            Assert.Equal(productionShort, refused.Detail);
        }

        var send = await Assert.ThrowsAsync<ConflictException>(() => LedgerAsync(factory,
            Change(MovementType.SentToProduction, item.Id, warehouse: -0.001m, production: 0.001m, userId: user, links: links)));
        Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 0 {item.UnitName}.", send.Detail);
        Assert.Equal(2, await CountMovementsAsync(factory, item.Id));

        await LedgerAsync(factory, Change(MovementType.ConsumedInProduction, item.Id, production: -30.5m, userId: user, links: links));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.ProductionStorage));
    }

    /// <summary>
    /// Architecture 5.1: the ledger saves each movement as it is added, so the next check in the same request sees it.
    /// Two −8 kg movements on 12 kg in one transaction: the second is refused against the 4 kg left, and the refusal
    /// rolls back the first as well (the request saves nothing).
    /// </summary>
    [Fact]
    public async Task Ledger_counts_earlier_movements_of_the_same_request()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerAsync(factory,
            Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user),
            Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user)));

        Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 4 {item.UnitName}.", refused.Detail);
        Assert.Equal(12m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// PRD section 5, architecture 5.2: two withdrawals against the same stock at the same moment. The first holds its
    /// write transaction open after the ledger accepted −8 kg of 12 kg; the second, asking for −8 kg, must wait at
    /// <c>BEGIN</c> (it does not get in while the first is open), then sees the 4 kg left and is refused. Stock ends at 4,
    /// never −4.
    /// </summary>
    [Fact]
    public async Task Simultaneous_withdrawals_cannot_both_use_the_same_stock()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));
        var firstHasAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondIsCalling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHasBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task.Run gives each transaction its own thread: SQLite waits for its lock by blocking the thread.
        var first = Task.Run(() => InTransactionAsync(factory, async (_, ledger, _) =>
        {
            await ledger.AddAsync(Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user), Cancel);
            firstHasAdded.SetResult();
            await releaseFirst.Task; // hold the write lock until the test says so
        }), Cancel);

        try
        {
            await firstHasAdded.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            var second = Task.Run(() =>
            {
                secondIsCalling.SetResult();
                return InTransactionAsync(factory, async (_, ledger, _) =>
                {
                    secondHasBegun.SetResult();
                    await ledger.AddAsync(Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user), Cancel);
                });
            }, Cancel);

            await secondIsCalling.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            var firstToFinish = await Task.WhenAny(secondHasBegun.Task, Task.Delay(TimeSpan.FromMilliseconds(750), Cancel));
            Assert.NotSame(secondHasBegun.Task, firstToFinish);

            releaseFirst.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            var refused = await Assert.ThrowsAsync<ConflictException>(() => second.WaitAsync(TimeSpan.FromSeconds(10), Cancel));

            Assert.Equal("insufficient-stock", refused.Code);
            Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 4 {item.UnitName}.", refused.Detail);
            Assert.Equal(4m, await StockAsync(factory, item.Id, Location.Warehouse));
        }
        finally
        {
            // If an assertion failed, the first transaction would otherwise keep the write lock for later tests.
            releaseFirst.TrySetResult();
        }
    }

    /// <summary>
    /// PRD section 5: ten −2 kg withdrawals from 12 kg fired at once, each in its own scope and transaction. Exactly six
    /// succeed, the other four get <c>insufficient-stock</c>, and the stock ends at exactly 0, never below.
    /// </summary>
    [Fact]
    public async Task Ten_simultaneous_withdrawals_never_take_stock_below_zero()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));

        var attempts = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                await LedgerAsync(factory, Change(MovementType.Adjustment, item.Id, warehouse: -2m, userId: user));
                return "accepted";
            }
            catch (ConflictException refused)
            {
                return refused.Code;
            }
        }, Cancel));
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(6, outcomes.Count(outcome => outcome == "accepted"));
        Assert.Equal(4, outcomes.Count(outcome => outcome == "insufficient-stock"));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    // ---- StockLedger: shape, audit, insert-only -----------------------------------------------------------------

    /// <summary>
    /// Architecture 5.1, 5.2: the ledger's check is only safe inside the write transaction, so a call outside one is a
    /// programming error (<see cref="InvalidOperationException"/>) and nothing is added.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_to_run_outside_a_write_transaction()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.AddAsync(Change(MovementType.Received, item.Id, warehouse: 5m, userId: user), Cancel));

        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// Decision #77, architecture 5.2: <see cref="StockLedger"/> and <see cref="StockQueries"/> come from dependency
    /// injection with a scoped lifetime. Within one request scope a service always gets the same instance, and every
    /// request gets its own; so the ledger works on the request's own DbContext and therefore inside the request's
    /// write transaction (a ledger on another DbContext would see no transaction and refuse to run).
    /// </summary>
    [Fact]
    public async Task Ledger_and_queries_are_scoped_to_the_request()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<StockLedger>(), first.ServiceProvider.GetRequiredService<StockLedger>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<StockLedger>(), second.ServiceProvider.GetRequiredService<StockLedger>());
        Assert.Same(first.ServiceProvider.GetRequiredService<StockQueries>(), first.ServiceProvider.GetRequiredService<StockQueries>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<StockQueries>(), second.ServiceProvider.GetRequiredService<StockQueries>());

        // The injected ledger runs inside a transaction opened on the scope's own DbContext.
        var db = first.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = first.ServiceProvider.GetRequiredService<StockLedger>();
        await db.InWriteTransactionAsync(() =>
            ledger.AddAsync(Change(MovementType.Received, item.Id, warehouse: 5m, userId: user), Cancel), Cancel);

        Assert.Equal(5m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// D-2 (which columns each type fills, and their signs), PRD section 6 row 17 (no zero quantity), PRD 4.1 (at most 3
    /// decimal places) and architecture 3.1 (decimal(18,3)): a movement of the wrong shape is a programming error
    /// (<see cref="ArgumentException"/>, a <c>500</c> if it ever reached a client) and is not added. (v1.1) T-026,
    /// AC-28.1, D-11: a ShippedToCustomer movement with a positive, null or zero Warehouse change, or with any
    /// production-storage change, is refused the same way, even though it names a real sales order.
    /// </summary>
    [Theory]
    [InlineData(MovementType.Received, "-5", null)]
    [InlineData(MovementType.Received, "5", "5")]
    [InlineData(MovementType.Received, null, "5")]
    [InlineData(MovementType.Received, "0", null)]
    [InlineData(MovementType.Received, "0.0001", null)]
    [InlineData(MovementType.Received, "1000000000000000", null)]
    [InlineData(MovementType.SentToProduction, "-5", "4")]
    [InlineData(MovementType.SentToProduction, "5", "-5")]
    [InlineData(MovementType.SentToProduction, "-5", null)]
    [InlineData(MovementType.ConsumedInProduction, null, "5")]
    [InlineData(MovementType.ConsumedInProduction, "-5", "-5")]
    [InlineData(MovementType.ProducedOutput, null, "5")]
    [InlineData(MovementType.ReturnedToWarehouse, "5", "-4")]
    [InlineData(MovementType.ReturnedToWarehouse, "5", null)]
    [InlineData(MovementType.Adjustment, "5", "-5")]
    [InlineData(MovementType.Adjustment, null, null)]
    [InlineData(MovementType.Adjustment, null, "0")]
    [InlineData(MovementType.ShippedToCustomer, "5", null)]
    [InlineData(MovementType.ShippedToCustomer, null, null)]
    [InlineData(MovementType.ShippedToCustomer, null, "-5")]
    [InlineData(MovementType.ShippedToCustomer, "-5", "5")]
    [InlineData(MovementType.ShippedToCustomer, "-5", "-5")]
    [InlineData(MovementType.ShippedToCustomer, "0", null)]
    public async Task Ledger_refuses_a_movement_whose_shape_breaks_D2(MovementType type, string? warehouse, string? production)
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 100m, userId: user));

        // The links are right for the type, so the only fault is in the change columns, and the message says so.
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => LedgerAsync(factory,
            Change(type, item.Id, warehouse: Parse(warehouse), production: Parse(production), userId: user, links: links)));

        Assert.Matches("a change must be non-zero|its change columns do not match its type", refused.Message);
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// AC-05.3, design 2.3: every movement records its user and a UTC time in whole seconds. A movement without a user,
    /// with a local or unspecified time, or with a fraction of a second, is refused as a programming error; so is one
    /// that already has an id (a movement is added once, AC-11.3).
    /// </summary>
    [Theory]
    [InlineData("no user")]
    [InlineData("local time")]
    [InlineData("unspecified time")]
    [InlineData("fraction of a second")]
    [InlineData("already has an id")]
    public async Task Ledger_refuses_a_movement_without_user_and_utc_time(string fault)
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var now = WholeSecondsNow();
        var movement = fault switch
        {
            "no user" => new Movement { Type = MovementType.Received, ItemId = item.Id, WarehouseChange = 5m, ReceiptDate = Day, OccurredAt = now, UserId = "" },
            "local time" => new Movement { Type = MovementType.Received, ItemId = item.Id, WarehouseChange = 5m, ReceiptDate = Day, OccurredAt = now.ToLocalTime(), UserId = user },
            "unspecified time" => new Movement { Type = MovementType.Received, ItemId = item.Id, WarehouseChange = 5m, ReceiptDate = Day, OccurredAt = DateTime.SpecifyKind(now, DateTimeKind.Unspecified), UserId = user },
            "fraction of a second" => new Movement { Type = MovementType.Received, ItemId = item.Id, WarehouseChange = 5m, ReceiptDate = Day, OccurredAt = now.AddMilliseconds(250), UserId = user },
            _ => new Movement { Id = 123456, Type = MovementType.Received, ItemId = item.Id, WarehouseChange = 5m, ReceiptDate = Day, OccurredAt = now, UserId = user },
        };

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => LedgerAsync(factory, movement));

        Assert.Contains(fault switch
        {
            "no user" => "it has no UserId",
            "already has an id" => "it already has an id",
            _ => "OccurredAt must be a UTC time in whole seconds",
        }, refused.Message);
        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// Architecture 3.2 and design 5.6, the link and text fields of each type (StockLedger.CheckLinks). A movement is
    /// stored for ever (AC-11.3), so a wrong combination is refused as a programming error and nothing is added:
    /// ReceiptDate and Note only on Received (ReceiptDate required); Reason only, and required, on Adjustment;
    /// ReturnNumber only, and required, on ReturnedToWarehouse; ProductionOrderNumber required on SentToProduction,
    /// ConsumedInProduction and ProducedOutput, optional on ReturnedToWarehouse, not allowed on Received or Adjustment;
    /// ComplementaryOrderNumber only on SentToProduction. Note and Reason must be trimmed, not blank, and fit 500
    /// characters. <paramref name="fields"/> lists exactly the fields the movement carries.
    /// (v1.1) T-026, AC-28.1, decision #157: SalesOrderNumber is required on ShippedToCustomer and refused on every other
    /// type (the AC names Received, Adjustment and SentToProduction; the other three are covered too), and a shipment
    /// carrying a production order, complementary order, return, receipt date, note or reason is refused. The sales
    /// order named is real, so the only fault is the combination.
    /// </summary>
    [Theory]
    [InlineData(MovementType.Received, "", "ReceiptDate is required on Received")]
    [InlineData(MovementType.Received, "receiptDate,order", "ProductionOrderNumber is not allowed on Received")]
    [InlineData(MovementType.Received, "receiptDate,reason", "Reason is not allowed on Received")]
    [InlineData(MovementType.Received, "receiptDate,return", "ReturnNumber is not allowed on Received")]
    [InlineData(MovementType.Received, "receiptDate,blankNote", "Note must be trimmed, not blank")]
    [InlineData(MovementType.Received, "receiptDate,untrimmedNote", "Note must be trimmed, not blank")]
    [InlineData(MovementType.Received, "receiptDate,longNote", "Note must be trimmed, not blank, and at most 500 characters")]
    [InlineData(MovementType.SentToProduction, "", "ProductionOrderNumber is required on SentToProduction")]
    [InlineData(MovementType.SentToProduction, "complementary", "ProductionOrderNumber is required on SentToProduction")]
    [InlineData(MovementType.SentToProduction, "order,receiptDate", "ReceiptDate is not allowed on SentToProduction")]
    [InlineData(MovementType.SentToProduction, "order,note", "Note is not allowed on SentToProduction")]
    [InlineData(MovementType.SentToProduction, "order,return", "ReturnNumber is not allowed on SentToProduction")]
    [InlineData(MovementType.ConsumedInProduction, "", "ProductionOrderNumber is required on ConsumedInProduction")]
    [InlineData(MovementType.ConsumedInProduction, "order,complementary", "ComplementaryOrderNumber is not allowed on ConsumedInProduction")]
    [InlineData(MovementType.ProducedOutput, "", "ProductionOrderNumber is required on ProducedOutput")]
    [InlineData(MovementType.ProducedOutput, "order,complementary", "ComplementaryOrderNumber is not allowed on ProducedOutput")]
    [InlineData(MovementType.ProducedOutput, "order,reason", "Reason is not allowed on ProducedOutput")]
    [InlineData(MovementType.ReturnedToWarehouse, "", "ReturnNumber is required on ReturnedToWarehouse")]
    [InlineData(MovementType.ReturnedToWarehouse, "return,complementary", "ComplementaryOrderNumber is not allowed on ReturnedToWarehouse")]
    [InlineData(MovementType.ReturnedToWarehouse, "return,note", "Note is not allowed on ReturnedToWarehouse")]
    [InlineData(MovementType.Adjustment, "", "Reason is required on Adjustment")]
    [InlineData(MovementType.Adjustment, "blankReason", "Reason must be trimmed, not blank")]
    [InlineData(MovementType.Adjustment, "longReason", "Reason must be trimmed, not blank, and at most 500 characters")]
    [InlineData(MovementType.Adjustment, "reason,order", "ProductionOrderNumber is not allowed on Adjustment")]
    [InlineData(MovementType.Adjustment, "reason,complementary", "ComplementaryOrderNumber is not allowed on Adjustment")]
    [InlineData(MovementType.Adjustment, "reason,receiptDate", "ReceiptDate is not allowed on Adjustment")]
    [InlineData(MovementType.Adjustment, "reason,note", "Note is not allowed on Adjustment")]
    [InlineData(MovementType.ShippedToCustomer, "", "SalesOrderNumber is required on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,order", "ProductionOrderNumber is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,complementary", "ComplementaryOrderNumber is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,return", "ReturnNumber is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,receiptDate", "ReceiptDate is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,note", "Note is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder,reason", "Reason is not allowed on ShippedToCustomer")]
    [InlineData(MovementType.Received, "receiptDate,salesOrder", "SalesOrderNumber is not allowed on Received")]
    [InlineData(MovementType.Adjustment, "reason,salesOrder", "SalesOrderNumber is not allowed on Adjustment")]
    [InlineData(MovementType.SentToProduction, "order,salesOrder", "SalesOrderNumber is not allowed on SentToProduction")]
    [InlineData(MovementType.ConsumedInProduction, "order,salesOrder", "SalesOrderNumber is not allowed on ConsumedInProduction")]
    [InlineData(MovementType.ProducedOutput, "order,salesOrder", "SalesOrderNumber is not allowed on ProducedOutput")]
    [InlineData(MovementType.ReturnedToWarehouse, "return,salesOrder", "SalesOrderNumber is not allowed on ReturnedToWarehouse")]
    public async Task Ledger_refuses_link_fields_that_do_not_fit_the_type(MovementType type, string fields, string expected)
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await StockBothLocationsAsync(item.Id, user, links);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            LedgerAsync(factory, WithFields(type, item.Id, user, links, fields)));

        Assert.Contains(expected, refused.Message);
        Assert.Equal(2, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// Architecture 3.2 and design 5.6: every combination the API will write is accepted, including the optional ones (a
    /// receipt with a note, a complementary send naming its parent order, a return linked to an order), and (v1.1) a
    /// shipment naming its sales order, the combination the ship (T-027) will write.
    /// </summary>
    [Theory]
    [InlineData(MovementType.Received, "receiptDate")]
    [InlineData(MovementType.Received, "receiptDate,note")]
    [InlineData(MovementType.SentToProduction, "order")]
    [InlineData(MovementType.SentToProduction, "order,complementary")]
    [InlineData(MovementType.ConsumedInProduction, "order")]
    [InlineData(MovementType.ProducedOutput, "order")]
    [InlineData(MovementType.ReturnedToWarehouse, "return")]
    [InlineData(MovementType.ReturnedToWarehouse, "return,order")]
    [InlineData(MovementType.Adjustment, "reason")]
    [InlineData(MovementType.ShippedToCustomer, "salesOrder")]
    public async Task Ledger_accepts_every_link_combination_the_api_writes(MovementType type, string fields)
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await StockBothLocationsAsync(item.Id, user, links);
        var movement = WithFields(type, item.Id, user, links, fields);

        await LedgerAsync(factory, movement);

        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements
            .AsNoTracking().SingleAsync(candidate => candidate.Id == movement.Id, Cancel);
        Assert.Equal(movement.ProductionOrderNumber, stored.ProductionOrderNumber);
        Assert.Equal(movement.ComplementaryOrderNumber, stored.ComplementaryOrderNumber);
        Assert.Equal(movement.ReturnNumber, stored.ReturnNumber);
        Assert.Equal(movement.ReceiptDate, stored.ReceiptDate);
        Assert.Equal(movement.Note, stored.Note);
        Assert.Equal(movement.Reason, stored.Reason);
        Assert.Equal(movement.SalesOrderNumber, stored.SalesOrderNumber);
    }

    // ---- (v1.1) StockLedger: shipments (T-026) -----------------------------------------------------------------

    /// <summary>
    /// T-026, D-2, AC-28.1, AC-26.2 figures: the ledger accepts a ShippedToCustomer movement with a negative Warehouse
    /// change, no production-storage change and the number of an existing sales order. With 1,001 in the Warehouse,
    /// shipping 600 leaves 401; production storage is untouched; the stored row keeps its sales order number and has
    /// none of the other links.
    /// </summary>
    [Fact]
    public async Task Ledger_accepts_a_shipment_and_the_warehouse_goes_down_by_its_quantity()
    {
        var item = await CreateItemAsync(ItemType.FinishedProduct);
        var user = await CreateUserAsync();
        var salesOrder = await CreateSalesOrderForAsync(item.Id, 600m);
        await LedgerAsync(factory, Change(MovementType.Adjustment, item.Id, warehouse: 1001m, userId: user));
        var shipment = Shipment(item.Id, 600m, user, salesOrder);

        await LedgerAsync(factory, shipment);

        Assert.Equal(401m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.ProductionStorage));
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements
            .AsNoTracking().SingleAsync(candidate => candidate.Id == shipment.Id, Cancel);
        Assert.Equal(MovementType.ShippedToCustomer, stored.Type);
        Assert.Equal(-600m, stored.WarehouseChange);
        Assert.Null(stored.ProductionStorageChange);
        Assert.Equal(salesOrder, stored.SalesOrderNumber);
        Assert.Null(stored.ProductionOrderNumber);
        Assert.Null(stored.ComplementaryOrderNumber);
        Assert.Null(stored.ReturnNumber);
        Assert.Null(stored.ReceiptDate);
        Assert.Null(stored.Note);
        Assert.Null(stored.Reason);
    }

    /// <summary>
    /// T-026, D-2, AC-12.3 pattern: a shipment larger than the Warehouse stock is refused with <c>409</c>
    /// <c>insufficient-stock</c> and the ledger's own message, and nothing is saved (the receipt made earlier in the
    /// same transaction is rolled back too). Shipping exactly what is there is allowed and leaves 0.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_a_shipment_that_takes_the_warehouse_below_zero()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var salesOrder = await CreateSalesOrderForAsync(item.Id, 12.5m);
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerAsync(factory,
            Change(MovementType.Received, item.Id, warehouse: 0.25m, userId: user),
            Shipment(item.Id, 12.5m, user, salesOrder)));

        Assert.Equal("insufficient-stock", refused.Code);
        Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 12.25 {item.UnitName}.", refused.Detail);
        Assert.Equal(12m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));

        await LedgerAsync(factory, Shipment(item.Id, 12m, user, salesOrder));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// T-026, architecture 5.1 (v1.1), decision #157: the ledger does not read the sales order; the foreign key refuses a
    /// shipment naming one that does not exist (<see cref="DbUpdateException"/>, "FOREIGN KEY constraint failed"), and
    /// the whole transaction rolls back, so the adjustment saved before it in the same transaction is gone too.
    /// </summary>
    [Fact]
    public async Task Database_refuses_a_shipment_naming_an_unknown_sales_order()
    {
        var item = await CreateItemAsync(ItemType.FinishedProduct);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Adjustment, item.Id, warehouse: 50m, userId: user));

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => LedgerAsync(factory,
            Change(MovementType.Adjustment, item.Id, warehouse: 5m, userId: user),
            Shipment(item.Id, 10m, user, salesOrderNumber: 999_999)));

        Assert.Contains("FOREIGN KEY constraint failed", refused.InnerException?.Message);
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));
        Assert.Equal(50m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// T-026, AC-26.10 backstop, decision #157: a second shipment movement for the same sales order and item is refused
    /// by <c>IX_Movements_OneShipmentPerLine</c> (<see cref="DbUpdateException"/>, UNIQUE), even when stock would cover
    /// it, and its transaction saves nothing. Another item of the same sales order (another line) is fine.
    /// </summary>
    [Fact]
    public async Task Database_refuses_a_second_shipment_for_the_same_sales_order_and_item()
    {
        var item = await CreateItemAsync(ItemType.FinishedProduct);
        var other = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var customer = await CreateCustomerAsync(manager);
        var salesOrder = await CreateSalesOrderAsync(manager, customer.Id, (item.Id, 10m), (other.Id, 3m));
        await LedgerAsync(factory,
            Change(MovementType.Adjustment, item.Id, warehouse: 100m, userId: user),
            Change(MovementType.Received, other.Id, warehouse: 100m, userId: user));
        await LedgerAsync(factory, Shipment(item.Id, 10m, user, salesOrder), Shipment(other.Id, 3m, user, salesOrder));

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => LedgerAsync(factory,
            Change(MovementType.Adjustment, item.Id, warehouse: 5m, userId: user),
            Shipment(item.Id, 10m, user, salesOrder)));

        Assert.Contains("UNIQUE constraint failed: Movements.SalesOrderNumber, Movements.ItemId", refused.InnerException?.Message);
        Assert.Equal(2, await CountMovementsAsync(factory, item.Id));
        Assert.Equal(90m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(97m, await StockAsync(factory, other.Id, Location.Warehouse));
    }

    /// <summary>
    /// Decision #86 (T-008 review probe): a movement added to the DbContext directly, next to a ledger movement in the
    /// same request, would be saved by the ledger's save without any stock check. A direct −100 kg next to a ledger
    /// +1 kg on 0 kg once took the Warehouse to −99. The ledger now refuses (<see cref="InvalidOperationException"/>),
    /// the request saves nothing, and the stock stays at 0.
    /// </summary>
    [Fact]
    public async Task Ledger_refuses_to_save_a_movement_added_without_it()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => InTransactionAsync(factory, async (db, ledger, _) =>
        {
            db.Movements.Add(Change(MovementType.Adjustment, item.Id, warehouse: -100m, userId: user));
            await ledger.AddAsync(Change(MovementType.Received, item.Id, warehouse: 1m, userId: user), Cancel);
        }));

        Assert.Contains("without StockLedger", refused.Message);
        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    // ---- (v1.2) StockLedger: batches (T-035, SEC-24) -------------------------------------------------------------
    //
    // AddRangeAsync adds many movements with one stock read and one save (SEC-24: a 500-line shipment held the write
    // lock for seconds). AC-55.3: its rules are exactly AddAsync's, applied to each movement in order, so a batch must
    // end in the same state as the same movements added one call at a time.

    /// <summary>
    /// SEC-24, AC-55.1, T-035: a batch of three movements (a receipt and a write-off of one item, a shipment of another)
    /// is saved with one <c>SaveChangesAsync</c>, not one per movement. Every movement gets its database id, and the
    /// stock is what the three changes add up to.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_saves_every_movement_with_one_save()
    {
        var cheese = await CreateItemAsync(ItemType.RawMaterial);
        var bags = await CreateItemAsync(ItemType.FinishedProduct);
        var user = await CreateUserAsync();
        var salesOrder = await CreateSalesOrderForAsync(bags.Id, 30m);
        await LedgerAsync(factory, Change(MovementType.Adjustment, bags.Id, warehouse: 50m, userId: user));
        Movement[] batch =
        [
            Change(MovementType.Received, cheese.Id, warehouse: 12m, userId: user),
            Change(MovementType.Adjustment, cheese.Id, warehouse: -5m, userId: user),
            Shipment(bags.Id, 30m, user, salesOrder),
        ];
        var savesByTheLedger = 0;

        await InTransactionAsync(factory, async (db, ledger, _) =>
        {
            // SavingChanges is an event the DbContext raises at the start of every SaveChanges call. Counted only around
            // the ledger call, because InWriteTransactionAsync saves once more before it commits.
            var saves = 0;
            db.SavingChanges += (_, _) => saves++;
            await ledger.AddRangeAsync(batch, Cancel);
            savesByTheLedger = saves;
        });

        Assert.Equal(1, savesByTheLedger);
        Assert.All(batch, movement => Assert.True(movement.Id > 0));
        Assert.Equal(3, batch.Select(movement => movement.Id).Distinct().Count());
        Assert.Equal(7m, await StockAsync(factory, cheese.Id, Location.Warehouse));
        Assert.Equal(20m, await StockAsync(factory, bags.Id, Location.Warehouse));
    }

    /// <summary>
    /// AC-55.3, D-2, architecture 5.1 (as <see cref="Ledger_counts_earlier_movements_of_the_same_request"/> for single
    /// calls): inside one batch, each movement is checked against the stock left by the movements before it. Two −8 kg
    /// on 12 kg: the second is refused against the 4 kg left, and nothing is saved. A receipt of 5 kg earlier in a batch
    /// covers a −15 kg later in it (12 + 5 − 15 = 2), as it would have if each had been added on its own.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_checks_each_movement_against_the_movements_before_it()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 12m, userId: user));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerBatchAsync(factory,
            Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user),
            Change(MovementType.Adjustment, item.Id, warehouse: -8m, userId: user)));

        Assert.Equal("insufficient-stock", refused.Code);
        Assert.Equal($"Not enough stock: {item.Name} in Warehouse has 4 {item.UnitName}.", refused.Detail);
        Assert.Equal(12m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));

        await LedgerBatchAsync(factory,
            Change(MovementType.Received, item.Id, warehouse: 5m, userId: user),
            Change(MovementType.Adjustment, item.Id, warehouse: -15m, userId: user));

        Assert.Equal(2m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(3, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// AC-55.3, AC-26.3 (all or nothing), D-2: three items with 10, 3 and 1 in the Warehouse and a batch taking 5, 4 and
    /// 2. The ledger names the first short movement in batch order (the second item, "has 3"), the same one a call per
    /// movement would have stopped at, and saves none of the three: the first item, which was covered, still has 10.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_refuses_the_first_short_movement_and_saves_none()
    {
        var first = await CreateItemAsync(ItemType.RawMaterial);
        var second = await CreateItemAsync(ItemType.RawMaterial);
        var third = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await LedgerAsync(factory,
            Change(MovementType.Received, first.Id, warehouse: 10m, userId: user),
            Change(MovementType.Received, second.Id, warehouse: 3m, userId: user),
            Change(MovementType.Received, third.Id, warehouse: 1m, userId: user));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerBatchAsync(factory,
            Change(MovementType.Adjustment, first.Id, warehouse: -5m, userId: user),
            Change(MovementType.Adjustment, second.Id, warehouse: -4m, userId: user),
            Change(MovementType.Adjustment, third.Id, warehouse: -2m, userId: user)));

        Assert.Equal($"Not enough stock: {second.Name} in Warehouse has 3 {second.UnitName}.", refused.Detail);
        Assert.Equal(10m, await StockAsync(factory, first.Id, Location.Warehouse));
        Assert.Equal(3m, await StockAsync(factory, second.Id, Location.Warehouse));
        Assert.Equal(1m, await StockAsync(factory, third.Id, Location.Warehouse));
        Assert.Equal(3, await CountMovementsAsync(factory, first.Id) + await CountMovementsAsync(factory, second.Id)
            + await CountMovementsAsync(factory, third.Id));
    }

    /// <summary>
    /// AC-55.3, D-2: the running balance covers Production storage too. With 10 kg in the Warehouse and none in
    /// Production storage, a batch that sends the 10 kg and then consumes 10.5 kg is refused for Production storage
    /// against the 10 kg the send put there, and saves nothing. Sending 10 and consuming exactly 10 is accepted, and both
    /// locations end at 0.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_counts_production_storage_movements_before_it()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await LedgerAsync(factory, Change(MovementType.Received, item.Id, warehouse: 10m, userId: user));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => LedgerBatchAsync(factory,
            Change(MovementType.SentToProduction, item.Id, warehouse: -10m, production: 10m, userId: user, links: links),
            Change(MovementType.ConsumedInProduction, item.Id, production: -10.5m, userId: user, links: links)));

        Assert.Equal($"Not enough stock: {item.Name} in Production storage has 10 {item.UnitName}.", refused.Detail);
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));

        await LedgerBatchAsync(factory,
            Change(MovementType.SentToProduction, item.Id, warehouse: -10m, production: 10m, userId: user, links: links),
            Change(MovementType.ConsumedInProduction, item.Id, production: -10m, userId: user, links: links));

        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.ProductionStorage));
    }

    /// <summary>
    /// Decision #86 for batches: a movement put on the DbContext directly, next to a ledger batch in the same request,
    /// would ride along on the batch's one save without any check. The ledger refuses
    /// (<see cref="InvalidOperationException"/>), and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_refuses_to_save_a_movement_added_without_it()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => InTransactionAsync(factory, async (db, ledger, _) =>
        {
            db.Movements.Add(Change(MovementType.Adjustment, item.Id, warehouse: -100m, userId: user));
            await ledger.AddRangeAsync([Change(MovementType.Received, item.Id, warehouse: 1m, userId: user)], Cancel);
        }));

        Assert.Contains("without StockLedger", refused.Message);
        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// Architecture 5.1, 5.2 for batches: outside a write transaction the batch is a programming error
    /// (<see cref="InvalidOperationException"/>) and nothing is added.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_refuses_to_run_outside_a_write_transaction()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.AddRangeAsync([Change(MovementType.Received, item.Id, warehouse: 5m, userId: user)], Cancel));

        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// D-2, AC-11.3 for batches: a malformed movement anywhere in the batch (here a receipt with a negative change, after
    /// a valid one) is a programming error (<see cref="ArgumentException"/>) and none of the batch is saved; so is the
    /// same movement object given twice, since a movement is added only once.
    /// </summary>
    [Fact]
    public async Task Ledger_batch_with_a_malformed_or_repeated_movement_saves_none()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var twice = Change(MovementType.Received, item.Id, warehouse: 5m, userId: user);

        var malformed = await Assert.ThrowsAsync<ArgumentException>(() => LedgerBatchAsync(factory,
            Change(MovementType.Received, item.Id, warehouse: 5m, userId: user),
            Change(MovementType.Received, item.Id, warehouse: -5m, userId: user)));
        var repeated = await Assert.ThrowsAsync<ArgumentException>(() => LedgerBatchAsync(factory, twice, twice));

        Assert.Contains("its change columns do not match its type", malformed.Message);
        Assert.Contains("appears twice in the batch", repeated.Message);
        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
    }

    /// <summary>
    /// AC-26.10 backstop, decision #157, AC-55.3: with every shipment movement in one save, the unique index
    /// <c>IX_Movements_OneShipmentPerLine</c> still refuses two movements for the same sales order and item
    /// (<see cref="DbUpdateException"/>, UNIQUE), even though the stock would cover both, and the save stores neither.
    /// </summary>
    [Fact]
    public async Task Database_refuses_a_batch_with_two_shipments_for_the_same_sales_order_and_item()
    {
        var item = await CreateItemAsync(ItemType.FinishedProduct);
        var user = await CreateUserAsync();
        var salesOrder = await CreateSalesOrderForAsync(item.Id, 10m);
        await LedgerAsync(factory, Change(MovementType.Adjustment, item.Id, warehouse: 100m, userId: user));

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => LedgerBatchAsync(factory,
            Shipment(item.Id, 10m, user, salesOrder), Shipment(item.Id, 10m, user, salesOrder)));

        Assert.Contains("UNIQUE constraint failed: Movements.SalesOrderNumber, Movements.ItemId", refused.InnerException?.Message);
        Assert.Equal(1, await CountMovementsAsync(factory, item.Id));
        Assert.Equal(100m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// (T-035) Many-items stock read, AC-10.2: <see cref="StockQueries.GetStockAsync(IReadOnlyCollection{int}, Location, CancellationToken)"/>
    /// gives each requested item's stock in the location asked for, the same figure as the one-item read, and 0 for an
    /// item with no movements; a repeated id appears once.
    /// </summary>
    [Fact]
    public async Task Stock_of_many_items_matches_the_one_item_read()
    {
        var stocked = await CreateItemAsync(ItemType.RawMaterial);
        var empty = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, stocked.Id, user);
        await LedgerAsync(factory,
            Change(MovementType.Received, stocked.Id, warehouse: 12.5m, userId: user),
            Change(MovementType.SentToProduction, stocked.Id, warehouse: -2.25m, production: 2.25m, userId: user, links: links));
        await using var scope = factory.Services.CreateAsyncScope();
        var stock = scope.ServiceProvider.GetRequiredService<StockQueries>();

        var warehouse = await stock.GetStockAsync([stocked.Id, empty.Id, stocked.Id], Location.Warehouse, Cancel);
        var production = await stock.GetStockAsync([stocked.Id, empty.Id], Location.ProductionStorage, Cancel);

        Assert.Equal(2, warehouse.Count);
        Assert.Equal(10.25m, warehouse[stocked.Id]);
        Assert.Equal(await stock.GetStockAsync(stocked.Id, Location.Warehouse, Cancel), warehouse[stocked.Id]);
        Assert.Equal(0m, warehouse[empty.Id]);
        Assert.Equal(2.25m, production[stocked.Id]);
        Assert.Equal(0m, production[empty.Id]);
    }

    /// <summary>
    /// Decision #85: inside a write transaction, <see cref="StockQueries.GetFiguresAsync"/> uses that transaction rather
    /// than opening its own (which would fail: one connection cannot hold two). So it sees the request's own movement,
    /// saved by the ledger but not yet committed, and the transaction is still open afterwards for the request's
    /// remaining work.
    /// </summary>
    [Fact]
    public async Task Figures_inside_a_write_transaction_use_that_transaction()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
        var stock = scope.ServiceProvider.GetRequiredService<StockQueries>();

        var (figures, stillOpen) = await db.InWriteTransactionAsync(async () =>
        {
            await ledger.AddAsync(Change(MovementType.Received, item.Id, warehouse: 7m, userId: user), Cancel);
            var read = await stock.GetFiguresAsync([item.Id], Cancel);
            return (read[item.Id], db.Database.CurrentTransaction is not null);
        }, Cancel);

        Assert.Equal(7m, figures.Warehouse);
        Assert.True(stillOpen);
        Assert.Equal(7m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    /// <summary>
    /// Decision #85 (T-008 review round 2): the read transaction <see cref="StockQueries.GetFiguresAsync"/> opens for
    /// itself must leave the request's DbContext exactly as it found it: connection closed again and no transaction,
    /// after a normal read and after a read cancelled half-way (the client disconnecting between two of its queries).
    /// A connection the caller had already opened stays open. A connection left open would hold a pooled SQLite
    /// connection for the rest of the request, and a transaction left attached would make the ledger think a write
    /// transaction is open.
    /// </summary>
    [Fact]
    public async Task Figures_outside_a_transaction_leave_the_connection_as_they_found_it()
    {
        using var cancelHalfWay = new CancellationTokenSource();
        var pause = new AfterFirstReadInterceptor(
            command => command.CommandText.Contains("FROM \"Movements\"", StringComparison.Ordinal)
                       && command.CommandText.Contains("GROUP BY", StringComparison.Ordinal),
            () => cancelHalfWay.CancelAsync());
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(pause)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await InsertMovementsAsync(app, Change(MovementType.Received, item.Id, warehouse: 5m));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = scope.ServiceProvider.GetRequiredService<StockQueries>();

        // A normal read.
        var figures = await stock.GetFiguresAsync([item.Id], Cancel);
        Assert.Equal(5m, figures[item.Id].Warehouse);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.Null(db.Database.CurrentTransaction);

        // A read cancelled after its first query, while the read transaction is open.
        pause.Arm();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stock.GetFiguresAsync([item.Id], cancelHalfWay.Token));
        Assert.True(pause.HasRun);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.Null(db.Database.CurrentTransaction);

        // A connection the caller opened itself is left open.
        await db.Database.OpenConnectionAsync(Cancel);
        figures = await stock.GetFiguresAsync([item.Id], Cancel);
        Assert.Equal(5m, figures[item.Id].Warehouse);
        Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
        Assert.Null(db.Database.CurrentTransaction);
        await db.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    /// <summary>
    /// Decision #85: the queries behind one stock row share one database snapshot. The test pauses <c>GET /stock</c>
    /// right after its first query (the movement sums) and, in that gap, commits a send: order InProduction with 50 kg
    /// of leftover applied and 950 kg sent, and the SentToProduction movement. The paused request must still show one
    /// consistent moment, before the send (Warehouse 1,000, storage 50, committed 0, free 50), never the mix "storage 50,
    /// committed 1,000, free 0". The next request shows the state after the send.
    /// </summary>
    [Fact]
    public async Task One_stock_row_reads_one_snapshot_even_when_a_send_commits_in_between()
    {
        Func<Task> betweenQueries = () => Task.CompletedTask;
        var pause = new AfterFirstReadInterceptor(
            command => command.CommandText.Contains("FROM \"Movements\"", StringComparison.Ordinal)
                       && command.CommandText.Contains("GROUP BY", StringComparison.Ordinal),
            () => betweenQueries());
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(pause)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var user = await app.CreateUserAsync(Seed.UniqueEmail("sender"), Roles.Warehouse);
        await InsertMovementsAsync(app,
            Change(MovementType.Received, cheese.Id, warehouse: 1050m),
            Change(MovementType.SentToProduction, cheese.Id, warehouse: -50m, production: 50m));
        int orderNumber;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = NewOrder(product.Id, user.Id, OrderStatus.Created);
            order.Lines.Add(new ProductionOrderLine { LineNo = 1, RawMaterialId = cheese.Id, AmountPerUnit = 1m, PlannedAmount = 1000m });
            db.ProductionOrders.Add(order);
            await db.SaveChangesAsync(Cancel);
            orderNumber = order.Number;
        }

        // What T-010's send will do, committed by another request while the read is paused.
        betweenQueries = async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
            await db.InWriteTransactionAsync(async () =>
            {
                var order = await db.ProductionOrders.Include(candidate => candidate.Lines)
                    .SingleAsync(candidate => candidate.Number == orderNumber, Cancel);
                order.Status = OrderStatus.InProduction;
                order.SentAt = order.StatusChangedAt = WholeSecondsNow();
                order.SentById = user.Id;
                var line = order.Lines.Single();
                line.LeftoverApplied = 50m;
                line.WarehouseSend = 950m;
                line.CarriedLeftover = 0m;
                line.ComplementarySends = 0m;
                await ledger.AddAsync(Change(MovementType.SentToProduction, cheese.Id, warehouse: -950m, production: 950m,
                    userId: user.Id, links: new Links(orderNumber, 0, 0, 0)), Cancel);
            }, Cancel);
        };
        var reader = await app.CreateClientAsAsync(Roles.Production);

        pause.Arm();
        var during = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{cheese.Id}", Cancel));
        var after = await ApiJson.ReadAsync(await reader.GetAsync($"{StockPath}/{cheese.Id}", Cancel));

        Assert.True(pause.HasRun);
        AssertLeftoverFigures(during, warehouse: 1000m, production: 50m, committed: 0m, orderNumber: null, pending: 0m, free: 50m);
        AssertLeftoverFigures(after, warehouse: 50m, production: 1000m, committed: 1000m, orderNumber: orderNumber, pending: 0m, free: 0m);
    }

    /// <summary>
    /// PRD section 5, D-2: different movement types racing on one item. Starting from 20 kg in each location, 32
    /// movements are fired at once: Warehouse withdrawals (−3), receipts (+2), sends (Warehouse −4, storage +4) and
    /// consumptions (storage −5). Some are refused, because together they ask for more than there is. The accepted ones
    /// are applied one at a time in id order, so replaying them in id order must keep both locations at zero or above
    /// after every single movement, not only at the end.
    /// </summary>
    [Fact]
    public async Task Mixed_simultaneous_movements_never_take_either_location_below_zero()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var links = await CreateLinksAsync(factory, item.Id, user);
        await StockBothLocationsAsync(item.Id, user, links, 20m);

        var movements = Enumerable.Range(0, 8).SelectMany(_ => new[]
        {
            Change(MovementType.Adjustment, item.Id, warehouse: -3m, userId: user),
            Change(MovementType.Received, item.Id, warehouse: 2m, userId: user),
            Change(MovementType.SentToProduction, item.Id, warehouse: -4m, production: 4m, userId: user, links: links),
            Change(MovementType.ConsumedInProduction, item.Id, production: -5m, userId: user, links: links),
        }).ToList();
        var outcomes = await Task.WhenAll(movements.Select(movement => Task.Run(async () =>
        {
            try
            {
                await LedgerAsync(factory, movement);
                return "accepted";
            }
            catch (ConflictException refused)
            {
                return refused.Code;
            }
        }, Cancel)));

        // Warehouse demand alone (8 × 3 + 8 × 4 = 56 kg) is more than 20 kg plus every receipt (16 kg).
        Assert.Contains("insufficient-stock", outcomes);
        Assert.All(outcomes, outcome => Assert.True(outcome is "accepted" or "insufficient-stock", outcome));

        await using var scope = factory.Services.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ItemId == item.Id)
            .OrderBy(movement => movement.Id)
            .Select(movement => new { movement.WarehouseChange, movement.ProductionStorageChange })
            .ToListAsync(Cancel);
        Assert.Equal(2 + outcomes.Count(outcome => outcome == "accepted"), history.Count);

        var warehouse = 0m;
        var production = 0m;
        foreach (var step in history)
        {
            warehouse += step.WarehouseChange ?? 0m;
            production += step.ProductionStorageChange ?? 0m;
            Assert.True(warehouse >= 0m, $"Warehouse went to {warehouse}.");
            Assert.True(production >= 0m, $"Production storage went to {production}.");
        }
    }

    /// <summary>
    /// AC-11.3, decision #35: a movement the ledger wrote can never be changed or deleted afterwards; the DbContext
    /// refuses the save and the movement keeps its values. (The <c>405</c> for <c>PUT</c>/<c>DELETE</c> on
    /// <c>/movements/{id}</c> is T-013's.)
    /// </summary>
    [Fact]
    public async Task Movements_written_by_the_ledger_are_insert_only()
    {
        var item = await CreateItemAsync(ItemType.RawMaterial);
        var user = await CreateUserAsync();
        var movement = Change(MovementType.Received, item.Id, warehouse: 40m, userId: user);
        await LedgerAsync(factory, movement);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Movements.SingleAsync(candidate => candidate.Id == movement.Id, Cancel);
            db.Entry(stored).Property(candidate => candidate.WarehouseChange).CurrentValue = 4000m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Cancel));

            db.ChangeTracker.Clear();
            db.Movements.Remove(await db.Movements.SingleAsync(candidate => candidate.Id == movement.Id, Cancel));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Cancel));
        }

        Assert.Equal(40m, await StockAsync(factory, item.Id, Location.Warehouse));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static IEnumerable<int> ItemIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("itemId").GetInt32()).ToList();

    private static void AssertLeftoverFigures(JsonElement body, decimal warehouse, decimal production, decimal committed,
        int? orderNumber, decimal pending, decimal free)
    {
        Assert.Equal(warehouse, body.GetProperty("warehouse").GetDecimal());
        Assert.Equal(production, body.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(committed, body.GetProperty("committedToOrder").GetDecimal());
        if (orderNumber is { } number)
        {
            Assert.Equal(number, body.GetProperty("committedOrderNumber").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("committedOrderNumber").ValueKind);
        }

        Assert.Equal(pending, body.GetProperty("pendingReturns").GetDecimal());
        Assert.Equal(free, body.GetProperty("freeLeftover").GetDecimal());
    }

    /// <summary>
    /// (v1.1) Design 5.5: the fields of a stock row, in order. <c>openDemand</c> sits right after the two stock figures
    /// (AC-31.1); for a finished product the last four are null, but the field list is the same.
    /// </summary>
    private static readonly string[] StockFields =
    [
        "itemId", "itemName", "itemType", "unit", "warehouse", "productionStorage", "openDemand",
        "committedToOrder", "committedOrderNumber", "pendingReturns", "freeLeftover",
    ];

    /// <summary>
    /// (v1.1) AC-31.1: a row's <c>openDemand</c>, after checking it is a JSON number. <c>GetDecimal</c> alone would
    /// throw on null too, but this way a null fails with a message that says so.
    /// </summary>
    private static decimal OpenDemand(JsonElement row)
    {
        var value = row.GetProperty("openDemand");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        return value.GetDecimal();
    }

    /// <summary>
    /// (v1.1) AC-31.3: every field of a stock row except <c>openDemand</c>, as name and raw JSON, so two reads of one
    /// row can be compared field by field while open demand is allowed to differ.
    /// </summary>
    private static List<string> FieldsExceptOpenDemand(JsonElement row) =>
        row.EnumerateObject()
            .Where(field => field.Name != "openDemand")
            .Select(field => $"{field.Name}={field.Value.GetRawText()}")
            .ToList();

    /// <summary>
    /// (v1.1) AC-31.2: the item's Warehouse stock and open demand through <c>GET /stock/{itemId}</c>, and that its row in
    /// <c>GET /stock</c> is exactly the same. For a factory of the test's own, so the item is on the first page.
    /// </summary>
    private static async Task AssertStockAndDemandAsync(HttpClient client, int itemId, decimal warehouse, decimal openDemand)
    {
        var single = await OrderFlow.StockAsync(client, itemId);
        Assert.Equal(warehouse, single.GetProperty("warehouse").GetDecimal());
        Assert.Equal(openDemand, OpenDemand(single));

        var list = await ApiJson.ReadAsync(await client.GetAsync(StockPath, Cancel));
        var row = list.GetProperty("items").EnumerateArray().Single(candidate => candidate.GetProperty("itemId").GetInt32() == itemId);
        Assert.Equal(single.GetRawText(), row.GetRawText());
    }

    private async Task<CreatedItem> CreateItemAsync(ItemType type)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        return await Seed.CreateItemAsync(manager, type);
    }

    private async Task<string> CreateUserAsync() =>
        (await factory.CreateUserAsync(Seed.UniqueEmail("ledger"), Roles.Manager)).Id;

    private static DateTime WholeSecondsNow()
    {
        var ticks = DateTime.UtcNow.Ticks;
        return new DateTime(ticks - ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static decimal? Parse(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A new movement with a UTC whole-second time and the link fields its type needs (StockLedger.CheckLinks): a
    /// receipt date on Received, a reason on Adjustment, the order of <paramref name="links"/> on a send, consumption or
    /// output, its return on ReturnedToWarehouse, (v1.1) its sales order on ShippedToCustomer. Without
    /// <paramref name="userId"/> it has no user yet;
    /// <see cref="InsertMovementsAsync"/> fills one in.
    /// </summary>
    private static Movement Change(MovementType type, int itemId, decimal? warehouse = null, decimal? production = null,
        string userId = "", Links? links = null) => new()
    {
        Type = type, ItemId = itemId, WarehouseChange = warehouse, ProductionStorageChange = production,
        OccurredAt = WholeSecondsNow(), UserId = userId,
        ReceiptDate = type == MovementType.Received ? new DateOnly(2026, 11, 1) : null,
        Reason = type == MovementType.Adjustment ? "Count correction" : null,
        ProductionOrderNumber = type is MovementType.SentToProduction or MovementType.ConsumedInProduction or MovementType.ProducedOutput
            ? links?.Order : null,
        ReturnNumber = type == MovementType.ReturnedToWarehouse ? links?.Return : null,
        SalesOrderNumber = type == MovementType.ShippedToCustomer ? links?.SalesOrder : null,
    };

    /// <summary>
    /// (v1.1) A ShippedToCustomer movement of <paramref name="quantity"/> (taken from the Warehouse, so the change is
    /// negative) for <paramref name="salesOrderNumber"/>, at a UTC whole-second time: the shape the ship (T-027) writes,
    /// architecture 5.3 step 6.
    /// </summary>
    private static Movement Shipment(int itemId, decimal quantity, string userId, int salesOrderNumber) => new()
    {
        Type = MovementType.ShippedToCustomer, ItemId = itemId, WarehouseChange = -quantity,
        OccurredAt = WholeSecondsNow(), UserId = userId, SalesOrderNumber = salesOrderNumber,
    };

    /// <summary>
    /// (v1.1) Creates a customer and a sales order with one line (<paramref name="quantity"/> of
    /// <paramref name="itemId"/>) through the API (T-024, T-025), and returns the sales order's number.
    /// </summary>
    private async Task<int> CreateSalesOrderForAsync(int itemId, decimal quantity)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var customer = await CreateCustomerAsync(manager);
        return await CreateSalesOrderAsync(manager, customer.Id, (itemId, quantity));
    }

    /// <summary>
    /// Real order, complementary-order, return and (v1.1) sales order numbers for movements to link to (the links are
    /// foreign keys).
    /// </summary>
    private sealed record Links(int Order, int Complementary, int Return, int SalesOrder);

    /// <summary>
    /// Creates a Created production order (for a new finished product), a PendingApproval complementary order under it,
    /// and a Pending return of <paramref name="itemId"/>, straight in the database, each inside a write transaction as a
    /// service's writes would be (the return through <see cref="OrderFlow.InsertPendingReturnAsync"/>). Created, not
    /// InProduction, because the shared database allows only one InProduction order; the ledger does not look at statuses.
    /// (v1.1) Also a Created sales order with one line of <paramref name="itemId"/>, through the API (T-025), so a
    /// shipment movement can name it; a Created status is enough, for the same reason.
    /// </summary>
    private static async Task<Links> CreateLinksAsync(TestApiFactory app, int itemId, string userId)
    {
        var (orderNumber, complementaryNumber) = (0, 0);
        await InTransactionAsync(app, async (db, _, _) =>
        {
            var unitName = Seed.UniqueName("unit");
            var productName = Seed.UniqueName("product");
            var product = new Item
            {
                Name = productName, NameNormalized = NameNormalizer.Normalize(productName), Type = ItemType.FinishedProduct,
                Unit = new UnitOfMeasure { Name = unitName, NameNormalized = NameNormalizer.Normalize(unitName) },
                CreatedAt = WholeSecondsNow(),
            };
            var order = NewOrder(0, userId, OrderStatus.Created);
            order.FinishedProduct = product;
            var complementary = new ComplementaryOrder
            {
                ParentOrder = order, Status = ComplementaryOrderStatus.PendingApproval, Reason = "Hopper spill",
                RequestedAt = WholeSecondsNow(), RequestedById = userId,
            };
            db.ComplementaryOrders.Add(complementary);
            await db.SaveChangesAsync(Cancel);
            (orderNumber, complementaryNumber) = (order.Number, complementary.Number);
        });
        var returnNumber = await InsertPendingReturnAsync(app, itemId, 1m, userId);
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var salesOrderNumber = await CreateSalesOrderAsync(manager, (await CreateCustomerAsync(manager)).Id, (itemId, 5m));
        return new Links(orderNumber, complementaryNumber, returnNumber, salesOrderNumber);
    }

    /// <summary>
    /// (T-035) Adds the movements through <see cref="StockLedger.AddRangeAsync"/>, as one batch, in one write transaction
    /// of their own, as the ship does. If the ledger refuses one, none of them is saved.
    /// </summary>
    private static Task LedgerBatchAsync(TestApiFactory app, params Movement[] movements) =>
        InTransactionAsync(app, (_, ledger, _) => ledger.AddRangeAsync(movements, Cancel));

    /// <summary>Adds movements straight to the database, bypassing the ledger, to set up a stock history.</summary>
    private static async Task InsertMovementsAsync(TestApiFactory app, params Movement[] movements)
    {
        var user = await app.CreateUserAsync(Seed.UniqueEmail("history"), Roles.Manager);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Movement properties are init-only, so a copy is made with the user filled in.
        db.Movements.AddRange(movements.Select(movement => new Movement
        {
            Type = movement.Type, ItemId = movement.ItemId, WarehouseChange = movement.WarehouseChange,
            ProductionStorageChange = movement.ProductionStorageChange, OccurredAt = movement.OccurredAt, UserId = user.Id,
        }));
        await db.SaveChangesAsync(Cancel);
    }

    /// <summary>
    /// An item's stock in one location as the sum of its stored movements, read straight from the database. Not
    /// <see cref="OrderFlow.StockAsync"/>, which reads <c>GET /stock/{itemId}</c>: the ledger tests check what the ledger
    /// stored, without going through the stock endpoint they are not about. (The two helpers share a name; this class's
    /// own one wins inside it.)
    /// </summary>
    private static async Task<decimal> StockAsync(TestApiFactory app, int itemId, Location location)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var changes = await db.Movements.Where(movement => movement.ItemId == itemId)
            .Select(movement => location == Location.Warehouse ? movement.WarehouseChange : movement.ProductionStorageChange)
            .ToListAsync(Cancel);
        return changes.Sum() ?? 0m;
    }

    private static async Task<int> CountMovementsAsync(TestApiFactory app, int itemId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements
            .CountAsync(movement => movement.ItemId == itemId, Cancel);
    }

    private async Task<bool> UnitExistsAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Units.AnyAsync(unit => unit.Name == name, Cancel);
    }

    private static ProductionOrder NewOrder(int finishedProductId, string userId, OrderStatus status)
    {
        var now = WholeSecondsNow();
        return new ProductionOrder
        {
            FinishedProductId = finishedProductId, OrderedQuantity = 1000m, Status = status,
            CreatedAt = now, CreatedById = userId, StatusChangedAt = now,
            SentAt = status == OrderStatus.InProduction ? now : null,
            SentById = status == OrderStatus.InProduction ? userId : null,
        };
    }

    private static ProductionOrderLine SentLine(int lineNo, int materialId, decimal leftoverApplied, decimal warehouseSend, decimal complementarySends) => new()
    {
        LineNo = lineNo, RawMaterialId = materialId, AmountPerUnit = 1m, PlannedAmount = 1000m,
        LeftoverApplied = leftoverApplied, WarehouseSend = warehouseSend, CarriedLeftover = 0m, ComplementarySends = complementarySends,
    };

    /// <summary>
    /// Fills the database to the PRD's performance-target size with three set-based SQL statements (fast, and
    /// parameterised): 500 raw materials, 100,000 receipts of 1.5 spread evenly over them, and 1,000 Completed orders
    /// with one line each.
    /// </summary>
    private static async Task SeedVolumeAsync(TestApiFactory app, int unitId, int productId, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.InWriteTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
                INSERT INTO Items (Name, NameNormalized, Description, Type, UnitId, CreatedAt)
                SELECT 'perf-' || i, 'PERF-' || i, NULL, 'RawMaterial', {unitId}, '2026-11-11 08:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99999)
                INSERT INTO Movements (Type, ItemId, WarehouseChange, OccurredAt, UserId)
                SELECT 'Received', (SELECT MIN(Id) FROM Items WHERE Name LIKE 'perf-%') + (i % 500), '1.5', '2026-11-11 10:00:00', {userId} FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
                INSERT INTO ProductionOrders (FinishedProductId, OrderedQuantity, Status, CreatedAt, CreatedById, StatusChangedAt)
                SELECT {productId}, '10.0', 'Completed', '2026-11-11 09:00:00', {userId}, '2026-11-11 09:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ProductionOrderLines (OrderNumber, LineNo, RawMaterialId, AmountPerUnit, PlannedAmount)
                SELECT o.Number, 1, (SELECT MIN(Id) FROM Items WHERE Name LIKE 'perf-%') + (o.Number % 500), '1.0', '10.0'
                FROM ProductionOrders o
                """, Cancel);
        }, Cancel);
    }

    /// <summary>
    /// Gives the item stock in both locations through the ledger: a receipt of 2 × <paramref name="amount"/> and a send
    /// of <paramref name="amount"/> to Production storage (two movements).
    /// </summary>
    private async Task StockBothLocationsAsync(int itemId, string userId, Links links, decimal amount = 100m) =>
        await LedgerAsync(factory,
            Change(MovementType.Received, itemId, warehouse: 2 * amount, userId: userId),
            Change(MovementType.SentToProduction, itemId, warehouse: -amount, production: amount, userId: userId, links: links));

    /// <summary>
    /// A movement of <paramref name="type"/> with valid change columns (5 kg, D-2) and exactly the link and text fields
    /// named in <paramref name="fields"/> (comma-separated), so a test controls every field CheckLinks looks at.
    /// </summary>
    private static Movement WithFields(MovementType type, int itemId, string userId, Links links, string fields)
    {
        var set = fields.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        (decimal? warehouse, decimal? production) = type switch
        {
            MovementType.SentToProduction => ((decimal?)-5m, (decimal?)5m),
            MovementType.ConsumedInProduction => (null, -5m),
            MovementType.ReturnedToWarehouse => (5m, -5m),
            MovementType.ShippedToCustomer => (-5m, null),
            _ => (5m, null), // Received, ProducedOutput, and a Warehouse Adjustment
        };

        string? Text(string plain, string blank, string untrimmed, string tooLong, string value) =>
            set.Contains(plain) ? value
            : set.Contains(blank) ? "   "
            : set.Contains(untrimmed) ? $" {value} "
            : set.Contains(tooLong) ? new string('x', 501)
            : null;

        return new Movement
        {
            Type = type, ItemId = itemId, WarehouseChange = warehouse, ProductionStorageChange = production,
            OccurredAt = WholeSecondsNow(), UserId = userId,
            ReceiptDate = set.Contains("receiptDate") ? Day : null,
            ProductionOrderNumber = set.Contains("order") ? links.Order : null,
            ComplementaryOrderNumber = set.Contains("complementary") ? links.Complementary : null,
            ReturnNumber = set.Contains("return") ? links.Return : null,
            SalesOrderNumber = set.Contains("salesOrder") ? links.SalesOrder : null,
            Note = Text("note", "blankNote", "untrimmedNote", "longNote", "Delivery note 4471"),
            Reason = Text("reason", "blankReason", "untrimmedReason", "longReason", "Count correction"),
        };
    }

    /// <summary>Times 20 calls of <paramref name="path"/> after one warm-up call, and returns the times sorted, in ms.</summary>
    private static async Task<long[]> TimeAsync(HttpClient client, string path)
    {
        (await client.GetAsync(path, Cancel)).EnsureSuccessStatusCode();
        var times = new long[20];
        for (var i = 0; i < times.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync(path, Cancel);
            await response.Content.ReadAsStringAsync(Cancel);
            times[i] = watch.ElapsedMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Array.Sort(times);
        return times;
    }
}
