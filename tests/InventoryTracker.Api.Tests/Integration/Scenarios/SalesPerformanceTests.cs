using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;
using static InventoryTracker.Api.Tests.Integration.Scenarios.ScenarioApi;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// (v1.1) T-031, PRD sections 5 and 11, architecture 7 (v1.1): the performance target at the version 1.1 volume. With
/// 100,000 movements, 1,000 production orders, 500 items, 500 customers and 5,000 sales orders of 1 to 10 lines in the
/// database, 95% of requests answer within 1 second, the stock view with open demand and the shipped-totals reports
/// included.
/// </summary>
/// <remarks>
/// <para>
/// It joins <see cref="PerformanceCollection"/>, so xUnit runs it alone after the parallel tests and the timings
/// measure the app, not the rest of the suite (decision #109a). Requests go through the in-process test server, as in
/// the T-020 and phase 5 performance tests, so the timings include the whole ASP.NET Core pipeline and SQLite, but not a
/// network.
/// </para>
/// <para>
/// Only the seed writes straight to the database, with set-based SQL as the v1 performance seeds do: through the API
/// the 100,000 movements alone would take minutes. Every timed request goes through the API.
/// </para>
/// <para>
/// Why the writes are timed too (T-030 review): open demand is the fourth query of <c>StockQueries.ReadFiguresAsync</c>
/// (decision #159), so every write that reads stock figures (the production send and completion, the ship, and the
/// return instruction) now runs it inside its write transaction, holding SQLite's single write lock. The ship, the send
/// and the completion are timed at this volume, with materials that are on Created sales orders, so that query has rows
/// to sum.
/// </para>
/// </remarks>
[Collection(nameof(PerformanceCollection))]
public class SalesPerformanceTests
{
    /// <summary>Timed calls per request, after warm-up calls that are not counted (one by default; five for the AC-55 test, decision #223).</summary>
    private const int Samples = 20;

    /// <summary>
    /// (T-035, decision #223) Uncounted rounds before the AC-55 test's 20 timed ones. A freshly started .NET process
    /// first runs code through a quick, unoptimised compile and recompiles the busiest methods with full optimisation
    /// only after they have run many times ("tiered compilation"). A 500-line shipment's hot paths (EF Core's decimal sum
    /// callback, run once per movement summed, and the 500-row save) need several large shipments before they are fully
    /// optimised: the tech lead measured each round faster than the last for the first 5 to 20 rounds, and flat rounds
    /// with tiered compilation turned off. With one warm-up, the p95 measured how fast the compiler warms up, not the
    /// shipment of a server that has been running a while, which is what AC-55.1 describes.
    /// </summary>
    private const int WarmUpRounds = 5;

    /// <summary>PRD section 5's sizes.</summary>
    private const int ItemCount = 500, RawMaterialCount = 400, CustomerCount = 500, SalesOrderCount = 5000,
        OrderCount = 1000, MovementCount = 100_000;

    /// <summary>A sales order of the seed with 10 lines, Shipped (see <see cref="SeedVolumeAsync"/>).</summary>
    private const int TenLineSalesOrder = 509;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// PRD section 5 (v1.1), architecture 7 (v1.1). After filling the database to the v1.1 size (the counts are checked
    /// through the API first), each request below is called once to warm up and then timed 20 times; the 19th fastest,
    /// the 95th percentile, must be under 1 second:
    /// <list type="bullet">
    /// <item><c>GET /stock</c> (a page of 100 with open demand, raw materials only, a deep page) and
    /// <c>GET /stock/{itemId}</c>;</item>
    /// <item>both reports: all time, one month, each filter and the last page;</item>
    /// <item><c>GET /sales-orders</c> with each filter and a deep page, <c>GET /sales-orders/{number}</c> (10 lines),
    /// and <c>GET /customers</c>;</item>
    /// <item>the writes <c>POST /sales-orders</c> (5 lines), ship (5 lines) and cancel, and a production order's create,
    /// send and completion, whose material is on Created sales orders.</item>
    /// </list>
    /// The figures are still exact at this volume: one item's open demand equals its Created lines, and its shipped
    /// total equals its ShippedToCustomer movements (AC-31.1, AC-32.4). The timings are written to the test output.
    /// </summary>
    [Fact]
    public async Task PRD_5_v1_1_reads_and_writes_meet_the_target_at_the_v1_1_volume()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var unit = await Seed.CreateUnitAsync(manager);
        var volume = await SeedVolumeAsync(app, unit.Id, managerUser.Id);
        await AssertVolumeAsync(app, manager);
        await AssertFiguresAreExactAsync(app, manager, volume.Item(7));

        var times = await TimeReadsAsync(manager, volume);
        foreach (var (request, sorted) in await TimeSalesWritesAsync(manager, warehouse, volume))
        {
            times[request] = sorted;
        }

        foreach (var (request, sorted) in await TimeProductionWritesAsync(manager, warehouse, production, volume))
        {
            times[request] = sorted;
        }

        // Written to the test output (visible with -showLiveOutput or in a TRX report), so a slow machine shows before it
        // fails, and the task report can quote them.
        foreach (var (request, sorted) in times)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{request}: median {sorted[Samples / 2]} ms, p95 {sorted[Samples - 2]} ms, max {sorted[^1]} ms.");
        }

        Assert.All(times, pair => Assert.True(pair.Value[Samples - 2] < 1000, $"{pair.Key} p95 was {pair.Value[Samples - 2]} ms."));
    }

    /// <summary>
    /// (v1.2) AC-55.1, AC-55.2, SEC-24, T-035, PRD section 20 row 82: at the section 5 volume, shipping a Created sales
    /// order of 500 lines (the line cap, one line per seeded item) whose stock covers every line answers <c>200</c>
    /// within 1 second, and a receipt that another Warehouse user records while the shipment holds the write lock
    /// answers within 1 second too. <see cref="WarmUpRounds"/> uncounted rounds, then 20 timed ones; the 95th percentile
    /// of the 20 is asserted, as in the test above. Each shipment still writes exactly one movement per line (AC-55.3).
    /// Every round, warm-ups included, is written to the test output in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the receipt is timed (AC-55.2). SQLite lets one write transaction run at a time (architecture 5.2), so a long
    /// shipment makes every other save wait: before T-035, receipts waited up to 4.4 s behind a 500-line shipment. The
    /// receipt is started at the moment the shipment holds the lock, by <see cref="WhenTheShipReadsStock"/>: when the
    /// ship's transaction first reads <c>Movements</c> (its all-lines stock check), the receipt is sent. It then waits
    /// for the rest of the shipment and runs. Its time is measured from that moment to its answer.
    /// </para>
    /// <para>
    /// The receipt's times come in steps of about 150 ms: Microsoft.Data.Sqlite retries a busy lock every 150 ms, so a
    /// waiting save answers at the first retry after the shipment commits.
    /// </para>
    /// <para>
    /// Measured with this test on the owner's machine (in-process test server, as for every test here): before the
    /// batching, the shipment's p95 was 1,689 ms and the receipt's 1,714 ms (the test failed). With the batching, the
    /// covering index (decision #223) and five warm-ups, three runs on a quiet machine: shipment p95 436 to 490 ms
    /// (median 198 to 267), receipt p95 470 to 626 ms (median 316 to 326). Three runs while other programs kept about
    /// a third of the processor busy: shipment p95 675 to 711 ms, receipt p95 782 to 791 ms. In both, the timed rounds
    /// kept getting faster until about round 11.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AC_55_a_500_line_shipment_and_a_receipt_beside_it_answer_within_1_second_at_the_section_5_volume()
    {
        var output = TestContext.Current.TestOutputHelper;
        var whenShipReadsStock = new WhenTheShipReadsStock();
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(whenShipReadsStock)),
        };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (otherWarehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var unit = await Seed.CreateUnitAsync(manager);
        var volume = await SeedVolumeAsync(app, unit.Id, managerUser.Id);
        await AssertVolumeAsync(app, manager);

        // AC-55.1: 500 lines, the most a sales order may have (RequestLimits.MaxLines), one per seeded item. The seed
        // leaves every item well over 1,000 in the Warehouse, so 25 shipments of 1 each are always covered.
        var lines = Enumerable.Range(0, ItemCount).Select(index => (volume.Item(index), 1m)).ToArray();
        var receivedItem = volume.Item(0);
        var ship = new List<long>();
        var receipt = new List<long>();
        for (var round = 0; round < WarmUpRounds + Samples; round++)
        {
            var number = await CreateSalesOrderAsync(manager, volume.Customer(1), lines);

            // Armed for this shipment only. Task.Run gives the receipt its own thread, because SQLite waits for the write
            // lock by blocking the thread that asked for it, and that must not be the shipment's.
            Task<long>? receiptTime = null;
            whenShipReadsStock.Arm(() => receiptTime = Task.Run(() => TimeRequestAsync(
                () => otherWarehouse.PostAsJsonAsync($"{ApiPath}/receipts",
                    new { itemId = receivedItem, quantity = 100m, receiptDate = "2026-11-27" }, Cancel),
                HttpStatusCode.Created), Cancel));

            ship.Add(await TimeRequestAsync(() => ShipAsync(warehouse, number), HttpStatusCode.OK));

            Assert.True(receiptTime is not null, $"Round {round}: the shipment never read Movements inside its transaction.");
            receipt.Add(await receiptTime);
            output?.WriteLine($"Round {round + 1}{(round < WarmUpRounds ? " (warm-up)" : "")}: ship {ship[^1]} ms, receipt {receipt[^1]} ms.");

            // AC-55.3: still exactly one ShippedToCustomer movement per line.
            Assert.Equal(ItemCount, await TotalAsync(manager, $"{ApiPath}/movements?salesOrderNumber={number}"));
        }

        var times = new Dictionary<string, long[]>
        {
            ["POST /sales-orders/{n}/ship (500 lines)"] = WithoutWarmUp(ship, WarmUpRounds),
            ["POST /receipts while the 500-line shipment runs"] = WithoutWarmUp(receipt, WarmUpRounds),
        };
        foreach (var (request, sorted) in times)
        {
            output?.WriteLine(
                $"{request}: median {sorted[Samples / 2]} ms, p95 {sorted[Samples - 2]} ms, max {sorted[^1]} ms.");
        }

        Assert.All(times, pair => Assert.True(pair.Value[Samples - 2] < 1000, $"{pair.Key} p95 was {pair.Value[Samples - 2]} ms."));
    }

    // ---- The timed requests -----------------------------------------------------------------------------------------

    /// <summary>
    /// Architecture 7 (v1.1), the reads: stock with open demand, both reports, the sales order list and detail, and the
    /// customer list. Each list read asks for pages of 100, the maximum (design 2.5). The deep pages are the last page of
    /// each report and page 50 of the sales orders, so the offset is large.
    /// </summary>
    private static async Task<Dictionary<string, long[]>> TimeReadsAsync(HttpClient reader, Volume volume)
    {
        var customer = volume.Customer(1);
        var item = volume.Item(7);
        var month = "from=2026-06-01&to=2026-06-30";
        var lastItemPage = await LastPageAsync(reader, ReportPath("shipped-by-item"));
        var lastCustomerPage = await LastPageAsync(reader, ReportPath("shipped-by-customer"));

        return new Dictionary<string, long[]>
        {
            ["GET /stock (100, with open demand)"] = await TimeGetAsync(reader, $"{ApiPath}/stock?pageSize=100"),
            ["GET /stock?itemType=RawMaterial (100)"] = await TimeGetAsync(reader, $"{ApiPath}/stock?itemType=RawMaterial&pageSize=100"),
            ["GET /stock, page 5 of 100"] = await TimeGetAsync(reader, $"{ApiPath}/stock?pageSize=100&page=5"),
            ["GET /stock/{itemId}"] = await TimeGetAsync(reader, $"{ApiPath}/stock/{item}"),

            ["GET /reports/shipped-by-item, all time"] = await TimeGetAsync(reader, ReportPath("shipped-by-item", "pageSize=100")),
            ["GET /reports/shipped-by-item, one month"] = await TimeGetAsync(reader, ReportPath("shipped-by-item", $"{month}&pageSize=100")),
            ["GET /reports/shipped-by-item?itemType"] = await TimeGetAsync(reader, ReportPath("shipped-by-item", "itemType=FinishedProduct&pageSize=100")),
            ["GET /reports/shipped-by-item?customerId"] = await TimeGetAsync(reader, ReportPath("shipped-by-item", $"customerId={customer}&pageSize=100")),
            [$"GET /reports/shipped-by-item, last page ({lastItemPage})"] =
                await TimeGetAsync(reader, ReportPath("shipped-by-item", $"page={lastItemPage}&pageSize=100")),

            ["GET /reports/shipped-by-customer, all time"] = await TimeGetAsync(reader, ReportPath("shipped-by-customer", "pageSize=100")),
            ["GET /reports/shipped-by-customer, one month"] = await TimeGetAsync(reader, ReportPath("shipped-by-customer", $"{month}&pageSize=100")),
            ["GET /reports/shipped-by-customer?customerId"] = await TimeGetAsync(reader, ReportPath("shipped-by-customer", $"customerId={customer}&pageSize=100")),
            ["GET /reports/shipped-by-customer?itemId"] = await TimeGetAsync(reader, ReportPath("shipped-by-customer", $"itemId={item}&pageSize=100")),
            [$"GET /reports/shipped-by-customer, last page ({lastCustomerPage})"] =
                await TimeGetAsync(reader, ReportPath("shipped-by-customer", $"page={lastCustomerPage}&pageSize=100")),

            ["GET /sales-orders"] = await TimeGetAsync(reader, $"{SalesOrdersPath}?pageSize=100"),
            ["GET /sales-orders?status=Created"] = await TimeGetAsync(reader, $"{SalesOrdersPath}?status=Created&pageSize=100"),
            ["GET /sales-orders?customerId"] = await TimeGetAsync(reader, $"{SalesOrdersPath}?customerId={customer}&pageSize=100"),
            ["GET /sales-orders?itemId"] = await TimeGetAsync(reader, $"{SalesOrdersPath}?itemId={item}&pageSize=100"),
            ["GET /sales-orders, page 50 of 100"] = await TimeGetAsync(reader, $"{SalesOrdersPath}?pageSize=100&page=50"),
            ["GET /sales-orders/{number} (10 lines)"] = await TimeGetAsync(reader, $"{SalesOrdersPath}/{TenLineSalesOrder}"),

            ["GET /customers"] = await TimeGetAsync(reader, $"{CustomersPath}?pageSize=100"),
            ["GET /customers?isActive=false"] = await TimeGetAsync(reader, $"{CustomersPath}?isActive=false&pageSize=100"),
        };
    }

    /// <summary>
    /// Architecture 7 (v1.1), the sales writes, 21 rounds (the first is the warm-up): create a sales order of 5 lines
    /// (one of each of five seeded raw materials), ship it, then create a second one and cancel it. Only the create, the
    /// ship and the cancel are timed. Each ship takes 1 of each material, well within the seeded stock.
    /// </summary>
    private static async Task<Dictionary<string, long[]>> TimeSalesWritesAsync(HttpClient manager, HttpClient warehouse, Volume volume)
    {
        var lines = Enumerable.Range(100, 5).Select(index => (volume.Item(index), 1m)).ToArray();
        var create = new List<long>();
        var ship = new List<long>();
        var cancel = new List<long>();
        for (var round = 0; round <= Samples; round++)
        {
            var watch = Stopwatch.StartNew();
            var number = await CreateSalesOrderAsync(manager, volume.Customer(1), lines);
            create.Add(watch.ElapsedMilliseconds);

            ship.Add(await TimeRequestAsync(() => ShipAsync(warehouse, number), HttpStatusCode.OK));

            var toCancel = await CreateSalesOrderAsync(manager, volume.Customer(1), lines);
            cancel.Add(await TimeRequestAsync(() => CancelSalesOrderAsync(manager, toCancel), HttpStatusCode.OK));
        }

        return new Dictionary<string, long[]>
        {
            ["POST /sales-orders (5 lines)"] = WithoutWarmUp(create),
            ["POST /sales-orders/{n}/ship (5 lines)"] = WithoutWarmUp(ship),
            ["POST /sales-orders/{n}/cancel"] = WithoutWarmUp(cancel),
        };
    }

    /// <summary>
    /// The production writes that read stock figures, and so open demand, inside their write transaction: 21 cycles (the
    /// first is the warm-up) of create, send and complete an order of 10 of a seeded finished product, whose recipe uses
    /// two seeded raw materials (1 and 0.5 per unit). Both materials are on Created sales orders.
    /// </summary>
    private static async Task<Dictionary<string, long[]>> TimeProductionWritesAsync(
        HttpClient manager, HttpClient warehouse, HttpClient production, Volume volume)
    {
        var (first, second) = (volume.Item(200), volume.Item(201));
        var product = volume.Item(RawMaterialCount + 1);
        await PutRecipeAsync(manager, product, (first, 1m), (second, 0.5m));
        var create = new List<long>();
        var send = new List<long>();
        var complete = new List<long>();
        for (var round = 0; round <= Samples; round++)
        {
            var watch = Stopwatch.StartNew();
            var number = await CreateOrderAsync(manager, product, 10m);
            create.Add(watch.ElapsedMilliseconds);

            send.Add(await TimeRequestAsync(() => SendAsync(warehouse, number, (first, 10m), (second, 5m)), HttpStatusCode.OK));
            complete.Add(await TimeRequestAsync(
                () => CompleteAsync(production, number, 10m, reason: null, (first, 0m), (second, 0m)), HttpStatusCode.OK));
        }

        return new Dictionary<string, long[]>
        {
            ["POST /production-orders"] = WithoutWarmUp(create),
            ["POST /production-orders/{n}/send (2 lines)"] = WithoutWarmUp(send),
            ["POST /production-orders/{n}/complete (2 lines)"] = WithoutWarmUp(complete),
        };
    }

    /// <summary>Times 20 calls of <paramref name="path"/> after one warm-up call; returns the times sorted, in ms.</summary>
    private static async Task<long[]> TimeGetAsync(HttpClient client, string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Cancel)).StatusCode);
        var times = new List<long>();
        for (var i = 0; i < Samples; i++)
        {
            times.Add(await TimeRequestAsync(() => client.GetAsync(path, Cancel), HttpStatusCode.OK));
        }

        times.Sort();
        return [.. times];
    }

    /// <summary>Times one request, including reading the whole response body, and checks its status; in ms.</summary>
    private static async Task<long> TimeRequestAsync(Func<Task<HttpResponseMessage>> request, HttpStatusCode status)
    {
        var watch = Stopwatch.StartNew();
        var response = await request();
        var body = await response.Content.ReadAsStringAsync(Cancel);
        var elapsed = watch.ElapsedMilliseconds;
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        return elapsed;
    }

    /// <summary>
    /// The <see cref="Samples"/> timed rounds, sorted, after the first <paramref name="warmUps"/> uncounted ones (one, the
    /// first round, unless a test says otherwise).
    /// </summary>
    private static long[] WithoutWarmUp(List<long> times, int warmUps = 1)
    {
        Assert.Equal(warmUps + Samples, times.Count);
        return [.. times.Skip(warmUps).Order()];
    }

    /// <summary>The number of the last page of 100 rows of a list, from its <c>totalCount</c>.</summary>
    private static async Task<int> LastPageAsync(HttpClient reader, string path)
    {
        var total = (await GetAsync(reader, $"{path}?pageSize=1")).GetProperty("totalCount").GetInt32();
        Assert.True(total > 100, $"{path} has only {total} rows; the last page would not be deep.");
        return (total + 99) / 100;
    }

    // ---- The volume -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills the database to PRD section 5's v1.1 size with set-based SQL, in one write transaction. Parameterised
    /// (<c>ExecuteSqlAsync</c> turns each <c>{value}</c> into a SQL parameter, never pasted text):
    /// <list type="bullet">
    /// <item>500 items, <c>sales-perf-item-001</c> to <c>-500</c>: the first 400 raw materials, the last 100 finished
    /// products, all in one unit;</item>
    /// <item>500 customers, every tenth deactivated;</item>
    /// <item>5,000 sales orders, created every 105 minutes from 2025-12-01, so over a year. Sales order <c>i</c> has
    /// <c>1 + i % 10</c> lines (1 to 10, 27,500 in all), each a different item, of 1 to 10 units. Its customer is
    /// <c>i % 500</c> and its status digit <c>(i / 500 + i) % 10</c>: 0 to 7 Shipped a day after creation, 8 Created,
    /// 9 Cancelled two hours after. So every customer has ten sales orders (eight Shipped, one Created, one Cancelled),
    /// and the whole set is 80% Shipped;</item>
    /// <item>one ShippedToCustomer movement per line of a Shipped sales order, at its shipment time, linked to it, as
    /// the ship writes them (architecture 5.3), then opening stock (receipts of 10 for raw materials, count adjustments
    /// of 10 for finished products, which cannot be received) up to 100,000 movements in all. Every item ends with far
    /// more stock than was shipped;</item>
    /// <item>1,000 Completed production orders of one line each, as the v1 seed.</item>
    /// </list>
    /// </summary>
    private static async Task<Volume> SeedVolumeAsync(TestApiFactory app, int unitId, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var volume = new Volume(0, 0);
        await db.InWriteTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {ItemCount})
                INSERT INTO Items (Name, NameNormalized, Description, Type, UnitId, CreatedAt)
                SELECT printf('sales-perf-item-%03d', i), printf('SALES-PERF-ITEM-%03d', i), NULL,
                       CASE WHEN i <= {RawMaterialCount} THEN 'RawMaterial' ELSE 'FinishedProduct' END, {unitId},
                       '2025-11-01 08:00:00'
                FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {CustomerCount})
                INSERT INTO Customers (Name, NameNormalized, Notes, IsActive, CreatedAt, CreatedById)
                SELECT printf('Customer %03d', i), printf('CUSTOMER %03d', i), 'Deliveries before 10:00',
                       CASE WHEN i % 10 = 0 THEN 0 ELSE 1 END, datetime('2025-11-01 08:00:00', printf('+%d minutes', i)),
                       {userId}
                FROM n
                """, Cancel);
            volume = new Volume(
                await db.Items.Where(item => item.Name == "sales-perf-item-001").Select(item => item.Id).SingleAsync(Cancel),
                await db.Customers.Where(customer => customer.Name == "Customer 001").Select(customer => customer.Id).SingleAsync(Cancel));

            // Sales order i's customer is i % 500 (0 is the first customer). The CTE "o" works out each sales order's
            // creation time and status once, so the INSERT can use them in several columns.
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {SalesOrderCount}),
                o AS (
                    SELECT i, datetime('2025-12-01 08:00:00', printf('+%d minutes', (i - 1) * 105)) AS created,
                           CASE WHEN (i / 500 + i) % 10 < 8 THEN 'Shipped'
                                WHEN (i / 500 + i) % 10 = 8 THEN 'Created' ELSE 'Cancelled' END AS status
                    FROM n)
                INSERT INTO SalesOrders (CustomerId, CustomerReference, Status, CreatedAt, CreatedById, ShippedAt, ShippedById,
                                         CancelledAt, CancelledById, CancellationReason, StatusChangedAt)
                SELECT {volume.FirstCustomer} + (i % {CustomerCount}), printf('PO-%05d', i), status, created, {userId},
                       CASE WHEN status = 'Shipped' THEN datetime(created, '+1 day') END,
                       CASE WHEN status = 'Shipped' THEN {userId} END,
                       CASE WHEN status = 'Cancelled' THEN datetime(created, '+2 hours') END,
                       CASE WHEN status = 'Cancelled' THEN {userId} END,
                       CASE WHEN status = 'Cancelled' THEN 'Customer changed the order' END,
                       CASE status WHEN 'Shipped' THEN datetime(created, '+1 day')
                                   WHEN 'Cancelled' THEN datetime(created, '+2 hours') ELSE created END
                FROM o
                ORDER BY i
                """, Cancel);

            // Line j (0 to 9) of sales order s: item (7s + 37j) % 500, distinct for the ten j of one order, so no
            // sales order has an item twice (the unique index). Quantities are written as EF Core writes decimals ("3.0").
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE k(j) AS (SELECT 0 UNION ALL SELECT j + 1 FROM k WHERE j < 9)
                INSERT INTO SalesOrderLines (SalesOrderNumber, LineNo, ItemId, Quantity)
                SELECT s.Number, k.j + 1, {volume.FirstItem} + ((s.Number * 7 + k.j * 37) % {ItemCount}),
                       printf('%d.0', 1 + (s.Number + k.j) % 10)
                FROM SalesOrders s JOIN k ON k.j <= s.Number % 10
                ORDER BY s.Number, k.j
                """, Cancel);

            // Opening stock first (the lower ids, a day before the first shipment), then the shipments in time order.
            var shipments = await db.SalesOrderLines.CountAsync(line => line.SalesOrder.Status == SalesOrderStatus.Shipped, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {MovementCount - shipments - 1})
                INSERT INTO Movements (Type, ItemId, WarehouseChange, OccurredAt, UserId, ReceiptDate, Reason)
                SELECT CASE WHEN i % {ItemCount} < {RawMaterialCount} THEN 'Received' ELSE 'Adjustment' END,
                       {volume.FirstItem} + (i % {ItemCount}), '10.0', '2025-11-30 08:00:00', {userId},
                       CASE WHEN i % {ItemCount} < {RawMaterialCount} THEN '2025-11-30' END,
                       CASE WHEN i % {ItemCount} < {RawMaterialCount} THEN NULL ELSE 'Opening stock count' END
                FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO Movements (Type, ItemId, WarehouseChange, OccurredAt, UserId, SalesOrderNumber)
                SELECT 'ShippedToCustomer', l.ItemId, '-' || l.Quantity, s.ShippedAt, s.ShippedById, s.Number
                FROM SalesOrderLines l JOIN SalesOrders s ON s.Number = l.SalesOrderNumber
                WHERE s.Status = 'Shipped'
                ORDER BY s.ShippedAt, l.LineNo
                """, Cancel);

            // 1,000 Completed production orders of the first finished product, one line each, as the v1 seed.
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {OrderCount})
                INSERT INTO ProductionOrders (FinishedProductId, OrderedQuantity, Status, CreatedAt, CreatedById, StatusChangedAt)
                SELECT {volume.Item(RawMaterialCount)}, '10.0', 'Completed', '2025-12-01 09:00:00', {userId}, '2025-12-01 09:00:00'
                FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ProductionOrderLines (OrderNumber, LineNo, RawMaterialId, AmountPerUnit, PlannedAmount)
                SELECT o.Number, 1, {volume.FirstItem} + (o.Number % {RawMaterialCount}), '1.0', '10.0'
                FROM ProductionOrders o
                """, Cancel);
        }, Cancel);
        return volume;
    }

    /// <summary>
    /// PRD section 5 (v1.1): the database really is the target size, read through the API (as a Manager, who can read
    /// every list): 500 items, 500 customers, 5,000 sales orders (4,000 Shipped, 500 Created, 500 Cancelled), 1,000
    /// production orders and 100,000 movements. From the database: every sales order has 1 to 10 lines, both ends
    /// present, and the shipments span almost a year.
    /// </summary>
    private static async Task AssertVolumeAsync(TestApiFactory app, HttpClient manager)
    {
        Assert.Equal(ItemCount, await TotalAsync(manager, $"{ApiPath}/stock"));
        Assert.Equal(CustomerCount, await TotalAsync(manager, CustomersPath));
        Assert.Equal(SalesOrderCount, await TotalAsync(manager, SalesOrdersPath));
        Assert.Equal(4000, await TotalAsync(manager, $"{SalesOrdersPath}?status=Shipped"));
        Assert.Equal(500, await TotalAsync(manager, $"{SalesOrdersPath}?status=Created"));
        Assert.Equal(500, await TotalAsync(manager, $"{SalesOrdersPath}?status=Cancelled"));
        Assert.Equal(OrderCount, await TotalAsync(manager, OrdersPath));
        Assert.Equal(MovementCount, await TotalAsync(manager, $"{ApiPath}/movements"));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lineCounts = await db.SalesOrders.Select(order => order.Lines.Count).ToListAsync(Cancel);
        Assert.Equal((1, 10), (lineCounts.Min(), lineCounts.Max()));
        Assert.Equal(27_500, lineCounts.Sum());
        var shippedAt = await db.SalesOrders.Where(order => order.ShippedAt != null).Select(order => order.ShippedAt!.Value).ToListAsync(Cancel);
        Assert.True(shippedAt.Max() - shippedAt.Min() > TimeSpan.FromDays(360), "The shipments must spread over a year.");
    }

    /// <summary>
    /// AC-31.1, D-12, AC-32.4 at volume, so the timed reads are timing right answers: the item's open demand through
    /// <c>GET /stock/{itemId}</c> equals the sum of its lines on Created sales orders (read from the database and summed
    /// here), and its all-time row in the by-item report equals its ShippedToCustomer movements through
    /// <c>GET /movements</c>, with the sign reversed, and their count.
    /// </summary>
    private static async Task AssertFiguresAreExactAsync(TestApiFactory app, HttpClient manager, int itemId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = await db.SalesOrderLines
            .Where(line => line.ItemId == itemId && line.SalesOrder.Status == SalesOrderStatus.Created)
            .Select(line => line.Quantity)
            .ToListAsync(Cancel);
        Assert.NotEmpty(created);
        Assert.Equal(created.Sum(), await OpenDemandAsync(manager, itemId));

        var shipments = await AllRowsAsync(manager, $"{ApiPath}/movements?itemId={itemId}&type=ShippedToCustomer");
        var row = (await ShippedByItemAsync(manager, "pageSize=100")).GetProperty("items").EnumerateArray()
            .Single(candidate => candidate.GetProperty("itemId").GetInt32() == itemId);
        Assert.Equal(-shipments.Sum(movement => movement.GetProperty("warehouseChange").GetDecimal()),
            row.GetProperty("shippedQuantity").GetDecimal());
        Assert.Equal(shipments.Count, row.GetProperty("salesOrderCount").GetInt32());
    }

    /// <summary>The <c>totalCount</c> of a list (design 2.5), asking for one row.</summary>
    private static async Task<int> TotalAsync(HttpClient reader, string path) =>
        (await GetAsync(reader, $"{path}{(path.Contains('?') ? '&' : '?')}pageSize=1")).GetProperty("totalCount").GetInt32();

    /// <summary>
    /// (T-035) An EF Core command interceptor for the AC-55.2 test: once armed, the next command inside a transaction that
    /// reads <c>Movements</c> (the ship's stock check, while it holds the write lock) runs the armed action, once. Unlike
    /// <see cref="AfterFirstReadInterceptor"/>, it can be armed again for every round, and it only <em>starts</em> the
    /// other request: waiting for it here would deadlock, because the other request waits for this one's lock.
    /// </summary>
    private sealed class WhenTheShipReadsStock : DbCommandInterceptor
    {
        private Action? _armed;

        /// <summary>The next stock read inside a transaction runs <paramref name="start"/>.</summary>
        public void Arm(Action start) => Volatile.Write(ref _armed, start);

        /// <inheritdoc />
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            // Interlocked.Exchange takes the action and disarms in one step, so it runs exactly once.
            if (command.Transaction is not null
                && command.CommandText.Contains("\"Movements\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, null) is { } start)
            {
                start();
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Where the seed's rows start: the id of <c>sales-perf-item-001</c> and of <c>Customer 001</c>. The seed's ids are
    /// consecutive, because each table is filled by one statement.
    /// </summary>
    private sealed record Volume(int FirstItem, int FirstCustomer)
    {
        /// <summary>The id of seeded item <paramref name="index"/>, counting from 0 (0 to 399 raw materials, 400 to 499 finished products).</summary>
        public int Item(int index) => FirstItem + index;

        /// <summary>The id of seeded customer <paramref name="index"/>, counting from 0 (every tenth from <c>Customer 010</c> is inactive).</summary>
        public int Customer(int index) => FirstCustomer + index;
    }
}
