using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// The steps of a production order's life that many integration tests need as set-up: signing in as a role, saving a
/// recipe, creating an order, receiving stock, putting leftover into production storage, sending, completing, and
/// reading the order and the stock back. One shared copy, so the order tests (T-009 to T-011, and T-012 and T-015 to
/// T-020 after them; decision #101) all set up their orders the same way, and a fix here reaches every one of them.
/// </summary>
/// <remarks>
/// <para>
/// Test classes import these with <c>using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;</c>. A
/// <c>using static</c> brings a class's static members into scope, so a test can write <c>SendAsync(...)</c> instead of
/// <c>OrderFlow.SendAsync(...)</c>. If a test class has a member with the same name, its own member wins.
/// </para>
/// <para>
/// Most helpers go through the real API and check its status code, so a broken set-up fails at the line that caused it
/// (like <see cref="Seed"/>). A few write straight to the database: for states no endpoint produces in one step (a
/// complementary order in any status, a Pending return larger than the free leftover), and for set-up that is quicker or
/// more exact without a request (leftover put into production storage, an order's status changed in one step). Those
/// still run inside <c>InWriteTransactionAsync</c> and write movements through <see cref="StockLedger"/>, as a real
/// service must (architecture 5.1, 5.2), so the stored data is the same as an endpoint's.
/// </para>
/// </remarks>
public static class OrderFlow
{
    /// <summary>The base path of the production order endpoints (design 4, resource and endpoint map).</summary>
    public const string OrdersPath = "/api/v1/production-orders";

    /// <summary>The design 5.7 example's creation time, 08:00 UTC on 11 November 2026.</summary>
    public static readonly DateTimeOffset EightAm = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Users ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A new user with the given roles (such as <c>Roles.Manager</c>), signed in with a real token, and the user record,
    /// so a test can compare ids and emails in the responses.
    /// </summary>
    public static async Task<(HttpClient Client, AppUser User)> SignedInAsync(TestApiFactory app, params string[] roles)
    {
        var email = Seed.UniqueEmail("orderflow");
        var user = await app.CreateUserAsync(email, roles);
        return (app.CreateClientWithToken(await app.SignInAsync(email)), user);
    }

    // ---- Recipes and orders through the API ---------------------------------------------------------------------------

    /// <summary>
    /// Saves a recipe through <c>PUT /items/{id}/recipe</c> (T-007), lines in the order given, and checks it got
    /// <c>200</c>. <paramref name="manager"/> must send a Manager's token.
    /// </summary>
    public static async Task PutRecipeAsync(HttpClient manager, int productId, params (int ItemId, decimal Amount)[] lines)
    {
        var response = await manager.PutAsJsonAsync($"/api/v1/items/{productId}/recipe",
            new { lines = lines.Select(line => new { itemId = line.ItemId, amountPerUnit = line.Amount }) }, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A new finished product (unique name, new unit) with the given recipe, lines in the order given. Each pair is a
    /// raw material's id and its amount per unit of product.
    /// </summary>
    public static async Task<CreatedItem> ProductWithRecipeAsync(HttpClient manager, params (int ItemId, decimal Amount)[] lines)
    {
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await PutRecipeAsync(manager, product.Id, lines);
        return product;
    }

    /// <summary>Creates an order through the API (T-009), checks the <c>201</c>, and returns its number.</summary>
    public static async Task<int> CreateOrderAsync(HttpClient manager, int productId, decimal quantity)
    {
        var response = await manager.PostAsJsonAsync(OrdersPath, new { finishedProductId = productId, orderedQuantity = quantity }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("number").GetInt32();
    }

    /// <summary>
    /// A Created order for 1,000 units of a new product (its unit has a unique name) whose recipe is 1 of a new material
    /// ("cheese", planned 1,000) and 0.015 of another ("starch", planned 15), both in one new unit of their own. "cheese"
    /// and "starch" are only the tuple's labels: both items get unique names. With <paramref name="receive"/>, the
    /// Warehouse gets 2,000 and 100 of them first, enough for any in-range send.
    /// </summary>
    public static async Task<(int Number, CreatedItem Cheese, CreatedItem Starch)> TwoLineOrderAsync(
        HttpClient manager, bool receive = true)
    {
        var unit = await Seed.CreateUnitAsync(manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        if (receive)
        {
            await ReceiveAsync(manager, cheese.Id, 2000m);
            await ReceiveAsync(manager, starch.Id, 100m);
        }

        return (await CreateOrderAsync(manager, product.Id, 1000m), cheese, starch);
    }

    /// <summary>
    /// Sends the materials for an order (<c>POST /production-orders/{number}/send</c>, T-010), with one
    /// <c>{ itemId, quantity }</c> per pair, in the order given. Returns the response unchecked, because the send is
    /// often the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, int number, params (int ItemId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync($"{OrdersPath}/{number}/send",
            new { lines = lines.Select(line => new { itemId = line.ItemId, quantity = line.Quantity }) }, Cancel);

    /// <summary>
    /// Completes an order (<c>POST /production-orders/{number}/complete</c>, T-011) with the produced quantity, an
    /// optional reason, and one <c>{ itemId, leftover }</c> per pair, in the order given. Returns the response
    /// unchecked, because the completion is often the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client, int number, decimal producedQuantity, string? reason, params (int ItemId, decimal Leftover)[] lines) =>
        client.PostAsJsonAsync($"{OrdersPath}/{number}/complete", new
        {
            producedQuantity,
            lines = lines.Select(line => new { itemId = line.ItemId, leftover = line.Leftover }),
            reason,
        }, Cancel);

    /// <summary>
    /// Cancels an order (<c>POST /production-orders/{number}/cancel</c>, T-016) with <c>{ "reason": ... }</c>. Returns the
    /// response unchecked, because the cancellation is often the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> CancelAsync(HttpClient client, int number, string? reason) =>
        client.PostAsJsonAsync($"{OrdersPath}/{number}/cancel", new { reason }, Cancel);

    // ---- Complementary orders through the API ---------------------------------------------------------------------

    /// <summary>The base path of the complementary order endpoints once created (design 2.1, 4).</summary>
    public const string ComplementaryPath = "/api/v1/complementary-orders";

    /// <summary>
    /// Requests a complementary order for order <paramref name="number"/>
    /// (<c>POST /production-orders/{number}/complementary-orders</c>, T-017) with the reason and one
    /// <c>{ itemId, requestedAmount }</c> per pair, in the order given. Returns the response unchecked, because the request
    /// is often the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> RequestComplementaryAsync(
        HttpClient client, int number, string? reason, params (int ItemId, decimal Amount)[] lines) =>
        client.PostAsJsonAsync($"{OrdersPath}/{number}/complementary-orders", new
        {
            reason,
            lines = lines.Select(line => new { itemId = line.ItemId, requestedAmount = line.Amount }),
        }, Cancel);

    /// <summary>
    /// <see cref="RequestComplementaryAsync"/> for set-up: checks the <c>201</c> and returns the new complementary order's
    /// number.
    /// </summary>
    public static async Task<int> RequestedComplementaryAsync(
        HttpClient client, int number, string reason, params (int ItemId, decimal Amount)[] lines)
    {
        var response = await RequestComplementaryAsync(client, number, reason, lines);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("number").GetInt32();
    }

    /// <summary>Approves a complementary order (<c>POST /complementary-orders/{number}/approve</c>, T-017), unchecked.</summary>
    public static Task<HttpResponseMessage> ApproveAsync(HttpClient client, int complementaryNumber) =>
        client.PostAsync($"{ComplementaryPath}/{complementaryNumber}/approve", content: null, Cancel);

    /// <summary>
    /// Rejects a complementary order (<c>POST /complementary-orders/{number}/reject</c>, T-017) with
    /// <c>{ "note": ... }</c>, unchecked.
    /// </summary>
    public static Task<HttpResponseMessage> RejectAsync(HttpClient client, int complementaryNumber, string? note) =>
        client.PostAsJsonAsync($"{ComplementaryPath}/{complementaryNumber}/reject", new { note }, Cancel);

    /// <summary>
    /// <see cref="RequestedComplementaryAsync"/> followed by <see cref="ApproveAsync"/> (checked for <c>200</c>): a
    /// complementary order ready to be sent (T-018), through the real endpoints. Returns its number.
    /// </summary>
    public static async Task<int> ApprovedComplementaryAsync(
        HttpClient requester, HttpClient manager, int number, string reason, params (int ItemId, decimal Amount)[] lines)
    {
        var complementary = await RequestedComplementaryAsync(requester, number, reason, lines);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(manager, complementary)).StatusCode);
        return complementary;
    }

    /// <summary>
    /// Sends a complementary order (<c>POST /complementary-orders/{number}/send</c>, T-018) with one
    /// <c>{ itemId, quantity }</c> per pair, in the order given. Returns the response unchecked, because the send is often
    /// the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> SendComplementaryAsync(
        HttpClient client, int complementaryNumber, params (int ItemId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync($"{ComplementaryPath}/{complementaryNumber}/send",
            new { lines = lines.Select(line => new { itemId = line.ItemId, quantity = line.Quantity }) }, Cancel);

    /// <summary>Reads one complementary order through <c>GET /complementary-orders/{number}</c> and checks it got <c>200</c>.</summary>
    public static async Task<JsonElement> ReadComplementaryAsync(HttpClient client, int complementaryNumber)
    {
        var response = await client.GetAsync($"{ComplementaryPath}/{complementaryNumber}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    // ---- Leftover returns through the API --------------------------------------------------------------------------

    /// <summary>The base path of the leftover return endpoints (design 4, 5.9).</summary>
    public const string ReturnsPath = "/api/v1/returns";

    /// <summary>The reason the return helpers give when a test does not care which.</summary>
    public const string ReturnReason = "Not needed for the next run";

    /// <summary>
    /// Instructs a return (<c>POST /returns</c>, T-019) with <c>{ itemId, quantity, reason, orderNumber }</c>. Returns
    /// the response unchecked, because the instruction is often the request under test.
    /// </summary>
    public static Task<HttpResponseMessage> InstructReturnAsync(
        HttpClient client, int itemId, decimal quantity, string? reason = ReturnReason, int? orderNumber = null) =>
        client.PostAsJsonAsync(ReturnsPath, new { itemId, quantity, reason, orderNumber }, Cancel);

    /// <summary>
    /// <see cref="InstructReturnAsync"/> for set-up: checks the <c>201</c> and returns the new return's number. The
    /// material needs that much free leftover (AC-22.2); for a Pending return larger than that, see
    /// <see cref="InsertPendingReturnAsync"/>.
    /// </summary>
    public static async Task<int> InstructedReturnAsync(HttpClient manager, int itemId, decimal quantity, int? orderNumber = null)
    {
        var response = await InstructReturnAsync(manager, itemId, quantity, ReturnReason, orderNumber);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("number").GetInt32();
    }

    /// <summary>Records receiving a return (<c>POST /returns/{number}/receive</c>, T-019), unchecked.</summary>
    public static Task<HttpResponseMessage> ReceiveReturnAsync(HttpClient client, int number) =>
        client.PostAsync($"{ReturnsPath}/{number}/receive", content: null, Cancel);

    /// <summary>Cancels a return (<c>POST /returns/{number}/cancel</c>, T-019), unchecked.</summary>
    public static Task<HttpResponseMessage> CancelReturnAsync(HttpClient client, int number) =>
        client.PostAsync($"{ReturnsPath}/{number}/cancel", content: null, Cancel);

    /// <summary>Reads one return through <c>GET /returns/{number}</c> and checks it got <c>200</c>.</summary>
    public static async Task<JsonElement> ReadReturnAsync(HttpClient client, int number)
    {
        var response = await client.GetAsync($"{ReturnsPath}/{number}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>
    /// The AC-15.5 starting point: Cheese (named exactly that) in kg, a product using 1 kg per bag, 2,000 kg in the
    /// Warehouse, 50 kg of free leftover in production storage, and Created orders #7 (08:00, 30 bags), #8 (09:00, 1,000
    /// bags) and #9 (10:00, 500 bags). Orders #1 to #6 are created first and cancelled, so the next numbers are the PRD's.
    /// Needs a factory of its own (the exact names, the numbers) with <paramref name="time"/> as its clock, which is left
    /// at 10:00.
    /// </summary>
    public static async Task<OldestFirstScene> OldestFirstSceneAsync(TestApiFactory app, FakeTimeProvider time)
    {
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        for (var i = 0; i < 6; i++)
        {
            await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 1m), OrderStatus.Cancelled, managerUser.Id);
        }

        await ReceiveAsync(manager, cheese.Id, 2000m);
        await PutLeftoverAsync(app, cheese.Id, 50m, managerUser.Id);

        time.SetUtcNow(EightAm);
        Assert.Equal(7, await CreateOrderAsync(manager, product.Id, 30m));
        time.SetUtcNow(EightAm.AddHours(1));
        Assert.Equal(8, await CreateOrderAsync(manager, product.Id, 1000m));
        time.SetUtcNow(EightAm.AddHours(2));
        Assert.Equal(9, await CreateOrderAsync(manager, product.Id, 500m));
        return new OldestFirstScene(manager, managerUser, warehouse, cheese, product);
    }

    /// <summary>
    /// The usual set-up of a race test (T-016, T-017): a Manager, a Warehouse and a Production user, one new material at 1
    /// per unit of a new product, 1,000 of it received, and a Created order for 1,000. With <paramref name="send"/> the
    /// Warehouse user sends the 1,000, so the order is InProduction. Only one order can be InProduction per database
    /// (AC-15.10), so a test that sends gives this its own factory.
    /// </summary>
    public static async Task<RaceScene> RaceSceneAsync(TestApiFactory app, bool send)
    {
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, productionUser) = await SignedInAsync(app, Roles.Production);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        if (send)
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m))).StatusCode);
        }

        return new RaceScene(manager, warehouse, production, productionUser, cheese, number);
    }

    /// <summary>Reads an order's detail through <c>GET /production-orders/{number}</c> and checks it got <c>200</c>.</summary>
    public static async Task<JsonElement> ReadOrderAsync(HttpClient client, int number)
    {
        var response = await client.GetAsync($"{OrdersPath}/{number}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The line for one raw material in an order's detail (design 5.7). Fails unless there is exactly one.</summary>
    public static JsonElement Line(JsonElement order, int itemId) =>
        order.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("itemId").GetInt32() == itemId);

    // ---- Stock through the API --------------------------------------------------------------------------------------

    /// <summary>A receipt into the Warehouse through <c>POST /receipts</c> (T-008), checked for <c>201</c>.</summary>
    public static async Task ReceiveAsync(HttpClient client, int itemId, decimal quantity, string receiptDate = "2026-11-01")
    {
        var response = await client.PostAsJsonAsync("/api/v1/receipts", new { itemId, quantity, receiptDate }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// One item's stock through <c>GET /stock/{itemId}</c> (T-008), checked for <c>200</c>: the Warehouse and production
    /// storage figures (design 5.5).
    /// </summary>
    public static async Task<JsonElement> StockAsync(HttpClient client, int itemId)
    {
        var response = await client.GetAsync($"/api/v1/stock/{itemId}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    // ---- States that later tasks' endpoints will produce, written directly ----------------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> in its own scope and write transaction, as a service handling one request would.
    /// The work gets the DbContext, the ledger and the clock's current time; anything it leaves unsaved is saved before
    /// the commit.
    /// </summary>
    /// <remarks>
    /// <c>CreateAsyncScope</c> asks the app's dependency injection container for a fresh <em>scope</em>: the same set of
    /// per-request services (one <see cref="AppDbContext"/>, one <see cref="StockLedger"/>) a real request gets. Disposing
    /// the scope (<c>await using</c>) disposes them, closing the database connection.
    /// </remarks>
    public static async Task InTransactionAsync(TestApiFactory app, Func<AppDbContext, StockLedger, DateTime, Task> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;
        await db.InWriteTransactionAsync(() => work(db, ledger, now), Cancel);
    }

    /// <summary>
    /// Adds the movements through <see cref="StockLedger"/>, in the order given, in one write transaction of their own,
    /// as a service would, so the ledger's checks (no negative stock, the link rules) apply to each. If the ledger refuses
    /// one, the transaction rolls back and none of them is saved. (Shared with StockTests since T-020.)
    /// </summary>
    public static Task LedgerAsync(TestApiFactory app, params Movement[] movements) =>
        InTransactionAsync(app, async (_, ledger, _) =>
        {
            foreach (var movement in movements)
            {
                await ledger.AddAsync(movement, Cancel);
            }
        });

    /// <summary>
    /// Puts free leftover into production storage with a ledger Adjustment at the clock's current time: the same movement
    /// <c>POST /adjustments</c> (T-015) writes, without needing a request. It is free leftover (D-4) unless an
    /// InProduction order commits it.
    /// </summary>
    public static Task PutLeftoverAsync(TestApiFactory app, int itemId, decimal quantity, string userId) =>
        InTransactionAsync(app, (_, ledger, now) => ledger.AddAsync(new Movement
        {
            Type = MovementType.Adjustment, ItemId = itemId, ProductionStorageChange = quantity,
            OccurredAt = now, UserId = userId, Reason = "Leftover from an earlier run",
        }, Cancel));

    /// <summary>
    /// Changes an order's status directly in the database to Completed or Cancelled, in its own write transaction, as
    /// completing (T-011) and cancelling (T-016) do. See <see cref="ApplyStatusAsync"/> for exactly what is stored: only
    /// the status fields, and no movement. For set-up only: a test of completion or cancellation itself uses the endpoint.
    /// </summary>
    public static Task SetStatusAsync(TestApiFactory app, int number, OrderStatus status, string userId) =>
        InTransactionAsync(app, (db, _, now) => ApplyStatusAsync(db, number, status, userId, now));

    /// <summary>
    /// The status change of <see cref="SetStatusAsync"/>, for a test that makes it inside its own
    /// <see cref="InTransactionAsync"/> together with other writes (such as the movements of a completion). Sets the
    /// status and its change time to <paramref name="now"/>; for Completed, the completion time and user; for Cancelled,
    /// the cancellation time and user and always the reason "Customer order withdrawn". Nothing else changes: no
    /// produced quantity, no line figures, no movement.
    /// </summary>
    public static async Task ApplyStatusAsync(AppDbContext db, int number, OrderStatus status, string userId, DateTime now)
    {
        var order = await db.ProductionOrders.SingleAsync(candidate => candidate.Number == number, Cancel);
        order.Status = status;
        order.StatusChangedAt = now;
        switch (status)
        {
            case OrderStatus.Completed:
                order.CompletedAt = now;
                order.CompletedById = userId;
                break;
            case OrderStatus.Cancelled:
                order.CancelledAt = now;
                order.CancelledById = userId;
                order.CancellationReason = "Customer order withdrawn";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    // ---- Reading what was stored ------------------------------------------------------------------------------------

    /// <summary>
    /// The order as stored, with its lines, read in a new scope. <c>AsNoTracking</c> tells EF Core not to keep the rows
    /// in its change tracker, which a read-only check does not need.
    /// </summary>
    public static async Task<ProductionOrder> StoredOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductionOrders.AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Number == number, Cancel);
    }

    /// <summary>
    /// Inserts a complementary order of one line (20 of <paramref name="itemId"/>) for order <paramref name="parent"/>,
    /// in <paramref name="status"/>, with the who-and-when pairs that status needs, and returns its number. Written
    /// directly so a test can have any status in one step; the endpoints are <see cref="RequestComplementaryAsync"/>,
    /// <see cref="ApproveAsync"/> and <see cref="RejectAsync"/> (T-017) and <see cref="SendComplementaryAsync"/> (T-018).
    /// A Sent one gets no movement and does not change the parent line: tests of the automatic cancellation (AC-19.6,
    /// AC-21.4), of AC-17.3 and of AC-18.3 only need its status.
    /// </summary>
    public static async Task<int> InsertComplementaryOrderAsync(
        TestApiFactory app, int parent, ComplementaryOrderStatus status, int itemId, string userId)
    {
        var number = 0;
        await InTransactionAsync(app, async (db, _, now) =>
        {
            var complementary = new ComplementaryOrder
            {
                ParentOrderNumber = parent, Status = status, Reason = "Hopper spill at start of run",
                RequestedAt = now, RequestedById = userId,
                Lines = [new ComplementaryOrderLine { LineNo = 1, RawMaterialId = itemId, RequestedAmount = 20m }],
            };
            switch (status)
            {
                case ComplementaryOrderStatus.Approved:
                    (complementary.ApprovedAt, complementary.ApprovedById) = (now, userId);
                    break;
                case ComplementaryOrderStatus.Rejected:
                    (complementary.RejectedAt, complementary.RejectedById) = (now, userId);
                    break;
                case ComplementaryOrderStatus.Sent:
                    (complementary.ApprovedAt, complementary.ApprovedById, complementary.SentAt, complementary.SentById) = (now, userId, now, userId);
                    break;
            }

            db.ComplementaryOrders.Add(complementary);
            await db.SaveChangesAsync(Cancel);
            number = complementary.Number;
        });
        return number;
    }

    /// <summary>
    /// Inserts a Pending return of <paramref name="quantity"/> of <paramref name="itemId"/>, instructed now by
    /// <paramref name="userId"/>, in its own write transaction, and returns its number. For a Pending return the
    /// instruction endpoint would refuse because it is larger than the free leftover (AC-22.2): the state decision #18
    /// describes (production storage written off after the return was instructed), which lets a test of a check that
    /// reads Pending returns (the send's AC-15.11) skip putting leftover into production storage first. Otherwise use
    /// <see cref="InstructedReturnAsync"/>.
    /// </summary>
    public static Task<int> InsertPendingReturnAsync(TestApiFactory app, int itemId, decimal quantity, string userId) =>
        InsertReturnAsync(app, itemId, quantity, userId, ReturnStatus.Pending);

    /// <summary>
    /// Inserts a return of <paramref name="quantity"/> of <paramref name="itemId"/> straight in
    /// <paramref name="status"/>, instructed now by <paramref name="userId"/>, in its own write transaction, and returns
    /// its number. A Received or Cancelled one gets the who-and-when pair of that step too (AC-05.3), but no movement:
    /// it is for tests of figures that must ignore returns no longer Pending (AC-10.3), where receiving one through the
    /// endpoint would move stock the test does not want moved. For a Pending one, see <see cref="InsertPendingReturnAsync"/>.
    /// </summary>
    public static async Task<int> InsertReturnAsync(
        TestApiFactory app, int itemId, decimal quantity, string userId, ReturnStatus status)
    {
        var number = 0;
        await InTransactionAsync(app, async (db, _, now) =>
        {
            var leftoverReturn = new LeftoverReturn
            {
                ItemId = itemId, Quantity = quantity, Reason = ReturnReason, Status = status,
                CreatedAt = now, CreatedById = userId,
            };
            switch (status)
            {
                case ReturnStatus.Received:
                    (leftoverReturn.ReceivedAt, leftoverReturn.ReceivedById) = (now, userId);
                    break;
                case ReturnStatus.Cancelled:
                    (leftoverReturn.CancelledAt, leftoverReturn.CancelledById) = (now, userId);
                    break;
            }

            db.Returns.Add(leftoverReturn);
            await db.SaveChangesAsync(Cancel);
            number = leftoverReturn.Number;
        });
        return number;
    }

    /// <summary>A return as stored, read in a new scope.</summary>
    public static async Task<LeftoverReturn> StoredReturnAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Returns.AsNoTracking()
            .SingleAsync(leftoverReturn => leftoverReturn.Number == number, Cancel);
    }

    /// <summary>Every movement linked to return <paramref name="number"/>, oldest first, read in a new scope.</summary>
    public static async Task<List<Movement>> MovementsOfReturnAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ReturnNumber == number)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>Every movement linked to order <paramref name="number"/>, oldest first, read in a new scope.</summary>
    public static async Task<List<Movement>> MovementsOfOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ProductionOrderNumber == number)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>A complementary order as stored, read in a new scope.</summary>
    public static async Task<ComplementaryOrder> StoredComplementaryOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ComplementaryOrders.AsNoTracking()
            .SingleAsync(complementary => complementary.Number == number, Cancel);
    }

    /// <summary>An order's notifications as stored, in the order they were created.</summary>
    public static async Task<List<Notification>> NotificationsOfOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking()
            .Where(notification => notification.ProductionOrderNumber == number)
            .OrderBy(notification => notification.Id)
            .ToListAsync(Cancel);
    }

    // ---- Assertions -------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-13.2: a Created order line's labelled estimate block: exactly the five design 5.7 fields, in order, with these
    /// figures. The tolerance shown is expected to be the default 2% unless <paramref name="tolerance"/> says otherwise.
    /// </summary>
    public static void AssertEstimate(
        JsonElement line, decimal leftover, decimal nominal, decimal min, decimal max, decimal tolerance = 2m)
    {
        var estimate = line.GetProperty("estimate");
        Assert.Equal(["leftoverApplied", "nominalSend", "sendRangeMin", "sendRangeMax", "tolerancePercent"],
            estimate.EnumerateObject().Select(field => field.Name));
        Assert.Equal(leftover, estimate.GetProperty("leftoverApplied").GetDecimal());
        Assert.Equal(nominal, estimate.GetProperty("nominalSend").GetDecimal());
        Assert.Equal(min, estimate.GetProperty("sendRangeMin").GetDecimal());
        Assert.Equal(max, estimate.GetProperty("sendRangeMax").GetDecimal());
        Assert.Equal(tolerance, estimate.GetProperty("tolerancePercent").GetDecimal());
    }
}

/// <summary>What <see cref="OrderFlow.RaceSceneAsync"/> set up: who acts, the material and the order.</summary>
public sealed record RaceScene(
    HttpClient Manager, HttpClient Warehouse, HttpClient Production, AppUser ProductionUser, CreatedItem Cheese, int Number);

/// <summary>What <see cref="OrderFlow.OldestFirstSceneAsync"/> set up: who acts, the material and the product.</summary>
public sealed record OldestFirstScene(
    HttpClient Manager, AppUser ManagerUser, HttpClient Warehouse, CreatedItem Cheese, CreatedItem Product);
