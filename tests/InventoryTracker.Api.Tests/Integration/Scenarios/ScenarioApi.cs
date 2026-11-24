using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// Helpers for the T-020 and (v1.1) T-031 scenario tests that go through HTTP only: the start-up Manager's sign-in,
/// staff created through <c>POST /users</c>, adjustments, every page of a list, an order's notifications as a Manager
/// reads them, and two checks run at the end of every scenario: AC-05.3 (every change names a user and a UTC time) and
/// AC-11.4 (the movements add up to the stock, which is never below zero).
/// </summary>
/// <remarks>
/// <para>
/// The scenarios start from an empty database and must never write to it except through the API (T-020). The shared
/// <see cref="OrderFlow"/> helpers they use are only its HTTP ones (<c>CreateOrderAsync</c>, <c>SendAsync</c>,
/// <c>CompleteAsync</c>, <c>CancelAsync</c>, the complementary order and return calls, <c>ReceiveAsync</c>,
/// <c>StockAsync</c>, <c>ReadOrderAsync</c>), plus the pure <c>Line</c> and <c>AssertEstimate</c>. Its database helpers
/// (<c>SignedInAsync</c>, <c>PutLeftoverAsync</c>, <c>SetStatusAsync</c>, the <c>Stored...</c> and <c>...OfOrderAsync</c>
/// readers) are not used here, so the scenarios read back only what a client could read.
/// </para>
/// <para>
/// A <c>static</c> class: nothing here keeps state between calls; each method takes the app or the client it works with.
/// </para>
/// </remarks>
public static class ScenarioApi
{
    /// <summary>The base path every endpoint of design section 4 lives under (design 2.1).</summary>
    public const string ApiPath = "/api/v1";

    /// <summary>Design 2.3, AC-05.3: a UTC time in ISO 8601 with whole seconds and the <c>Z</c> suffix.</summary>
    public static readonly Regex UtcTime = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", RegexOptions.CultureInvariant);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Users ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// US-02, design 3.2: the first Manager, which start-up created from <c>ADMIN_EMAIL</c> and <c>ADMIN_PASSWORD</c>
    /// (the test values of <see cref="TestApiFactory"/>), signed in through <c>POST /auth/sign-in</c>.
    /// </summary>
    public static async Task<HttpClient> StartUpManagerAsync(TestApiFactory app) =>
        app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));

    /// <summary>
    /// AC-03.1, design 3.3: a Manager creates a user with <paramref name="email"/> and <paramref name="roles"/> through
    /// <c>POST /users</c> (checked for <c>201</c>), and the new user signs in with the initial password.
    /// </summary>
    public static async Task<Staff> CreateStaffAsync(TestApiFactory app, HttpClient manager, string email, params string[] roles)
    {
        var id = await CreateUserAsync(manager, email, roles);
        return new Staff(app.CreateClientWithToken(await app.SignInAsync(email)), id, email);
    }

    /// <summary>
    /// <c>POST /users</c> with <see cref="TestApiFactory.UserPassword"/>, checked for <c>201</c>; returns the new user's id.
    /// </summary>
    public static async Task<string> CreateUserAsync(HttpClient manager, string email, params string[] roles)
    {
        var response = await manager.PostAsJsonAsync($"{ApiPath}/users",
            new { email, password = TestApiFactory.UserPassword, roles }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("id").GetString()!;
    }

    // ---- Requests ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An adjustment through <c>POST /adjustments</c> (US-12, design 5.6), checked for <c>201</c>; returns the movement.
    /// <paramref name="location"/> is <c>Warehouse</c> or <c>ProductionStorage</c>.
    /// </summary>
    public static async Task<JsonElement> AdjustAsync(
        HttpClient manager, int itemId, string location, decimal quantity, string reason)
    {
        var response = await manager.PostAsJsonAsync($"{ApiPath}/adjustments", new { itemId, location, quantity, reason }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>A <c>GET</c> of <paramref name="path"/> (relative to the host), checked for <c>200</c>; returns the body.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>
    /// Every row of a paged list (design 2.5), reading pages of 100 until <c>totalCount</c> rows have arrived. Rows come
    /// in the list's own order, newest first.
    /// </summary>
    public static async Task<List<JsonElement>> AllRowsAsync(HttpClient client, string path)
    {
        var rows = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; ; page++)
        {
            var body = await GetAsync(client, $"{path}{separator}pageSize=100&page={page}");
            var items = body.GetProperty("items").EnumerateArray().ToList();
            rows.AddRange(items);
            if (items.Count == 0 || rows.Count >= body.GetProperty("totalCount").GetInt32())
            {
                return rows;
            }
        }
    }

    /// <summary>
    /// The notifications of order <paramref name="number"/> as <paramref name="manager"/> reads them through
    /// <c>GET /notifications</c> (US-23), oldest first (by id), so a test can compare them in the order they were raised.
    /// </summary>
    public static async Task<List<JsonElement>> OrderNotificationsAsync(HttpClient manager, int number) =>
        (await AllRowsAsync(manager, $"{ApiPath}/notifications"))
            .Where(notification => notification.GetProperty("orderNumber").GetInt32() == number)
            .OrderBy(notification => notification.GetProperty("id").GetInt32())
            .ToList();

    /// <summary>The movements linked to order <paramref name="number"/> through <c>GET /movements?orderNumber=</c>, oldest first.</summary>
    public static async Task<List<JsonElement>> OrderMovementsAsync(HttpClient client, int number) =>
        (await AllRowsAsync(client, $"{ApiPath}/movements?orderNumber={number}"))
            .OrderBy(movement => movement.GetProperty("id").GetInt32())
            .ToList();

    /// <summary>A number field that may be <c>null</c> (design 2.3: unknown or not touched), read as a decimal.</summary>
    public static decimal? NullableDecimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();

    // ---- End-of-scenario checks -------------------------------------------------------------------------------------

    /// <summary>
    /// AC-11.4, D-2, PRD section 5 ("stock never goes below zero in either location"): for every item in
    /// <c>GET /stock</c> and both locations, the sum of <c>warehouseChange</c> (or <c>productionStorageChange</c>) over
    /// the item's rows of <c>GET /movements?itemId=</c> equals the stock shown, and neither stock is below zero. A raw
    /// material's free leftover is never below zero either (D-4). Returns how many items were checked, so the caller can
    /// assert that the check covered something.
    /// </summary>
    public static async Task<int> AssertMovementsAddUpToStockAsync(HttpClient reader)
    {
        var rows = await AllRowsAsync(reader, $"{ApiPath}/stock");
        foreach (var row in rows)
        {
            var itemId = row.GetProperty("itemId").GetInt32();
            var movements = await AllRowsAsync(reader, $"{ApiPath}/movements?itemId={itemId}");
            var warehouse = row.GetProperty("warehouse").GetDecimal();
            var production = row.GetProperty("productionStorage").GetDecimal();

            Assert.Equal(warehouse, movements.Sum(movement => NullableDecimal(movement.GetProperty("warehouseChange")) ?? 0m));
            Assert.Equal(production, movements.Sum(movement => NullableDecimal(movement.GetProperty("productionStorageChange")) ?? 0m));
            Assert.True(warehouse >= 0m, $"Item {itemId} has {warehouse} in the Warehouse.");
            Assert.True(production >= 0m, $"Item {itemId} has {production} in production storage.");
            if (NullableDecimal(row.GetProperty("freeLeftover")) is { } free)
            {
                Assert.True(free >= 0m, $"Item {itemId} has {free} free leftover.");
            }
        }

        return rows.Count;
    }

    /// <summary>
    /// AC-05.3: every movement, every order and complementary order status change, every return status change and
    /// (v1.1, T-031) every sales order status change names the user who made it (<c>id</c> and <c>email</c>) and a UTC
    /// time (design 2.3). Reads every movement, and every order, complementary order, return and sales order in full
    /// (their detail's <c>history</c>, design 5.7 to 5.9 and 5.12). Each history also ends at the record's current
    /// status. Returns the number of records checked of each kind. <paramref name="reader"/> must be a Manager, the one
    /// role that can read all five lists.
    /// </summary>
    public static async Task<AuditCounts> AssertEveryChangeIsAttributedAsync(HttpClient reader)
    {
        var movements = await AllRowsAsync(reader, $"{ApiPath}/movements");
        foreach (var movement in movements)
        {
            AssertPerson(movement.GetProperty("user"));
            AssertUtc(movement.GetProperty("occurredAt"));
        }

        var orders = await AssertHistoriesAsync(reader, "production-orders");
        var complementaryOrders = await AssertHistoriesAsync(reader, "complementary-orders");
        var returns = await AssertHistoriesAsync(reader, "returns");
        var salesOrders = await AssertHistoriesAsync(reader, "sales-orders");
        return new AuditCounts(movements.Count, orders, complementaryOrders, returns, salesOrders);
    }

    /// <summary>
    /// Reads every record of a numbered resource (<c>production-orders</c>, <c>complementary-orders</c>, <c>returns</c>,
    /// (v1.1) <c>sales-orders</c>) by its number and checks its <c>history</c>: the statuses are exactly a legal path
    /// from the starting status to the record's current one (<see cref="LegalPaths"/>), so no step is missing, and each
    /// entry has a UTC <c>at</c> and a <c>by</c> person. Returns how many records it read.
    /// </summary>
    private static async Task<int> AssertHistoriesAsync(HttpClient reader, string resource)
    {
        var rows = await AllRowsAsync(reader, $"{ApiPath}/{resource}");
        foreach (var row in rows)
        {
            var number = row.GetProperty("number").GetInt32();
            var detail = await GetAsync(reader, $"{ApiPath}/{resource}/{number}");
            var history = detail.GetProperty("history").EnumerateArray().ToList();
            var path = string.Join(">", history.Select(entry => entry.GetProperty("status").GetString()));
            var legal = LegalPaths(resource, detail);
            Assert.True(legal.Contains(path),
                $"{resource} #{number}: history '{path}' is not one of: {string.Join(" | ", legal)}.");
            foreach (var entry in history)
            {
                AssertUtc(entry.GetProperty("at"));
                AssertPerson(entry.GetProperty("by"));
            }
        }

        return rows.Count;
    }

    /// <summary>
    /// The status paths a record in its current status may show in <c>history</c>, written <c>A&gt;B&gt;C</c>, from the
    /// lifecycles in design 3.1 and the history rules of design 5.7 to 5.9 and 5.12 (each status reached is listed once,
    /// oldest first, starting with the creation status). Usually there is one path. A Cancelled production order was cancelled
    /// either while Created or after its send; it was sent exactly when its lines carry a fixed <c>leftoverApplied</c>
    /// (design 5.7: set at send and kept after cancellation), so it too has one exact path. A Cancelled complementary
    /// order may have been Approved first, and its response carries no other sign of that, so either path is allowed.
    /// </summary>
    private static IReadOnlyList<string> LegalPaths(string resource, JsonElement detail)
    {
        var status = detail.GetProperty("status").GetString();
        return (resource, status) switch
        {
            ("production-orders", "Created") => ["Created"],
            ("production-orders", "InProduction") => ["Created>InProduction"],
            ("production-orders", "Completed") => ["Created>InProduction>Completed"],
            ("production-orders", "Cancelled") => detail.GetProperty("lines").EnumerateArray()
                .Any(line => line.GetProperty("leftoverApplied").ValueKind != JsonValueKind.Null)
                ? ["Created>InProduction>Cancelled"]
                : ["Created>Cancelled"],
            ("complementary-orders", "PendingApproval") => ["PendingApproval"],
            ("complementary-orders", "Approved") => ["PendingApproval>Approved"],
            ("complementary-orders", "Rejected") => ["PendingApproval>Rejected"],
            ("complementary-orders", "Sent") => ["PendingApproval>Approved>Sent"],
            ("complementary-orders", "Cancelled") => ["PendingApproval>Cancelled", "PendingApproval>Approved>Cancelled"],
            ("returns", "Pending") => ["Pending"],
            ("returns", "Received") => ["Pending>Received"],
            ("returns", "Cancelled") => ["Pending>Cancelled"],

            // (v1.1) Design 3.1 and 5.12: Created, then Shipped or Cancelled, both final (D-10).
            ("sales-orders", "Created") => ["Created"],
            ("sales-orders", "Shipped") => ["Created>Shipped"],
            ("sales-orders", "Cancelled") => ["Created>Cancelled"],
            _ => throw new InvalidOperationException($"No lifecycle known for {resource} in status {status}."),
        };
    }

    /// <summary>Design 2.3 "People": <c>{ "id", "email" }</c>, both filled in.</summary>
    private static void AssertPerson(JsonElement person)
    {
        Assert.Equal(JsonValueKind.Object, person.ValueKind);
        Assert.False(string.IsNullOrEmpty(person.GetProperty("id").GetString()));
        Assert.Contains("@", person.GetProperty("email").GetString());
    }

    private static void AssertUtc(JsonElement time) => Assert.Matches(UtcTime, time.GetString()!);
}

/// <summary>A user created through <c>POST /users</c> and signed in: their client, id and email.</summary>
public sealed record Staff(HttpClient Client, string Id, string Email);

/// <summary>How many records <see cref="ScenarioApi.AssertEveryChangeIsAttributedAsync"/> checked, by kind.</summary>
public sealed record AuditCounts(int Movements, int Orders, int ComplementaryOrders, int Returns, int SalesOrders);
