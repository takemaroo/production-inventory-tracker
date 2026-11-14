using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.ProductionOrders;
using InventoryTracker.Api.Features.Stock;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-009: creating and viewing production orders (<c>POST /api/v1/production-orders</c>, <c>GET /api/v1/production-orders</c>,
/// <c>GET /api/v1/production-orders/{number}</c>; US-13, US-14, AC-08.4, AC-15.5 estimates; design 5.7).
/// </summary>
/// <remarks>
/// <para>
/// Orders are created through the API. Orders in later statuses (InProduction, Completed, Cancelled) and complementary
/// orders are inserted or changed directly through the DbContext, because the endpoints that create them come in later
/// tasks (T-010, T-011, T-016, T-017). Free leftover in production storage is put there through <see cref="StockLedger"/>,
/// the only writer of movements.
/// </para>
/// <para>
/// Tests in this class share one database and each uses its own items, so their leftover allocations never meet. A test
/// that needs a fake clock, exact order numbers or an InProduction order (the database allows only one) creates its own
/// factory.
/// </para>
/// </remarks>
public class ProductionOrdersTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>Decision #80, design 7.2, typed out (not taken from the API) so a wrong message fails the test.</summary>
    private const string OrderedQuantityAboveMax = "Ordered quantity must be 999,999,999.999 or less.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-13.1, AC-13.2, AC-14.2: create, and the full detail -------------------------------------------------

    /// <summary>
    /// AC-13.1, AC-13.2, AC-14.2, AC-05.3, design 5.7 (the "Order detail while Created" example, AC-15.3): a Manager orders
    /// 1,000 bags of a product whose recipe is 1 kg of Cheese and 0.015 kg of Potato starch per bag, with 5 kg of free Cheese
    /// leftover in production storage. <c>201</c> with <c>Location</c>; the order is Created, records its creator and time,
    /// and has one line per recipe line, in recipe order, with the amount copied and planned 1,000 kg and 15 kg. The
    /// estimates are exactly the design's: Cheese leftover 5, nominal 995, range 975 to 1,015; starch 0, 15, 14.7 to 15.3;
    /// tolerance 2. Every design field is present, in the design's order, with <c>null</c> for values not known yet.
    /// <c>GET</c> returns exactly the same body.
    /// </summary>
    [Fact]
    public async Task Manager_creates_an_order_with_the_design_example_detail()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(EightAm) };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag);
        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1m), (starch.Id, 0.015m));
        await PutLeftoverAsync(app, cheese.Id, 5m, managerUser.Id);

        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = product.Id, orderedQuantity = 1000 }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        var number = order.GetProperty("number").GetInt32();
        Assert.Equal($"{OrdersPath}/{number}", response.Headers.Location?.AbsolutePath);

        Assert.Equal(
            ["number", "finishedProductId", "finishedProductName", "unit", "orderedQuantity", "status", "createdAt", "createdBy",
             "producedQuantity", "outputVariance", "outputVariancePercent", "toleranceAtCompletionPercent", "completionReason",
             "cancellationReason", "lines", "complementaryOrders", "history"],
            order.EnumerateObject().Select(field => field.Name));
        Assert.Equal(product.Id, order.GetProperty("finishedProductId").GetInt32());
        Assert.Equal(product.Name, order.GetProperty("finishedProductName").GetString());
        Assert.Equal("Bag", order.GetProperty("unit").GetString());
        Assert.Equal(1000m, order.GetProperty("orderedQuantity").GetDecimal());
        Assert.Equal("Created", order.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T08:00:00Z", order.GetProperty("createdAt").GetString());
        AssertPerson(order.GetProperty("createdBy"), managerUser);
        foreach (var field in new[] { "producedQuantity", "outputVariance", "outputVariancePercent",
                     "toleranceAtCompletionPercent", "completionReason", "cancellationReason" })
        {
            Assert.Equal(JsonValueKind.Null, order.GetProperty(field).ValueKind);
        }

        var lines = order.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([cheese.Id, starch.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
        AssertCreatedLine(lines[0], cheese, amountPerUnit: 1m, planned: 1000m);
        AssertCreatedLine(lines[1], starch, amountPerUnit: 0.015m, planned: 15m);
        AssertEstimate(lines[0], leftover: 5m, nominal: 995m, min: 975m, max: 1015m);
        AssertEstimate(lines[1], leftover: 0m, nominal: 15m, min: 14.7m, max: 15.3m);

        Assert.Equal(0, order.GetProperty("complementaryOrders").GetArrayLength());
        var history = Assert.Single(order.GetProperty("history").EnumerateArray());
        Assert.Equal(["status", "at", "by", "note"], history.EnumerateObject().Select(field => field.Name));
        Assert.Equal("Created", history.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T08:00:00Z", history.GetProperty("at").GetString());
        AssertPerson(history.GetProperty("by"), managerUser);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("note").ValueKind);

        // AC-14.2: any role reads the same detail; nothing changed, so the estimate is the same too.
        var reader = await app.CreateClientAsAsync(Roles.Warehouse);
        var read = await ApiJson.ReadAsync(await reader.GetAsync(response.Headers.Location, Cancel));
        Assert.Equal(order.GetRawText(), read.GetRawText());
    }

    /// <summary>
    /// AC-13.4: creating an order moves no stock. No movement is added, and the materials' stock is unchanged.
    /// </summary>
    [Fact]
    public async Task Creating_an_order_creates_no_movement()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        var movementsBefore = await CountAsync(factory, db => db.Movements);

        await CreateOrderBodyAsync(manager, product.Id, 500m);

        Assert.Equal(movementsBefore, await CountAsync(factory, db => db.Movements));
        var cheeseStock = await ApiJson.ReadAsync(await manager.GetAsync($"/api/v1/stock/{cheese.Id}", Cancel));
        Assert.Equal(1000m, cheeseStock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, cheeseStock.GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-13.5: an order can be created while another order is InProduction, and several orders can be Created at once.
    /// D-4 inside the estimate: material committed to the running order is not free leftover. Production storage holds
    /// 1,030 kg of Cheese and the running order has 1,000 kg committed, so only 30 kg is free, and a new order planning
    /// 30 kg gets an estimate of leftover 30, nominal 0, range 0 to 0.6 (the AC-15.4 figures).
    /// </summary>
    [Fact]
    public async Task Orders_can_be_created_while_another_is_in_production()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await PutLeftoverAsync(app, cheese.Id, 1030m, managerUser.Id);
        await InsertOrderAsync(app, product.Id, managerUser.Id, OrderStatus.InProduction,
            SentLine(1, cheese.Id, leftoverApplied: 0m, warehouseSend: 1000m));

        var first = await CreateOrderBodyAsync(manager, product.Id, 30m);
        var second = await CreateOrderBodyAsync(manager, product.Id, 30m);

        Assert.Equal("Created", first.GetProperty("status").GetString());
        Assert.Equal("Created", second.GetProperty("status").GetString());
        AssertEstimate(Line(first, cheese.Id), leftover: 30m, nominal: 0m, min: 0m, max: 0.6m);

        // D-5: the 30 kg is allocated to the older Created order, so the younger one gets none.
        AssertEstimate(Line(second, cheese.Id), leftover: 0m, nominal: 30m, min: 29.4m, max: 30.6m);
    }

    // ---- AC-08.4: the recipe snapshot ---------------------------------------------------------------------------

    /// <summary>
    /// AC-08.4 (the PRD's example): order #1 is created for 1,000 bags at 1 kg per bag; the recipe is then changed to
    /// 1.01 kg per bag. The order still shows amount per unit 1 and 1,000 kg planned, and the stored line still holds
    /// amount per unit 1, so completing it with 1,000 bags gives RequiredForActualOutput 1,000 kg (D-9 reads the line's
    /// copy, checked here with the same T-004 function completion will use). A new order for 1,000 bags plans 1,010 kg.
    /// </summary>
    [Fact]
    public async Task Changing_the_recipe_does_not_change_existing_orders()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var oldOrder = await CreateOrderBodyAsync(manager, product.Id, 1000m);
        var oldNumber = oldOrder.GetProperty("number").GetInt32();

        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1.01m));

        var reread = await ApiJson.ReadAsync(await manager.GetAsync($"{OrdersPath}/{oldNumber}", Cancel));
        Assert.Equal(1m, Line(reread, cheese.Id).GetProperty("amountPerUnit").GetDecimal());
        Assert.Equal(1000m, Line(reread, cheese.Id).GetProperty("plannedAmount").GetDecimal());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storedLine = await db.ProductionOrderLines.AsNoTracking()
                .SingleAsync(line => line.OrderNumber == oldNumber, Cancel);
            Assert.Equal(1m, storedLine.AmountPerUnit);

            // As if the order had been sent with 1,000 kg and completed with 1,000 bags and no leftover.
            storedLine.LeftoverApplied = 0m;
            storedLine.WarehouseSend = 1000m;
            storedLine.ComplementarySends = 0m;
            storedLine.CarriedLeftover = 0m;
            var completion = ProductionCalculations.CompleteLine(storedLine, producedQuantity: 1000m, leftoverEntered: 0m, tolerancePercent: 2m);
            Assert.Equal(1000m, completion.RequiredForActualOutput);
        }

        var newOrder = await CreateOrderBodyAsync(manager, product.Id, 1000m);
        Assert.Equal(1.01m, Line(newOrder, cheese.Id).GetProperty("amountPerUnit").GetDecimal());
        Assert.Equal(1010m, Line(newOrder, cheese.Id).GetProperty("plannedAmount").GetDecimal());
    }

    // ---- AC-15.5, AC-13.2: the leftover estimate, oldest order first ------------------------------------------------

    /// <summary>
    /// AC-15.5 (estimate bullet), D-5, D-7, through the API. Production storage holds 50 kg of free Cheese, put there by a
    /// receipt and a send (a ledger movement) for an earlier order that was then cancelled. Orders #7 (created 08:00,
    /// 30 bags), #8 (09:00, 1,000 bags) and #9 (10:00, 500 bags) are Created, at 1 kg of Cheese per bag. Estimates, exactly
    /// as in the PRD: #7 leftover 30, nominal 0, range 0 to 0.6; #8 leftover 20, nominal 980, range 960 to 1,000; #9
    /// leftover 0, nominal 500, range 490 to 510.
    /// Then AC-15.5's third bullet and AC-21.2 with AC-13.2 (recalculated on every read): when #7 is cancelled, the next read
    /// of #8 shows leftover 50, nominal 950, range 930 to 970, and #9 still gets nothing.
    /// </summary>
    [Fact]
    public async Task Estimates_follow_the_oldest_order_first_example()
    {
        var fakeTime = new FakeTimeProvider(EightAm.AddHours(-1));
        await using var app = new TestApiFactory { FakeTime = fakeTime };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));

        // Orders #1 to #6 came earlier and were cancelled, so the PRD's numbers #7, #8, #9 are the next ones. #6 had sent
        // 50 kg before it was cancelled, which is now free leftover (design 3.8).
        var earlier = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            earlier.Add((await CreateOrderBodyAsync(manager, product.Id, 50m)).GetProperty("number").GetInt32());
        }

        foreach (var earlierNumber in earlier)
        {
            await SetStatusAsync(app, earlierNumber, OrderStatus.Cancelled, managerUser.Id);
        }

        await ReceiveAsync(manager, cheese.Id, 50m, receiptDate: "2026-11-11");
        await LedgerAsync(app, new Movement
        {
            Type = MovementType.SentToProduction, ItemId = cheese.Id, WarehouseChange = -50m, ProductionStorageChange = 50m,
            OccurredAt = fakeTime.GetUtcNow().UtcDateTime, UserId = managerUser.Id, ProductionOrderNumber = earlier[^1],
        });

        fakeTime.SetUtcNow(EightAm);
        var order7 = await CreateOrderBodyAsync(manager, product.Id, 30m);
        fakeTime.SetUtcNow(EightAm.AddHours(1));
        var order8 = await CreateOrderBodyAsync(manager, product.Id, 1000m);
        fakeTime.SetUtcNow(EightAm.AddHours(2));
        var order9 = await CreateOrderBodyAsync(manager, product.Id, 500m);
        Assert.Equal([7, 8, 9], new[] { order7, order8, order9 }.Select(order => order.GetProperty("number").GetInt32()));

        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        AssertEstimate(Line(await ReadOrderAsync(warehouse, 7), cheese.Id), leftover: 30m, nominal: 0m, min: 0m, max: 0.6m);
        AssertEstimate(Line(await ReadOrderAsync(warehouse, 8), cheese.Id), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);
        AssertEstimate(Line(await ReadOrderAsync(warehouse, 9), cheese.Id), leftover: 0m, nominal: 500m, min: 490m, max: 510m);

        await SetStatusAsync(app, 7, OrderStatus.Cancelled, managerUser.Id);

        AssertEstimate(Line(await ReadOrderAsync(warehouse, 8), cheese.Id), leftover: 50m, nominal: 950m, min: 930m, max: 970m);
        AssertEstimate(Line(await ReadOrderAsync(warehouse, 9), cheese.Id), leftover: 0m, nominal: 500m, min: 490m, max: 510m);
        Assert.Equal(JsonValueKind.Null, Line(await ReadOrderAsync(warehouse, 7), cheese.Id).GetProperty("estimate").ValueKind);
    }

    /// <summary>
    /// D-5 tie-break (architecture 3.3): two orders created in the same second are queued by number, lowest first, so the
    /// lower number takes the leftover. 10 kg free, both plan 10 kg: the first takes 10, the second 0.
    /// </summary>
    [Fact]
    public async Task Orders_created_in_the_same_second_are_queued_by_number()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(EightAm) };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await PutLeftoverAsync(app, cheese.Id, 10m, managerUser.Id);

        var first = await CreateOrderBodyAsync(manager, product.Id, 10m);
        var second = await CreateOrderBodyAsync(manager, product.Id, 10m);

        Assert.Equal(first.GetProperty("createdAt").GetString(), second.GetProperty("createdAt").GetString());
        AssertEstimate(Line(await ReadOrderAsync(manager, first.GetProperty("number").GetInt32()), cheese.Id),
            leftover: 10m, nominal: 0m, min: 0m, max: 0.2m);
        AssertEstimate(Line(await ReadOrderAsync(manager, second.GetProperty("number").GetInt32()), cheese.Id),
            leftover: 0m, nominal: 10m, min: 9.8m, max: 10.2m);
    }

    /// <summary>
    /// Decision #85, AC-13.2: the estimate takes the free leftover and the queue of waiting orders from one snapshot,
    /// even when a send commits between those two reads. 50 kg of free Cheese; order A (older, 30 kg) and B (1,000 kg)
    /// are Created. The read of B is paused right after its last stock-figure query (pending returns) and before the
    /// waiting-orders query; in that gap another request commits A's send (leftover applied 30, 100 kg from the
    /// Warehouse, carried leftover 20). B must show one consistent moment, leftover 20 (nominal 980, range 960 to
    /// 1,000), which is also what it shows after the send. A mixed read would give 50 (figures from before the send,
    /// queue from after it, so A's 30 kg looks free) or 150 (storage after the send, commitment before it).
    /// </summary>
    [Fact]
    public async Task Estimate_reads_one_snapshot_even_when_a_send_commits_in_between()
    {
        Func<Task> betweenQueries = () => Task.CompletedTask;
        var pause = new AfterFirstReadInterceptor(
            command => command.CommandText.Contains("FROM \"Returns\"", StringComparison.Ordinal),
            () => betweenQueries());
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(pause)),
        };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);
        var older = await CreateOrderAsync(manager, product.Id, 30m);
        var younger = await CreateOrderAsync(manager, product.Id, 1000m);

        // What T-010's send of the older order will do, committed by another request while the read is paused.
        betweenQueries = async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
            var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
            await db.InWriteTransactionAsync(async () =>
            {
                var order = await db.ProductionOrders.Include(candidate => candidate.Lines)
                    .SingleAsync(candidate => candidate.Number == older, Cancel);
                order.Status = OrderStatus.InProduction;
                order.SentAt = order.StatusChangedAt = now;
                order.SentById = managerUser.Id;
                var line = order.Lines.Single();
                line.LeftoverApplied = 30m;
                line.WarehouseSend = 100m;
                line.CarriedLeftover = 20m;
                line.ComplementarySends = 0m;
                await ledger.AddAsync(new Movement
                {
                    Type = MovementType.SentToProduction, ItemId = cheese.Id, WarehouseChange = -100m,
                    ProductionStorageChange = 100m, OccurredAt = now, UserId = managerUser.Id, ProductionOrderNumber = older,
                }, Cancel);
            }, Cancel);
        };
        var reader = await app.CreateClientAsAsync(Roles.Warehouse);

        pause.Arm();
        var during = await ReadOrderAsync(reader, younger);

        Assert.True(pause.HasRun);
        AssertEstimate(Line(during, cheese.Id), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);

        // The send really committed, and afterwards the estimate is the same 20 kg: the 20 kg carried leftover is free.
        Assert.Equal("InProduction", (await ReadOrderAsync(reader, older)).GetProperty("status").GetString());
        AssertEstimate(Line(await ReadOrderAsync(reader, younger), cheese.Id), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);
    }

    /// <summary>
    /// Decision #94 (amending #91), decision #58, D-7: with a configured tolerance of 2.125%, the estimate shows the
    /// tolerance as 2.13 (2 places, half away from zero), while the range is calculated with the real 2.125%: planned
    /// 1,000 kg and no leftover give 978.75 to 1,021.25 kg.
    /// </summary>
    [Fact]
    public async Task Estimate_shows_the_tolerance_rounded_and_calculates_with_the_configured_value()
    {
        await using var app = new TestApiFactory();
        app.Settings["Inventory:TolerancePercent"] = "2.125";
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));

        var order = await CreateOrderBodyAsync(manager, product.Id, 1000m);

        AssertEstimate(Line(order, cheese.Id), leftover: 0m, nominal: 1000m, min: 978.75m, max: 1021.25m, tolerance: 2.13m);
    }

    // ---- Detail in other statuses ---------------------------------------------------------------------------------

    /// <summary>
    /// Design 5.7 rules for the line fields, AC-14.2, AC-05.3: an InProduction order has no estimate; its send figures are
    /// shown (complementary sends starting at 0), its completion figures are still <c>null</c>, and its history lists
    /// Created and then InProduction with the sender.
    /// </summary>
    [Fact]
    public async Task In_production_order_shows_send_figures_and_no_estimate()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var sender = await app.CreateUserAsync(Seed.UniqueEmail("sender"), Roles.Warehouse);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var number = await InsertOrderAsync(app, product.Id, managerUser.Id, OrderStatus.InProduction,
            SentLine(1, cheese.Id, leftoverApplied: 5m, warehouseSend: 995m), sentById: sender.Id);

        var order = await ReadOrderAsync(manager, number);

        Assert.Equal("InProduction", order.GetProperty("status").GetString());
        var line = Line(order, cheese.Id);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("estimate").ValueKind);
        Assert.Equal(5m, line.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(995m, line.GetProperty("warehouseSend").GetDecimal());
        Assert.Equal(0m, line.GetProperty("complementarySends").GetDecimal());
        Assert.Equal(0m, line.GetProperty("carriedLeftover").GetDecimal());
        foreach (var field in new[] { "leftoverEntered", "leftoverRemaining", "actualUsage", "requiredForActualOutput",
                     "usageVariance", "usageVariancePercent", "usageVarianceAboveTolerance" })
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
        }

        var history = order.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", "InProduction"], history.Select(entry => entry.GetProperty("status").GetString()));
        AssertPerson(history[1].GetProperty("by"), sender);
    }

    /// <summary>
    /// Decision #58, decision #49, AC-14.2, AC-20.10, on a Completed order inserted through the DbContext. Each displayed
    /// percentage is the stored 6-place value rounded to 2 places, half away from zero: output variance 2.004999 → 2.00;
    /// line usage variance 2.005 → 2.01 and −2.005 → −2.01; tolerance at completion 2 → 2. A stored <c>null</c> percentage
    /// (required rounded to 0) stays <c>null</c>, with above-tolerance true. Every completion figure is shown, the history
    /// ends with Completed by the completing user with the reason as its note, and the complementary orders are listed
    /// with their status, oldest first.
    /// </summary>
    [Fact]
    public async Task Completed_order_shows_stored_percentages_rounded_to_two_places()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var managerUser = await factory.CreateUserAsync(Seed.UniqueEmail("creator"), Roles.Manager);
        var producer = await factory.CreateUserAsync(Seed.UniqueEmail("producer"), Roles.Production);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var completedAt = new DateTime(2026, 11, 11, 15, 40, 0, DateTimeKind.Utc);

        int number;
        int[] complementaryNumbers;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = NewOrder(product.Id, managerUser.Id, OrderStatus.Completed);
            order.SentAt = order.CreatedAt.AddHours(1);
            order.SentById = managerUser.Id;
            order.CompletedAt = completedAt;
            order.CompletedById = producer.Id;
            order.StatusChangedAt = completedAt;
            order.ProducedQuantity = 1001m;
            order.OutputVariance = 1m;
            order.OutputVariancePercent = 2.004999m;
            order.ToleranceAtCompletionPercent = 2m;
            order.CompletionReason = "Last bag line jammed";
            order.Lines.Add(CompletedLine(1, cheese.Id, usageVariancePercent: 2.005m, aboveTolerance: true));
            order.Lines.Add(CompletedLine(2, starch.Id, usageVariancePercent: -2.005m, aboveTolerance: true));
            order.Lines.Add(CompletedLine(3, salt.Id, usageVariancePercent: null, aboveTolerance: true));
            db.ProductionOrders.Add(order);
            await db.SaveChangesAsync(Cancel);

            var sent = NewComplementary(order.Number, managerUser.Id, ComplementaryOrderStatus.Sent, completedAt.AddHours(-4));
            var cancelled = NewComplementary(order.Number, managerUser.Id, ComplementaryOrderStatus.Cancelled, completedAt.AddHours(-1));
            db.ComplementaryOrders.AddRange(sent, cancelled);
            await db.SaveChangesAsync(Cancel);
            number = order.Number;
            complementaryNumbers = [sent.Number, cancelled.Number];
        }

        var body = await ReadOrderAsync(await factory.CreateClientAsAsync(Roles.Production), number);

        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(1001m, body.GetProperty("producedQuantity").GetDecimal());
        Assert.Equal(1m, body.GetProperty("outputVariance").GetDecimal());
        Assert.Equal(2.00m, body.GetProperty("outputVariancePercent").GetDecimal());
        Assert.Equal(2m, body.GetProperty("toleranceAtCompletionPercent").GetDecimal());
        Assert.Equal("Last bag line jammed", body.GetProperty("completionReason").GetString());

        Assert.Equal(2.01m, Line(body, cheese.Id).GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(-2.01m, Line(body, starch.Id).GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, Line(body, salt.Id).GetProperty("usageVariancePercent").ValueKind);
        Assert.True(Line(body, salt.Id).GetProperty("usageVarianceAboveTolerance").GetBoolean());

        var cheeseLine = Line(body, cheese.Id);
        Assert.Equal(JsonValueKind.Null, cheeseLine.GetProperty("estimate").ValueKind);
        Assert.Equal(4m, cheeseLine.GetProperty("leftoverEntered").GetDecimal());
        Assert.Equal(4m, cheeseLine.GetProperty("leftoverRemaining").GetDecimal());
        Assert.Equal(1016m, cheeseLine.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(1001m, cheeseLine.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(15m, cheeseLine.GetProperty("usageVariance").GetDecimal());

        var history = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", "InProduction", "Completed"], history.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("2026-11-11T15:40:00Z", history[2].GetProperty("at").GetString());
        AssertPerson(history[2].GetProperty("by"), producer);
        Assert.Equal("Last bag line jammed", history[2].GetProperty("note").GetString());

        var linked = body.GetProperty("complementaryOrders").EnumerateArray().ToList();
        Assert.Equal(complementaryNumbers, linked.Select(entry => entry.GetProperty("number").GetInt32()));
        Assert.Equal(["Sent", "Cancelled"], linked.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("2026-11-11T11:40:00Z", linked[0].GetProperty("requestedAt").GetString());
        Assert.Equal(["number", "status", "requestedAt"], linked[0].EnumerateObject().Select(field => field.Name));
    }

    /// <summary>
    /// Design 5.7, AC-14.2, AC-05.3: a Cancelled order has no estimate, shows its cancellation reason, and its history
    /// ends with Cancelled by the Manager who cancelled it, with the reason as its note.
    /// </summary>
    [Fact]
    public async Task Cancelled_order_shows_its_reason_and_no_estimate()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var canceller = await factory.CreateUserAsync(Seed.UniqueEmail("canceller"), Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var number = (await CreateOrderBodyAsync(manager, product.Id, 10m)).GetProperty("number").GetInt32();
        await SetStatusAsync(factory, number, OrderStatus.Cancelled, canceller.Id);

        var order = await ReadOrderAsync(manager, number);

        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        Assert.Equal("Customer order withdrawn", order.GetProperty("cancellationReason").GetString());
        Assert.Equal(JsonValueKind.Null, Line(order, cheese.Id).GetProperty("estimate").ValueKind);
        var last = order.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("Cancelled", last.GetProperty("status").GetString());
        AssertPerson(last.GetProperty("by"), canceller);
        Assert.Equal("Customer order withdrawn", last.GetProperty("note").GetString());
    }

    /// <summary>AC-14.3, design 7.2: an unknown order number is <c>404</c> <c>not-found</c> "Order #{number} not found.".</summary>
    [Fact]
    public async Task Unknown_order_number_is_404()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var response = await reader.GetAsync($"{OrdersPath}/999999", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Order #999999 not found.");
    }

    // ---- AC-13.3, decision #80: inputs ------------------------------------------------------------------------------

    /// <summary>
    /// AC-13.3, design 7.2, decision #80: a bad ordered quantity is <c>400</c> <c>validation</c> with one message under
    /// <c>orderedQuantity</c>, and nothing is saved: zero or less; more than 3 decimal places (checked before the maximum);
    /// above 999,999,999.999; missing; <c>null</c>.
    /// </summary>
    [Theory]
    [InlineData("0", "Ordered quantity must be greater than 0.")]
    [InlineData("-5", "Ordered quantity must be greater than 0.")]
    [InlineData("1.0001", "Ordered quantity can have at most 3 decimal places.")]
    [InlineData("999999999.9991", "Ordered quantity can have at most 3 decimal places.")]
    [InlineData("1000000000", OrderedQuantityAboveMax)]
    [InlineData("999999999.9990001", "Ordered quantity can have at most 3 decimal places.")]
    [InlineData(null, "Ordered quantity is required.")]
    [InlineData("null", "Ordered quantity is required.")]
    public async Task Bad_ordered_quantity_is_rejected(string? quantityJson, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await ProductWithRecipeAsync(manager, ((await Seed.CreateItemAsync(manager, ItemType.RawMaterial)).Id, 1m));
        var ordersBefore = await CountAsync(factory, db => db.ProductionOrders);
        var body = quantityJson is null
            ? $$"""{ "finishedProductId": {{product.Id}} }"""
            : $$"""{ "finishedProductId": {{product.Id}}, "orderedQuantity": {{quantityJson}} }""";

        var response = await manager.PostAsync(OrdersPath, ApiJson.Content(body), Cancel);

        await ApiAssert.ValidationAsync(response, "orderedQuantity", message);
        Assert.Equal(ordersBefore, await CountAsync(factory, db => db.ProductionOrders));
    }

    /// <summary>
    /// Design 2.4, PRD 4.1: trailing zeros do not count as decimal places, so 12.5000 is accepted as 12.5, and the planned
    /// amount is round3(0.333333 × 12.5) = round3(4.1666625) = 4.167 (AC-13.1, half away from zero).
    /// </summary>
    [Fact]
    public async Task Trailing_zeros_are_accepted_and_planned_amount_is_rounded()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 0.333333m));

        var response = await manager.PostAsync(OrdersPath,
            ApiJson.Content($$"""{ "finishedProductId": {{product.Id}}, "orderedQuantity": 12.5000 }"""), Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal(12.5m, order.GetProperty("orderedQuantity").GetDecimal());
        Assert.Equal(4.167m, Line(order, cheese.Id).GetProperty("plannedAmount").GetDecimal());
    }

    /// <summary>
    /// Decision #80, architecture 3.1 (overflow): the largest order, 999,999,999.999, of a product whose recipe line is the
    /// largest amount, 999,999.999999 per unit, is created. Its planned amount is round3(999,999.999999 × 999,999,999.999)
    /// = 999,999,999,998,000, which fits <c>decimal(18,3)</c>; the estimate's range, shown but never stored, is computed
    /// without overflow.
    /// </summary>
    [Fact]
    public async Task Largest_order_of_the_largest_recipe_amount_is_created_without_overflow()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 999_999.999999m));

        var order = await CreateOrderBodyAsync(manager, product.Id, 999_999_999.999m);

        var line = Line(order, cheese.Id);
        Assert.Equal(999_999.999999m, line.GetProperty("amountPerUnit").GetDecimal());
        Assert.Equal(999_999_999_998_000m, line.GetProperty("plannedAmount").GetDecimal());
        AssertEstimate(line, leftover: 0m, nominal: 999_999_999_998_000m,
            min: 979_999_999_998_040m, max: 1_019_999_999_997_960m);

        // Read back from the database: the stored planned amount is the same exact value.
        var reread = await ReadOrderAsync(manager, order.GetProperty("number").GetInt32());
        Assert.Equal(999_999_999_998_000m, Line(reread, cheese.Id).GetProperty("plannedAmount").GetDecimal());
    }

    /// <summary>
    /// Design 2.2 step 4, design 7.2: a missing finished product is "Finished product is required."; an unknown id is
    /// "Item {id} does not exist." (<c>400</c>, not <c>404</c>, because the id is in the body).
    /// </summary>
    [Theory]
    [InlineData("""{ "orderedQuantity": 10 }""", "Finished product is required.")]
    [InlineData("""{ "finishedProductId": null, "orderedQuantity": 10 }""", "Finished product is required.")]
    [InlineData("""{ "finishedProductId": 987654, "orderedQuantity": 10 }""", "Item 987654 does not exist.")]
    public async Task Missing_or_unknown_finished_product_is_rejected(string body, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsync(OrdersPath, ApiJson.Content(body), Cancel);

        await ApiAssert.ValidationAsync(response, "finishedProductId", message);
    }

    /// <summary>Design 6: every field problem is reported in one <c>400</c>, each under its own path, joined in <c>detail</c>.</summary>
    [Fact]
    public async Task Every_field_problem_is_reported_at_once()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsync(OrdersPath, ApiJson.Content("{}"), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Finished product is required. Ordered quantity is required.");
        Assert.Equal(["Finished product is required."], ApiJson.Messages(body, "finishedProductId"));
        Assert.Equal(["Ordered quantity is required."], ApiJson.Messages(body, "orderedQuantity"));
    }

    /// <summary>
    /// Architecture 4.1 item 2, design 2.4: an ordered quantity sent as a string is <c>400</c> <c>validation</c>
    /// "orderedQuantity has the wrong type." and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Ordered_quantity_sent_as_a_string_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await ProductWithRecipeAsync(manager, ((await Seed.CreateItemAsync(manager, ItemType.RawMaterial)).Id, 1m));

        var response = await manager.PostAsync(OrdersPath,
            ApiJson.Content($$"""{ "finishedProductId": {{product.Id}}, "orderedQuantity": "10" }"""), Cancel);

        await ApiAssert.ValidationAsync(response, "orderedQuantity", "orderedQuantity has the wrong type.");
    }

    /// <summary>
    /// AC-13.3, design 7.2: an order for a raw material is <c>400</c> <c>order-not-finished-product</c>
    /// "Orders are for finished products only. {item} is a raw material.", and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Order_for_a_raw_material_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var ordersBefore = await CountAsync(factory, db => db.ProductionOrders);

        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = cheese.Id, orderedQuantity = 10 }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "order-not-finished-product",
            $"Orders are for finished products only. {cheese.Name} is a raw material.");
        Assert.Equal(ordersBefore, await CountAsync(factory, db => db.ProductionOrders));
    }

    /// <summary>
    /// AC-13.3, design 7.2: an order for a finished product without a recipe is <c>400</c> <c>no-recipe</c>
    /// "{item} has no recipe. Set its recipe before creating an order.", and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Order_for_a_product_without_a_recipe_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var ordersBefore = await CountAsync(factory, db => db.ProductionOrders);

        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = product.Id, orderedQuantity = 10 }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "no-recipe",
            $"{product.Name} has no recipe. Set its recipe before creating an order.");
        Assert.Equal(ordersBefore, await CountAsync(factory, db => db.ProductionOrders));
    }

    /// <summary>
    /// Design 2.2 step 4 before the product rules (as for receipts, decision #78): a raw material with a bad quantity gets
    /// the field error, not <c>order-not-finished-product</c>.
    /// </summary>
    [Fact]
    public async Task Field_errors_come_before_the_product_rules()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = cheese.Id, orderedQuantity = 0 }, Cancel);

        await ApiAssert.ValidationAsync(response, "orderedQuantity", "Ordered quantity must be greater than 0.");
    }

    // ---- Order numbers ------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-13.1 "the next order number", design 2.1, PRD section 5 (concurrent requests): ten orders created at the same
    /// moment, from ten clients, all succeed and get the numbers 1 to 10, each exactly once.
    /// </summary>
    [Fact]
    public async Task Simultaneous_creates_get_sequential_unique_numbers()
    {
        await using var app = new TestApiFactory();
        var email = Seed.UniqueEmail("manager");
        await app.CreateUserAsync(email, Roles.Manager);
        var token = await app.SignInAsync(email);
        var setup = app.CreateClientWithToken(token);
        var cheese = await Seed.CreateItemAsync(setup, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(setup, (cheese.Id, 1m));
        var clients = Enumerable.Range(0, 10).Select(_ => app.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(clients.Select((client, index) =>
            client.PostAsJsonAsync(OrdersPath, new { finishedProductId = product.Id, orderedQuantity = index + 1 }, Cancel)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var numbers = new List<int>();
        foreach (var response in responses)
        {
            var order = await ApiJson.ReadAsync(response);
            numbers.Add(order.GetProperty("number").GetInt32());

            // Each response is its own order: planned = 1 × its own ordered quantity.
            Assert.Equal(order.GetProperty("orderedQuantity").GetDecimal(),
                Line(order, cheese.Id).GetProperty("plannedAmount").GetDecimal());
        }

        Assert.Equal(Enumerable.Range(1, 10), numbers.Order());
        Assert.Equal(10, await CountAsync(app, db => db.ProductionOrders));
    }

    // ---- AC-14.1: the list ----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-14.1, design 2.5 and 5.7: the list is newest first (creation time, then number for the same second), filtered by
    /// status in any letter case, paged with the total count; a page past the end, or a status nothing has, is
    /// <c>200</c> with no items. Each row is exactly the design's nine summary fields, with <c>statusChangedAt</c> and
    /// <c>producedQuantity</c> from the stored order.
    /// </summary>
    [Fact]
    public async Task List_is_newest_first_filtered_by_status_and_paged()
    {
        var fakeTime = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = fakeTime };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));

        var a = await CreateOrderAsync(manager, product.Id, 100m);          // 08:00, stays Created
        fakeTime.SetUtcNow(EightAm.AddHours(1));
        var b = await CreateOrderAsync(manager, product.Id, 1000m);         // 09:00, then Completed
        fakeTime.SetUtcNow(EightAm.AddHours(2));
        var c = await CreateOrderAsync(manager, product.Id, 300m);          // 10:00, then Cancelled
        fakeTime.SetUtcNow(EightAm.AddHours(3));
        var d = await CreateOrderAsync(manager, product.Id, 400m);          // 11:00, same second as e
        var e = await CreateOrderAsync(manager, product.Id, 500m);
        var completedAt = EightAm.AddHours(4).UtcDateTime;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var completed = await db.ProductionOrders.SingleAsync(order => order.Number == b, Cancel);
            completed.Status = OrderStatus.Completed;
            completed.SentAt = completedAt;
            completed.SentById = managerUser.Id;
            completed.CompletedAt = completedAt;
            completed.CompletedById = managerUser.Id;
            completed.StatusChangedAt = completedAt;
            completed.ProducedQuantity = 990m;
            await db.SaveChangesAsync(Cancel);
        }

        await SetStatusAsync(app, c, OrderStatus.Cancelled, managerUser.Id);
        var reader = await app.CreateClientAsAsync(Roles.Production);

        var all = await ReadListAsync(reader, "");
        Assert.Equal([e, d, c, b, a], Numbers(all));
        Assert.Equal(5, all.GetProperty("totalCount").GetInt32());

        Assert.Equal([e, d, a], Numbers(await ReadListAsync(reader, "?status=Created")));
        var completedPage = await ReadListAsync(reader, "?status=completed");
        var row = Assert.Single(completedPage.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["number", "finishedProductId", "finishedProductName", "unit", "orderedQuantity", "status", "createdAt",
             "statusChangedAt", "producedQuantity"],
            row.EnumerateObject().Select(field => field.Name));
        Assert.Equal(b, row.GetProperty("number").GetInt32());
        Assert.Equal(product.Id, row.GetProperty("finishedProductId").GetInt32());
        Assert.Equal(product.Name, row.GetProperty("finishedProductName").GetString());
        Assert.Equal(product.UnitName, row.GetProperty("unit").GetString());
        Assert.Equal(1000m, row.GetProperty("orderedQuantity").GetDecimal());
        Assert.Equal("Completed", row.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T09:00:00Z", row.GetProperty("createdAt").GetString());
        Assert.Equal("2026-11-11T12:00:00Z", row.GetProperty("statusChangedAt").GetString());
        Assert.Equal(990m, row.GetProperty("producedQuantity").GetDecimal());

        var created = (await ReadListAsync(reader, "?status=Created")).GetProperty("items").EnumerateArray().Last();
        Assert.Equal(created.GetProperty("createdAt").GetString(), created.GetProperty("statusChangedAt").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("producedQuantity").ValueKind);

        var second = await ReadListAsync(reader, "?page=2&pageSize=2");
        Assert.Equal([c, b], Numbers(second));
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, second.GetProperty("totalCount").GetInt32());

        var pastTheEnd = await ReadListAsync(reader, "?page=4&pageSize=2");
        Assert.Empty(Numbers(pastTheEnd));
        Assert.Equal(5, pastTheEnd.GetProperty("totalCount").GetInt32());

        var none = await ReadListAsync(reader, "?status=InProduction");
        Assert.Empty(Numbers(none));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// Design 2.5 and 7.2: an unknown status and paging values outside their limits are <c>400</c> <c>validation</c>,
    /// all reported in one response.
    /// </summary>
    [Fact]
    public async Task Bad_list_query_values_are_rejected()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Warehouse);

        var badStatus = await reader.GetAsync($"{OrdersPath}?status=Done", Cancel);
        await ApiAssert.ValidationAsync(badStatus, "status", "status must be Created, InProduction, Completed or Cancelled.");

        var numericStatus = await reader.GetAsync($"{OrdersPath}?status=1", Cancel);
        await ApiAssert.ValidationAsync(numericStatus, "status", "status must be Created, InProduction, Completed or Cancelled.");

        var all = await reader.GetAsync($"{OrdersPath}?status=Done&page=0&pageSize=101", Cancel);
        var body = await ApiAssert.ProblemAsync(all, HttpStatusCode.BadRequest, "validation",
            "page must be 1 or greater. pageSize must be between 1 and 100. status must be Created, InProduction, Completed or Cancelled.");
        Assert.Equal(["page must be 1 or greater."], ApiJson.Messages(body, "page"));
        Assert.Equal(["pageSize must be between 1 and 100."], ApiJson.Messages(body, "pageSize"));
    }

    // ---- Access (PRD 4.2) and AC-13.6 ---------------------------------------------------------------------------------

    /// <summary>AC-01.5: every production-order endpoint without a token is <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Requests_without_a_token_are_401()
    {
        using var anonymous = factory.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync(OrdersPath, Cancel));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync($"{OrdersPath}/1", Cancel));
        await ApiAssert.NotAuthenticatedAsync(
            await anonymous.PostAsJsonAsync(OrdersPath, new { finishedProductId = 1, orderedQuantity = 1 }, Cancel));
    }

    /// <summary>
    /// AC-13.7, AC-05.1, PRD 4.2 ("Create or cancel an order": Manager only): Warehouse, Production, and a user with both,
    /// get <c>403</c> "It needs the Manager role." and no order is created. The role is checked before the body (design
    /// 2.2), so even a valid body is refused.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Warehouse + "," + Roles.Production)]
    public async Task Only_a_manager_can_create_an_order(string roles)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await ProductWithRecipeAsync(manager, ((await Seed.CreateItemAsync(manager, ItemType.RawMaterial)).Id, 1m));
        var other = await factory.CreateClientAsAsync(roles.Split(','));
        var ordersBefore = await CountAsync(factory, db => db.ProductionOrders);

        var response = await other.PostAsJsonAsync(OrdersPath, new { finishedProductId = product.Id, orderedQuantity = 10 }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        Assert.Equal(ordersBefore, await CountAsync(factory, db => db.ProductionOrders));
    }

    /// <summary>AC-14.1, AC-14.2, PRD 4.2 ("View ... orders"): every role can list orders and read one.</summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_list_and_view_orders(string role)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await ProductWithRecipeAsync(manager, ((await Seed.CreateItemAsync(manager, ItemType.RawMaterial)).Id, 1m));
        var number = await CreateOrderAsync(manager, product.Id, 10m);
        var reader = await factory.CreateClientAsAsync(role);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(OrdersPath, Cancel)).StatusCode);
        Assert.Equal(number, (await ReadOrderAsync(reader, number)).GetProperty("number").GetInt32());
    }

    /// <summary>
    /// AC-13.6, design 4: no endpoint changes an order after creation. <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> on an
    /// order get the framework's <c>405</c> <c>method-not-allowed</c>, and the order is unchanged.
    /// </summary>
    [Fact]
    public async Task Orders_cannot_be_edited_or_deleted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var number = await CreateOrderAsync(manager, product.Id, 10m);
        var change = new { finishedProductId = product.Id, orderedQuantity = 99 };

        var responses = new[]
        {
            await manager.PutAsJsonAsync($"{OrdersPath}/{number}", change, Cancel),
            await manager.PatchAsJsonAsync($"{OrdersPath}/{number}", change, Cancel),
            await manager.DeleteAsync($"{OrdersPath}/{number}", Cancel),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal("method-not-allowed", (await ApiJson.ReadAsync(response)).GetProperty("code").GetString());
        }

        var order = await ReadOrderAsync(manager, number);
        Assert.Equal(10m, order.GetProperty("orderedQuantity").GetDecimal());
        Assert.Equal(10m, Line(order, cheese.Id).GetProperty("plannedAmount").GetDecimal());
    }

    /// <summary>
    /// Decision #85: the detail refuses to be read outside a transaction, so its estimate always comes from one snapshot
    /// of the orders and the stock figures. T-010, T-011 and T-016 call it inside their write transactions.
    /// </summary>
    [Fact]
    public async Task Detail_read_outside_a_transaction_is_refused()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<ProductionOrderService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.ReadDetailAsync(1, Cancel));
    }

    /// <summary>
    /// The detail reads the database, so it refuses to run while the request holds unsaved changes: a send that set
    /// InProduction without saving would otherwise get back a stale Created detail with an estimate. Saved first, inside
    /// the same write transaction, the same read works and shows the change. Callers save first (T-010, T-011, T-016).
    /// </summary>
    [Fact]
    public async Task Detail_read_with_unsaved_changes_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await ProductWithRecipeAsync(manager, ((await Seed.CreateItemAsync(manager, ItemType.RawMaterial)).Id, 1m));
        var number = await CreateOrderAsync(manager, product.Id, 10m);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orders = scope.ServiceProvider.GetRequiredService<ProductionOrderService>();

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.InWriteTransactionAsync(async () =>
            {
                var order = await db.ProductionOrders.SingleAsync(candidate => candidate.Number == number, Cancel);
                order.Status = OrderStatus.InProduction;
                return await orders.ReadDetailAsync(number, Cancel);
            }, Cancel));
            Assert.Contains("SaveChangesAsync", refused.Message);
        }

        // Nothing was saved: the transaction rolled back, so the order is still Created.
        Assert.Equal("Created", (await ReadOrderAsync(manager, number)).GetProperty("status").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orders = scope.ServiceProvider.GetRequiredService<ProductionOrderService>();

            // Throwing at the end rolls this transaction back too, so the shared database is left as it was.
            var rolledBack = await Assert.ThrowsAsync<RollbackOnPurpose>(() => db.InWriteTransactionAsync(async () =>
            {
                var order = await db.ProductionOrders.SingleAsync(candidate => candidate.Number == number, Cancel);
                order.Status = OrderStatus.Cancelled;
                await db.SaveChangesAsync(Cancel);
                throw new RollbackOnPurpose(await orders.ReadDetailAsync(number, Cancel));
            }, Cancel));
            Assert.Equal(OrderStatus.Cancelled, rolledBack.Detail.Status);
            Assert.Null(Assert.Single(rolledBack.Detail.Lines).Estimate);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates an order through the API, checks it got <c>201</c>, and returns the whole response body, for the tests
    /// of creation itself. <see cref="OrderFlow.CreateOrderAsync"/> returns only the number.
    /// </summary>
    private static async Task<JsonElement> CreateOrderBodyAsync(HttpClient manager, int productId, decimal quantity)
    {
        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = productId, orderedQuantity = quantity }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    private static async Task<JsonElement> ReadListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(OrdersPath + query, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    private static List<int> Numbers(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("number").GetInt32()).ToList();

    /// <summary>Inserts an order with the given lines straight into the database and returns its number.</summary>
    private static async Task<int> InsertOrderAsync(TestApiFactory app, int productId, string userId, OrderStatus status,
        ProductionOrderLine line, string? sentById = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = NewOrder(productId, userId, status);
        if (sentById is not null)
        {
            order.SentById = sentById;
        }

        order.Lines.Add(line);
        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync(Cancel);
        return order.Number;
    }

    /// <summary>Thrown to roll a test's write transaction back after looking at what it produced.</summary>
    private sealed class RollbackOnPurpose(ProductionOrderResponse detail) : Exception
    {
        /// <summary>The detail read inside the transaction before it was rolled back.</summary>
        public ProductionOrderResponse Detail { get; } = detail;
    }

    private static ProductionOrder NewOrder(int productId, string userId, OrderStatus status)
    {
        var now = new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);
        var sent = status != OrderStatus.Created;
        return new ProductionOrder
        {
            FinishedProductId = productId, OrderedQuantity = 1000m, Status = status,
            CreatedAt = now, CreatedById = userId, StatusChangedAt = now,
            SentAt = sent ? now.AddHours(2) : null, SentById = sent ? userId : null,
        };
    }

    private static ProductionOrderLine SentLine(int lineNo, int materialId, decimal leftoverApplied, decimal warehouseSend) => new()
    {
        LineNo = lineNo, RawMaterialId = materialId, AmountPerUnit = 1m, PlannedAmount = 1000m,
        LeftoverApplied = leftoverApplied, WarehouseSend = warehouseSend, CarriedLeftover = 0m, ComplementarySends = 0m,
    };

    /// <summary>A completed line with worked example A's figures (AC-20.5) and the given stored percentage.</summary>
    private static ProductionOrderLine CompletedLine(int lineNo, int materialId, decimal? usageVariancePercent, bool aboveTolerance) => new()
    {
        LineNo = lineNo, RawMaterialId = materialId, AmountPerUnit = 1m, PlannedAmount = 1000m,
        LeftoverApplied = 5m, WarehouseSend = 995m, CarriedLeftover = 0m, ComplementarySends = 20m,
        LeftoverEntered = 4m, LeftoverRemaining = 4m, ActualUsage = 1016m, RequiredForActualOutput = 1001m,
        UsageVariance = 15m, UsageVariancePercent = usageVariancePercent, UsageVarianceAboveTolerance = aboveTolerance,
    };

    private static ComplementaryOrder NewComplementary(int parent, string userId, ComplementaryOrderStatus status, DateTime requestedAt) => new()
    {
        ParentOrderNumber = parent, Status = status, Reason = "Hopper spill at start of run",
        RequestedAt = requestedAt, RequestedById = userId,
    };

    private static async Task<int> CountAsync<T>(TestApiFactory app, Func<AppDbContext, IQueryable<T>> table)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await table(scope.ServiceProvider.GetRequiredService<AppDbContext>()).CountAsync(Cancel);
    }

    private static void AssertPerson(JsonElement person, AppUser user)
    {
        Assert.Equal(user.Id, person.GetProperty("id").GetString());
        Assert.Equal(user.Email, person.GetProperty("email").GetString());
        Assert.Equal(2, person.EnumerateObject().Count());
    }

    /// <summary>A line of a Created order: the design 5.7 fields in order, the creation figures, and nulls for the rest.</summary>
    private static void AssertCreatedLine(JsonElement line, CreatedItem material, decimal amountPerUnit, decimal planned)
    {
        Assert.Equal(
            ["itemId", "itemName", "unit", "amountPerUnit", "plannedAmount", "estimate", "leftoverApplied", "warehouseSend",
             "complementarySends", "carriedLeftover", "leftoverEntered", "leftoverRemaining", "actualUsage",
             "requiredForActualOutput", "usageVariance", "usageVariancePercent", "usageVarianceAboveTolerance"],
            line.EnumerateObject().Select(field => field.Name));
        Assert.Equal(material.Name, line.GetProperty("itemName").GetString());
        Assert.Equal(material.UnitName, line.GetProperty("unit").GetString());
        Assert.Equal(amountPerUnit, line.GetProperty("amountPerUnit").GetDecimal());
        Assert.Equal(planned, line.GetProperty("plannedAmount").GetDecimal());
        foreach (var field in line.EnumerateObject().Select(field => field.Name).Skip(6))
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
        }
    }
}
