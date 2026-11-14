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
/// (like <see cref="Seed"/>). A few write straight to the database, for states that the endpoints of later tasks will
/// produce (cancelling an order is T-016, adjustments are T-015). Those still run inside <c>InWriteTransactionAsync</c>
/// and write movements through <see cref="StockLedger"/>, as a real service must (architecture 5.1, 5.2), so the stored
/// data is the same as the future endpoint's.
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
    /// Adds one movement through <see cref="StockLedger"/> in its own write transaction, as a service would, so the
    /// ledger's checks (no negative stock, the link rules) apply to it.
    /// </summary>
    public static Task LedgerAsync(TestApiFactory app, Movement movement) =>
        InTransactionAsync(app, (_, ledger, _) => ledger.AddAsync(movement, Cancel));

    /// <summary>
    /// Puts free leftover into production storage with a ledger Adjustment at the clock's current time (the adjustment
    /// endpoint is T-015). It is free leftover (D-4) unless an InProduction order commits it.
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
    /// the status fields, and no movement.
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
