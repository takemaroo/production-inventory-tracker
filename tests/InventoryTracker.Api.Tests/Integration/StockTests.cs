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
/// (one scope stands for one request), inside <c>InWriteTransactionAsync</c> on that scope's DbContext. No endpoint takes stock away yet (adjustments arrive in T-015), so "refused" is
/// checked on the <see cref="ConflictException"/> itself; the exception handler that turns it into the <c>409</c> body
/// is tested in T-001.
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
    /// a raw material also shows 0 committed with no order, 0 pending and 0 free. Exactly the ten design fields. Every
    /// role may read it (PRD 4.2, AC-05.1).
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
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
        Assert.Equal(10, body.EnumerateObject().Count());

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
    /// committed order, pending returns and free leftover, which are about raw materials only (AC-10.3).
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

        int runningOrder;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var running = NewOrder(product.Id, user.Id, OrderStatus.InProduction);
            running.Lines.Add(SentLine(1, cheese.Id, leftoverApplied: 5m, warehouseSend: 995m, complementarySends: 20m));
            running.Lines.Add(SentLine(2, starch.Id, leftoverApplied: 0m, warehouseSend: 15m, complementarySends: 0m));
            var waiting = NewOrder(product.Id, user.Id, OrderStatus.Created);
            waiting.Lines.Add(new ProductionOrderLine { LineNo = 1, RawMaterialId = cheese.Id, AmountPerUnit = 1m, PlannedAmount = 1000m });
            db.ProductionOrders.AddRange(running, waiting);
            db.Returns.AddRange(
                NewReturn(cheese.Id, 10m, ReturnStatus.Pending, user.Id),
                NewReturn(cheese.Id, 5m, ReturnStatus.Pending, user.Id),
                NewReturn(cheese.Id, 7m, ReturnStatus.Received, user.Id),
                NewReturn(cheese.Id, 3m, ReturnStatus.Cancelled, user.Id));
            await db.SaveChangesAsync(Cancel);
            runningOrder = running.Number;
        }

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

        var pageTimes = await TimeAsync(reader, $"{StockPath}?pageSize=100");
        var itemTimes = await TimeAsync(reader, $"{StockPath}/{firstId}");

        // Written to the test's output (visible in a TRX report), so a slow machine can be spotted before it fails.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"GET /stock page of 100: p95 {pageTimes[18]} ms; GET /stock/{{id}}: p95 {itemTimes[18]} ms.");
        Assert.True(pageTimes[18] < 1000, $"GET /stock p95 was {pageTimes[18]} ms.");
        Assert.True(itemTimes[18] < 1000, $"GET /stock/{{id}} p95 was {itemTimes[18]} ms.");
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

        var refused = await Assert.ThrowsAsync<ConflictException>(() => InWriteTransactionAsync(factory, async (db, ledger) =>
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
        var first = Task.Run(() => InWriteTransactionAsync(factory, async (_, ledger) =>
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
                return InWriteTransactionAsync(factory, async (_, ledger) =>
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
    /// (<see cref="ArgumentException"/>, a <c>500</c> if it ever reached a client) and is not added.
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
    /// receipt with a note, a complementary send naming its parent order, a return linked to an order).
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

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => InWriteTransactionAsync(factory, async (db, ledger) =>
        {
            db.Movements.Add(Change(MovementType.Adjustment, item.Id, warehouse: -100m, userId: user));
            await ledger.AddAsync(Change(MovementType.Received, item.Id, warehouse: 1m, userId: user), Cancel);
        }));

        Assert.Contains("without StockLedger", refused.Message);
        Assert.Equal(0, await CountMovementsAsync(factory, item.Id));
        Assert.Equal(0m, await StockAsync(factory, item.Id, Location.Warehouse));
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
                    userId: user.Id, links: new Links(orderNumber, 0, 0)), Cancel);
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
    /// output, its return on ReturnedToWarehouse. Without <paramref name="userId"/> it has no user yet;
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
    };

    /// <summary>Real order, complementary-order and return numbers for movements to link to (the links are foreign keys).</summary>
    private sealed record Links(int Order, int Complementary, int Return);

    /// <summary>
    /// Creates a Created production order (for a new finished product), a PendingApproval complementary order under it,
    /// and a Pending return of <paramref name="itemId"/>, straight in the database. Created, not InProduction, because
    /// the shared database allows only one InProduction order; the ledger does not look at statuses.
    /// </summary>
    private static async Task<Links> CreateLinksAsync(TestApiFactory app, int itemId, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        var leftoverReturn = NewReturn(itemId, 1m, ReturnStatus.Pending, userId);
        db.ComplementaryOrders.Add(complementary);
        db.Returns.Add(leftoverReturn);
        await db.SaveChangesAsync(Cancel);
        return new Links(order.Number, complementary.Number, leftoverReturn.Number);
    }

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

    /// <summary>Runs <paramref name="work"/> the way a service does: in a fresh scope, inside one write transaction.</summary>
    private static async Task InWriteTransactionAsync(TestApiFactory app, Func<AppDbContext, StockLedger, Task> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
        await db.InWriteTransactionAsync(() => work(db, ledger), Cancel);
    }

    /// <summary>Adds the movements through the ledger, in order, in one write transaction.</summary>
    private static Task LedgerAsync(TestApiFactory app, params Movement[] movements) =>
        InWriteTransactionAsync(app, async (_, ledger) =>
        {
            foreach (var movement in movements)
            {
                await ledger.AddAsync(movement, Cancel);
            }
        });

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

    private static LeftoverReturn NewReturn(int itemId, decimal quantity, ReturnStatus status, string userId) => new()
    {
        ItemId = itemId, Quantity = quantity, Reason = "Not needed", Status = status,
        CreatedAt = WholeSecondsNow(), CreatedById = userId,
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
            Note = Text("note", "blankNote", "untrimmedNote", "longNote", "Delivery note 4471"),
            Reason = Text("reason", "blankReason", "untrimmedReason", "longReason", "Count correction"),
        };
    }

    /// <summary>
    /// An EF Core command interceptor (like <see cref="BeforeSaveInterceptor"/>, but called around each SQL command):
    /// once armed, it runs <c>work</c> the first time a command that <c>matches</c> has started returning rows, before
    /// the next query of the same request runs. Used to commit another request's write in the gap between two queries.
    /// </summary>
    private sealed class AfterFirstReadInterceptor(Func<DbCommand, bool> matches, Func<Task> work) : DbCommandInterceptor
    {
        private int _armed;
        private int _hasRun;

        /// <summary>True once <c>work</c> has run.</summary>
        public bool HasRun => Volatile.Read(ref _hasRun) == 1;

        /// <summary>From now on, the next matching command triggers <c>work</c> (once).</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <inheritdoc />
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            // The flag is set before work runs, so work's own commands (which pass through here too) do not re-enter.
            if (Volatile.Read(ref _armed) == 1 && matches(command) && Interlocked.Exchange(ref _hasRun, 1) == 0)
            {
                await work();
            }

            return result;
        }
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
