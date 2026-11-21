using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance test (phase 5) for the PRD section 5 performance target: "with 100,000 movements, 1,000 orders and 500
/// items in the database, 95% of requests answer within 1 second". The T-020 performance tests time
/// <c>GET /stock</c> and <c>GET /movements</c>; this one times the other reads people use all day (orders, an order's
/// detail with its live estimate, items, notifications) and the writes (receipts, adjustments, and whole order cycles of
/// create, send and complete), which architecture risk 3 says were never measured.
/// </summary>
/// <remarks>
/// It joins <see cref="PerformanceCollection"/>, so xUnit runs it alone after the parallel tests and the timings
/// measure the app, not the rest of the suite (decision #109a). Requests go through the in-process test server, as in
/// the T-020 tests, so the timings include the whole ASP.NET Core pipeline and SQLite, but not a network.
/// </remarks>
[Collection(nameof(PerformanceCollection))]
public class PerformanceAcceptanceTests
{
    private const int Samples = 20;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// PRD section 5. After filling the database to the target size, each request below is timed 20 times (after one
    /// warm-up) and the 19th fastest, the 95th percentile, must be under 1 second: the order list (all and filtered),
    /// a Created order's detail whose estimate sums a material with 200 movements, the item list, the notification list,
    /// a receipt, an adjustment, and each step of 20 order cycles (create, send, complete; each completion also raises an
    /// OutputVariance notification, so the notification list has rows to read).
    /// </summary>
    [Fact]
    public async Task PRD_5_order_item_notification_reads_and_writes_meet_the_target_at_full_volume()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var unit = await Seed.CreateUnitAsync(manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit);
        await SeedVolumeAsync(app, unit.Id, product.Id, managerUser.Id);
        var material = await FirstSeededMaterialAsync(app);
        await PutRecipeAsync(manager, product.Id, (material, 1m));
        var waiting = await CreateOrderAsync(manager, product.Id, 50m);

        var times = new Dictionary<string, long[]>
        {
            ["GET /production-orders"] = await TimeGetAsync(warehouse, "/api/v1/production-orders?pageSize=100"),
            ["GET /production-orders?status"] = await TimeGetAsync(warehouse, "/api/v1/production-orders?status=Completed&pageSize=100"),
            ["GET /production-orders/{n} (estimate)"] = await TimeGetAsync(warehouse, $"/api/v1/production-orders/{waiting}"),
            ["GET /items"] = await TimeGetAsync(warehouse, "/api/v1/items?pageSize=100"),
            ["POST /receipts"] = await TimeWritesAsync(i => manager.PostAsJsonAsync("/api/v1/receipts",
                new { itemId = material, quantity = 100m, receiptDate = "2026-11-01" }, Cancel), HttpStatusCode.Created),
            ["POST /adjustments"] = await TimeWritesAsync(i => manager.PostAsJsonAsync("/api/v1/adjustments",
                new { itemId = material, location = "Warehouse", quantity = -1m, reason = "Count" }, Cancel), HttpStatusCode.Created),
        };
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(manager, waiting, "Performance test")).StatusCode);

        var create = new long[Samples];
        var send = new long[Samples];
        var complete = new long[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var number = await CreateOrderAsync(manager, product.Id, 10m);
            create[i] = watch.ElapsedMilliseconds;

            watch.Restart();
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (material, 10m))).StatusCode);
            send[i] = watch.ElapsedMilliseconds;

            watch.Restart();
            Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(production, number, 11m, null, (material, 0m))).StatusCode);
            complete[i] = watch.ElapsedMilliseconds;
        }

        times["POST /production-orders"] = Sorted(create);
        times["POST /production-orders/{n}/send"] = Sorted(send);
        times["POST /production-orders/{n}/complete"] = Sorted(complete);
        times["GET /notifications"] = await TimeGetAsync(manager, "/api/v1/notifications?pageSize=100");

        // Written to the test output (visible in a TRX report), so a slow machine shows before it fails.
        foreach (var (request, sorted) in times)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{request}: p95 {sorted[Samples - 2]} ms, max {sorted[^1]} ms.");
        }

        Assert.All(times, pair => Assert.True(pair.Value[Samples - 2] < 1000, $"{pair.Key} p95 was {pair.Value[Samples - 2]} ms."));
    }

    /// <summary>
    /// Fills the database to the PRD's size with set-based SQL (fast and parameterised, like StockTests' seed, whose
    /// helper is private to that class): 500 raw materials, 100,000 receipts of 1.5 spread evenly over them (300 each in
    /// the Warehouse), and 1,000 Completed orders with one line each.
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
                SELECT 'qa-perf-' || i, 'QA-PERF-' || i, NULL, 'RawMaterial', {unitId}, '2026-11-11 08:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99999)
                INSERT INTO Movements (Type, ItemId, WarehouseChange, OccurredAt, UserId, ReceiptDate)
                SELECT 'Received', (SELECT MIN(Id) FROM Items WHERE Name LIKE 'qa-perf-%') + (i % 500), '1.5', '2026-11-11 10:00:00', {userId}, '2026-11-11' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
                INSERT INTO ProductionOrders (FinishedProductId, OrderedQuantity, Status, CreatedAt, CreatedById, StatusChangedAt)
                SELECT {productId}, '10.0', 'Completed', '2026-11-11 09:00:00', {userId}, '2026-11-11 09:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ProductionOrderLines (OrderNumber, LineNo, RawMaterialId, AmountPerUnit, PlannedAmount)
                SELECT o.Number, 1, (SELECT MIN(Id) FROM Items WHERE Name LIKE 'qa-perf-%') + (o.Number % 500), '1.0', '10.0'
                FROM ProductionOrders o
                """, Cancel);
        }, Cancel);
    }

    /// <summary>The id of the first seeded raw material; it has 200 receipt movements, 300 in the Warehouse.</summary>
    private static async Task<int> FirstSeededMaterialAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Items.Where(item => item.Name == "qa-perf-1").Select(item => item.Id).SingleAsync(Cancel);
    }

    /// <summary>Times 20 calls of <paramref name="path"/> after one warm-up call, and returns the times sorted, in ms.</summary>
    private static async Task<long[]> TimeGetAsync(HttpClient client, string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Cancel)).StatusCode);
        var times = new long[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync(path, Cancel);
            await response.Content.ReadAsStringAsync(Cancel);
            times[i] = watch.ElapsedMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return Sorted(times);
    }

    /// <summary>Times 20 writes made by <paramref name="write"/>, each checked for <paramref name="status"/>; sorted, in ms.</summary>
    private static async Task<long[]> TimeWritesAsync(Func<int, Task<HttpResponseMessage>> write, HttpStatusCode status)
    {
        var times = new long[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await write(i);
            await response.Content.ReadAsStringAsync(Cancel);
            times[i] = watch.ElapsedMilliseconds;
            Assert.Equal(status, response.StatusCode);
        }

        return Sorted(times);
    }

    private static long[] Sorted(long[] times)
    {
        Array.Sort(times);
        return times;
    }
}
