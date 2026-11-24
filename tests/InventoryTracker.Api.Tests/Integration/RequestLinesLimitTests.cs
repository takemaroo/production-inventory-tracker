using System.Net;
using System.Text;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// SEC-01: the line cap (<see cref="RequestLimits.MaxLines"/>, 500) on every body that carries <c>lines</c>, checked
/// before the write transaction, and the cap on the messages in one <c>400</c> (<see cref="ValidationErrors.MaxMessages"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>How "before the transaction" is proved.</b> The test takes SQLite's single write lock itself, from a separate
/// connection (<c>BEGIN IMMEDIATE</c>, exactly what <c>InWriteTransactionAsync</c> does, architecture 5.2), and keeps it.
/// Any request that opens a write transaction now has to wait. A body with 501 lines must still be answered at once,
/// which is only possible if it is refused without the lock, and so it can never make another writer wait. As a control,
/// the same request with 500 lines, which passes the cap and goes on into the transaction, is checked to be still
/// waiting; it finishes only after the test releases the lock. Before the fix, the 501-line body waited too (its lines
/// were checked inside the transaction) and the test timed out.
/// </para>
/// <para>
/// The records named in the URLs do not exist, so the 400 also shows that the cap is checked before the <c>404</c>, and
/// the control, once it gets the lock, answers <c>404</c>. (v1.1) A new sales order has no record in its URL; its body
/// names no customer, so the 400 also shows that the cap comes before the customer check (AC-24.5), and the control,
/// once it gets the lock, answers the field checks' <c>400</c>.
/// </para>
/// </remarks>
public class RequestLinesLimitTests(TestApiFactory app) : IClassFixture<TestApiFactory>
{
    private const int NoSuchRecord = 999_999;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The six endpoints whose body carries <c>lines</c>: method, path, and what the 500-line control answers once it has
    /// the write lock.
    /// </summary>
    public static TheoryData<string, string, HttpStatusCode> LineEndpoints => new()
    {
        { "PUT", $"/api/v1/items/{NoSuchRecord}/recipe", HttpStatusCode.NotFound },
        { "POST", $"{OrdersPath}/{NoSuchRecord}/send", HttpStatusCode.NotFound },
        { "POST", $"{OrdersPath}/{NoSuchRecord}/complete", HttpStatusCode.NotFound },
        { "POST", $"{OrdersPath}/{NoSuchRecord}/complementary-orders", HttpStatusCode.NotFound },
        { "POST", $"/api/v1/complementary-orders/{NoSuchRecord}/send", HttpStatusCode.NotFound },

        // (v1.1) T-025, AC-24.5: "Customer is required." and unknown items, all inside the transaction.
        { "POST", SalesFlow.SalesOrdersPath, HttpStatusCode.BadRequest },
    };

    /// <summary>
    /// SEC-01, (v1.1) AC-24.5: 501 lines on any line-carrying endpoint get <c>400</c> <c>too-many-lines</c> "At most 500
    /// lines are allowed." under <c>lines</c>, on its own, answered while another connection holds the write lock; 500
    /// lines pass the cap and wait for the lock (control), then answer <paramref name="afterRelease"/>: <c>404</c> for the
    /// missing record, or for a new sales order the <c>400</c> of its field checks.
    /// </summary>
    [Theory]
    [MemberData(nameof(LineEndpoints))]
    public async Task Too_many_lines_are_refused_without_the_write_lock(string method, string path, HttpStatusCode afterRelease)
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        await using var writeLock = await HeldWriteLock.TakeAsync(app.DatabasePath);

        var control = SendAsync(manager, method, path, Body(path, RequestLimits.MaxLines));
        var refused = await SendAsync(manager, method, path, Body(path, RequestLimits.MaxLines + 1))
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        var body = await ApiAssert.ProblemAsync(refused, HttpStatusCode.BadRequest, "too-many-lines", "At most 500 lines are allowed.");
        Assert.Equal(["At most 500 lines are allowed."], ApiJson.Messages(body, "lines"));
        Assert.Single(body.GetProperty("errors").EnumerateObject());

        // The control has had time to reach the lock; it must still be queued behind it.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
        Assert.False(control.IsCompleted, "The 500-line request did not wait for the write lock, so the control proves nothing.");

        await writeLock.ReleaseAsync();
        var controlResponse = await control.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        Assert.Equal(afterRelease, controlResponse.StatusCode);
    }

    /// <summary>
    /// SEC-01 regression, the reviewer's <c>p7_biglines.py</c> at a test-friendly size: a Warehouse user sends 100,000
    /// <c>null</c> lines (0.5 MB, under the body limit) for a real order. Before the fix this took the write lock and
    /// answered with 13.4 MB of messages. Now it is refused while the lock is held by someone else, in a small response.
    /// </summary>
    [Fact]
    public async Task Reviewers_100000_null_lines_are_refused_quickly_and_briefly()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
        var (number, _, _) = await TwoLineOrderAsync(manager);
        var nullLines = "{\"lines\":[" + string.Join(",", Enumerable.Repeat("null", 100_000)) + "]}";
        await using var writeLock = await HeldWriteLock.TakeAsync(app.DatabasePath);

        var response = await SendAsync(warehouse, "POST", $"{OrdersPath}/{number}/send", nullLines)
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        var text = await response.Content.ReadAsStringAsync(Cancel);
        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "too-many-lines", "At most 500 lines are allowed.");
        Assert.True(text.Length < 1_000, $"The response is {text.Length} characters.");
    }

    /// <summary>
    /// SEC-01: 500 <c>null</c> lines pass the cap and are checked as before, but the <c>400</c> lists only the first 100
    /// of the 1,001 messages (two per line, then the missing order lines), and <c>detail</c> says how many are not shown.
    /// </summary>
    [Fact]
    public async Task A_400_lists_at_most_100_messages()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager);
        var nullLines = "{\"lines\":[" + string.Join(",", Enumerable.Repeat("null", RequestLimits.MaxLines)) + "]}";

        var response = await SendAsync(manager, "POST", $"{OrdersPath}/{number}/send", nullLines);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("validation", body.GetProperty("code").GetString());
        var listed = body.GetProperty("errors").EnumerateObject().SelectMany(field => field.Value.EnumerateArray()).ToList();
        Assert.Equal(100, listed.Count);
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[0].itemId"));
        Assert.Equal(["Quantity is required."], ApiJson.Messages(body, "lines[49].quantity"));
        Assert.False(body.GetProperty("errors").TryGetProperty("lines[50].itemId", out _));
        Assert.EndsWith("Quantity is required. 901 more problems are not shown.", body.GetProperty("detail").GetString());
        Assert.True(text.Length < 20_000, $"The response is {text.Length} characters.");
    }

    /// <summary>
    /// SEC-01, the limits fit real use: a recipe of exactly 500 raw materials is saved (<c>200</c>), an order for it gets
    /// 500 lines (<c>201</c>, AC-13.1), and a send naming all 500 with the largest quantities passes the cap and is fully
    /// checked (here <c>send-out-of-range</c>, AC-15.6, its messages capped too). Written out, that send is under a tenth
    /// of the 1 MB body limit.
    /// </summary>
    [Fact]
    public async Task The_largest_legitimate_bodies_fit_the_limits()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var materials = await InsertRawMaterialsAsync(manager, RequestLimits.MaxLines);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        await PutRecipeAsync(manager, product.Id, materials.Select(id => (id, 0.123456m)).ToArray());
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(RequestLimits.MaxLines, (await ReadOrderAsync(manager, number)).GetProperty("lines").GetArrayLength());

        var send = JsonSerializer.Serialize(new { lines = materials.Select(id => new { itemId = id, quantity = 999_999_999.999m }) });
        Assert.True(Encoding.UTF8.GetByteCount(send) < RequestLimits.MaxBodyBytes / 10, $"The send is {send.Length} bytes.");

        var response = await SendAsync(manager, "POST", $"{OrdersPath}/{number}/send", send);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("send-out-of-range", body.GetProperty("code").GetString());
        Assert.EndsWith("400 more problems are not shown.", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// (v1.1) AC-24.5: a sales order of exactly 500 lines, each a different item, is accepted (<c>201</c>) with every line
    /// kept in the order sent (AC-24.1). The 500 items are raw materials, which a sales order may sell (decision #144).
    /// </summary>
    [Fact]
    public async Task A_sales_order_of_500_lines_is_accepted()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var customer = await SalesFlow.CreateCustomerAsync(manager);
        var items = await InsertRawMaterialsAsync(manager, RequestLimits.MaxLines);
        var body = JsonSerializer.Serialize(new { customerId = customer.Id, lines = items.Select(id => new { itemId = id, quantity = 1.5m }) });

        var response = await SendAsync(manager, "POST", SalesFlow.SalesOrdersPath, body);

        var created = await ApiJson.ReadAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(items, created.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("itemId").GetInt32()));
    }

    /// <summary>
    /// A body for <paramref name="path"/> with <paramref name="count"/> lines, each naming a different item. Only the
    /// number of lines matters to these tests; the other fields are present so the body is otherwise well formed. (v1.1)
    /// The sales order body deliberately has no <c>customerId</c>, so the cap is shown to come before the customer check.
    /// </summary>
    private static string Body(string path, int count)
    {
        string Line(int itemId) => path switch
        {
            _ when path.EndsWith("/recipe", StringComparison.Ordinal) => $"{{\"itemId\":{itemId},\"amountPerUnit\":1}}",
            _ when path.EndsWith("/complete", StringComparison.Ordinal) => $"{{\"itemId\":{itemId},\"leftover\":0}}",
            _ when path.EndsWith("/complementary-orders", StringComparison.Ordinal) => $"{{\"itemId\":{itemId},\"requestedAmount\":1}}",
            _ => $"{{\"itemId\":{itemId},\"quantity\":1}}",
        };

        var lines = string.Join(",", Enumerable.Range(1, count).Select(Line));
        return path switch
        {
            _ when path.EndsWith("/complete", StringComparison.Ordinal) => $"{{\"producedQuantity\":1,\"lines\":[{lines}]}}",
            _ when path.EndsWith("/complementary-orders", StringComparison.Ordinal) => $"{{\"reason\":\"Short\",\"lines\":[{lines}]}}",
            _ => $"{{\"lines\":[{lines}]}}",
        };
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string json) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = ApiJson.Content(json) }, Cancel);

    /// <summary>
    /// Adds <paramref name="count"/> raw materials straight to the database in one write transaction, which is much
    /// quicker than 500 <c>POST /items</c> calls. They get unique names and a new unit, as the API would store them.
    /// </summary>
    private async Task<List<int>> InsertRawMaterialsAsync(HttpClient manager, int count)
    {
        var unit = await Seed.CreateUnitAsync(manager);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<Clock>().UtcNow;

        var items = Enumerable.Range(1, count)
            .Select(_ => Seed.UniqueName("material"))
            .Select(name => new Item
            {
                Name = name,
                NameNormalized = NameNormalizer.Normalize(name),
                Type = ItemType.RawMaterial,
                UnitId = unit.Id,
                CreatedAt = now,
            })
            .ToList();
        await db.InWriteTransactionAsync(async () =>
        {
            db.Items.AddRange(items);
            await db.SaveChangesAsync(Cancel);
            return 0;
        }, Cancel);
        return items.Select(item => item.Id).ToList();
    }
}
