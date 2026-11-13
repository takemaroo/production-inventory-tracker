using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-008: <c>POST /api/v1/receipts</c> (US-09, AC-05.1 for this endpoint, design 5.6 and 7.2 "Receipts").
/// </summary>
/// <remarks>
/// Tests in this class share one database; each creates its own item, so stock figures never mix. The shared app runs
/// on a fixed clock (<see cref="FixedClockFactory"/>), so "today" is always 2026-11-11 and a run that crosses midnight
/// UTC cannot turn a valid date into a future one. Tests that check an exact time or move the clock create their own
/// factory.
/// </remarks>
public class ReceiptsTests(ReceiptsTests.FixedClockFactory factory) : IClassFixture<ReceiptsTests.FixedClockFactory>
{
    private const string ReceiptsPath = "/api/v1/receipts";
    private const string Today = "2026-11-11";
    private const string Tomorrow = "2026-11-12";

    /// <summary>
    /// The shared app for this class, with its clock stopped at 2026-11-11 12:00 UTC. xUnit creates a class fixture with
    /// its parameterless constructor, so the fake clock is set here rather than in an object initializer.
    /// </summary>
    public sealed class FixedClockFactory : TestApiFactory
    {
        /// <summary>Creates the app with a fake clock that stays at midday on <see cref="Today"/> unless moved.</summary>
        public FixedClockFactory()
        {
            FakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 11, 14, 5, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-09.1, AC-09.4: a receipt adds to Warehouse stock -----------------------------------------------------

    /// <summary>
    /// AC-09.1, AC-09.4, design 5.6: Cheese in the Warehouse is 200 kg; a Warehouse user's receipt of 1,000 kg makes it
    /// 1,200 kg and leaves Production storage unchanged. The <c>201</c> body is the Received movement with exactly the
    /// fifteen design fields, the date, note, user and UTC time (AC-05.3), and <c>Location: /api/v1/movements/{id}</c>.
    /// </summary>
    [Fact]
    public async Task Warehouse_user_records_a_receipt_and_warehouse_stock_goes_up()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start.AddMilliseconds(700)) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var email = Seed.UniqueEmail("warehouse");
        var warehouseUser = await timed.CreateUserAsync(email, Roles.Warehouse);
        var warehouse = timed.CreateClientWithToken(await timed.SignInAsync(email));
        await PostReceiptAsync(warehouse, cheese.Id, 200m);

        var response = await warehouse.PostAsJsonAsync(ReceiptsPath,
            new { itemId = cheese.Id, quantity = 1000, receiptDate = Today, note = "  Delivery note 4471  " }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/movements/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Received", body.GetProperty("type").GetString());
        Assert.Equal(cheese.Id, body.GetProperty("itemId").GetInt32());
        Assert.Equal(cheese.Name, body.GetProperty("itemName").GetString());
        Assert.Equal("kg", body.GetProperty("unit").GetString());
        Assert.Equal(1000m, body.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("productionStorageChange").ValueKind);
        Assert.Equal("2026-11-11T14:05:00Z", body.GetProperty("occurredAt").GetString());
        Assert.Equal(warehouseUser.Id, body.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(email, body.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(2, body.GetProperty("user").EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orderNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("complementaryOrderNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("returnNumber").ValueKind);
        Assert.Equal(Today, body.GetProperty("receiptDate").GetString());
        Assert.Equal("Delivery note 4471", body.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.Equal(15, body.EnumerateObject().Count());

        var stock = await ApiJson.ReadAsync(await warehouse.GetAsync($"/api/v1/stock/{cheese.Id}", Cancel));
        Assert.Equal(1200m, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, stock.GetProperty("productionStorage").GetDecimal());

        // AC-09.1: the date, note and user are stored on the movement itself, with a UTC time.
        await using var scope = timed.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements
            .AsNoTracking().SingleAsync(movement => movement.Id == id, Cancel);
        Assert.Equal(new DateOnly(2026, 11, 11), stored.ReceiptDate);
        Assert.Equal("Delivery note 4471", stored.Note);
        Assert.Equal(warehouseUser.Id, stored.UserId);
        Assert.Equal(new DateTime(2026, 11, 11, 14, 5, 0, DateTimeKind.Utc), stored.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, stored.OccurredAt.Kind);
        Assert.Null(stored.ProductionStorageChange);
    }

    /// <summary>
    /// PRD 4.2 and AC-05.2: a Manager, and a user holding Warehouse together with Production, may record a receipt too.
    /// </summary>
    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData("Warehouse,Production")]
    public async Task Manager_and_a_user_with_warehouse_among_their_roles_can_record_receipts(string roles)
    {
        var item = await CreateRawMaterialAsync();
        var caller = await factory.CreateClientAsAsync(roles.Split(','));

        var response = await caller.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 5, receiptDate = Today }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(5m, await WarehouseStockAsync(item.Id));
    }

    /// <summary>
    /// AC-09.1, AC-09.2 and design 2.4: the note is optional (left out or blank means none); an earlier date is
    /// accepted (a delivery recorded late); 3 decimal places are accepted, and trailing zeros do not count; a note of
    /// exactly 500 characters is accepted.
    /// </summary>
    [Fact]
    public async Task Optional_note_past_date_and_three_decimals_are_accepted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await CreateRawMaterialAsync();

        var noNote = await manager.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 0.001m, receiptDate = "2020-01-31" }, Cancel);
        var blankNote = await manager.PostAsync(ReceiptsPath, ApiJson.Content(
            $$"""{ "itemId": {{item.Id}}, "quantity": 12.5000, "receiptDate": "2026-11-01", "note": "   " }"""), Cancel);
        var longNote = await manager.PostAsJsonAsync(ReceiptsPath,
            new { itemId = item.Id, quantity = 0.499m, receiptDate = "2026-11-01", note = new string('n', 500) }, Cancel);

        Assert.Equal(HttpStatusCode.Created, noNote.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ApiJson.ReadAsync(noNote)).GetProperty("note").ValueKind);
        Assert.Equal("2020-01-31", (await ApiJson.ReadAsync(noNote)).GetProperty("receiptDate").GetString());
        Assert.Equal(HttpStatusCode.Created, blankNote.StatusCode);
        var blankBody = await ApiJson.ReadAsync(blankNote);
        Assert.Equal(JsonValueKind.Null, blankBody.GetProperty("note").ValueKind);
        Assert.Equal(12.5m, blankBody.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(HttpStatusCode.Created, longNote.StatusCode);
        Assert.Equal(500, (await ApiJson.ReadAsync(longNote)).GetProperty("note").GetString()!.Length);

        // 0.001 + 12.5 + 0.499 = 13.000 exactly: decimals, never floating point (architecture 3.1).
        Assert.Equal(13m, await WarehouseStockAsync(item.Id));
    }

    /// <summary>
    /// AC-09.2: "today" is the UTC date. At 23:59:59 UTC a receipt dated today is accepted and one dated tomorrow is
    /// refused; one second later, at midnight UTC, the same "tomorrow" is today and is accepted.
    /// </summary>
    [Fact]
    public async Task Receipt_date_is_compared_with_todays_utc_date()
    {
        var lastSecond = new DateTimeOffset(2026, 11, 11, 23, 59, 59, TimeSpan.Zero);
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(lastSecond) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var today = await manager.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 1, receiptDate = "2026-11-11" }, Cancel);
        var tomorrow = await manager.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 1, receiptDate = "2026-11-12" }, Cancel);
        timed.FakeTime!.Advance(TimeSpan.FromSeconds(1));
        var nextDay = await manager.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 1, receiptDate = "2026-11-12" }, Cancel);

        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
        await ApiAssert.ValidationAsync(tomorrow, "receiptDate", "Receipt date cannot be later than today (UTC).");
        Assert.Equal(HttpStatusCode.Created, nextDay.StatusCode);
    }

    // ---- AC-09.2, AC-09.3: rejected receipts --------------------------------------------------------------------

    /// <summary>
    /// AC-09.2 and design 7.2: each invalid field is <c>400</c> <c>validation</c> with the field named in <c>errors</c>,
    /// and no stock changes (PRD 4.1 "rejected"). Covers quantity zero or less, more than 3 decimals, missing and
    /// unknown item, a date later than today, a note over 500, plus the cases the design leaves to the API: a missing
    /// quantity or date, a malformed date, a quantity beyond the column's capacity, and values of the wrong JSON type.
    /// </summary>
    [Theory]
    [InlineData("""{ "itemId": {item}, "quantity": 0, "receiptDate": "{today}" }""", "quantity", "Quantity must be greater than 0.")]
    [InlineData("""{ "itemId": {item}, "quantity": -5, "receiptDate": "{today}" }""", "quantity", "Quantity must be greater than 0.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1.0001, "receiptDate": "{today}" }""", "quantity", "Quantity can have at most 3 decimal places.")]
    [InlineData("""{ "itemId": {item}, "receiptDate": "{today}" }""", "quantity", "Quantity is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1000000000000000, "receiptDate": "{today}" }""", "quantity", "Quantity cannot be more than 999,999,999,999,999.999.")]
    [InlineData("""{ "quantity": 10, "receiptDate": "{today}" }""", "itemId", "Item is required.")]
    [InlineData("""{ "itemId": 999999, "quantity": 10, "receiptDate": "{today}" }""", "itemId", "Item 999999 does not exist.")]
    [InlineData("""{ "itemId": {item}, "quantity": 10, "receiptDate": "{tomorrow}" }""", "receiptDate", "Receipt date cannot be later than today (UTC).")]
    [InlineData("""{ "itemId": {item}, "quantity": 10 }""", "receiptDate", "Receipt date is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 10, "receiptDate": "  " }""", "receiptDate", "Receipt date is required.")]
    [InlineData("""{ "itemId": {item}, "quantity": 10, "receiptDate": "11/11/2026" }""", "receiptDate", "Receipt date must be a date in the format yyyy-MM-dd.")]
    [InlineData("""{ "itemId": {item}, "quantity": 10, "receiptDate": "2026-02-30" }""", "receiptDate", "Receipt date must be a date in the format yyyy-MM-dd.")]
    [InlineData("""{ "itemId": {item}, "quantity": 10, "receiptDate": "{today}", "note": "{long-note}" }""", "note", "Note must be 500 characters or fewer.")]
    [InlineData("""{ "itemId": {item}, "quantity": "10", "receiptDate": "{today}" }""", "quantity", "quantity has the wrong type.")]
    [InlineData("""{ "itemId": "{item}", "quantity": 10, "receiptDate": "{today}" }""", "itemId", "itemId has the wrong type.")]
    public async Task Invalid_field_is_rejected_and_stock_is_unchanged(string template, string path, string message)
    {
        var item = await CreateRawMaterialAsync();
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var json = template
            .Replace("{item}", item.Id.ToString())
            .Replace("{today}", Today)
            .Replace("{tomorrow}", Tomorrow)
            .Replace("{long-note}", new string('n', 501));
        var before = await CountMovementsAsync();

        var response = await warehouse.PostAsync(ReceiptsPath, ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, path, message);
        Assert.Equal(before, await CountMovementsAsync());
        Assert.Equal(0m, await WarehouseStockAsync(item.Id));
    }

    /// <summary>
    /// AC-09.2, design 6: when several fields are wrong, one <c>400</c> names them all, in body order, and
    /// <c>detail</c> joins the messages.
    /// </summary>
    [Fact]
    public async Task Several_invalid_fields_are_reported_together()
    {
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var before = await CountMovementsAsync();

        var response = await warehouse.PostAsJsonAsync(ReceiptsPath,
            new { quantity = 0, receiptDate = "tomorrow", note = new string('n', 501) }, Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Item is required. Quantity must be greater than 0. Receipt date must be a date in the format yyyy-MM-dd. " +
            "Note must be 500 characters or fewer.");
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "itemId"));
        Assert.Equal(["Quantity must be greater than 0."], ApiJson.Messages(body, "quantity"));
        Assert.Equal(["Receipt date must be a date in the format yyyy-MM-dd."], ApiJson.Messages(body, "receiptDate"));
        Assert.Equal(["Note must be 500 characters or fewer."], ApiJson.Messages(body, "note"));
        Assert.Equal(before, await CountMovementsAsync());
    }

    /// <summary>
    /// AC-09.3: a receipt for a finished product is <c>400</c> <c>receipt-not-raw-material</c> "Receipts are for raw
    /// materials only." and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Receipt_for_a_finished_product_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var before = await CountMovementsAsync();

        var response = await manager.PostAsJsonAsync(ReceiptsPath,
            new { itemId = product.Id, quantity = 10, receiptDate = "2026-11-01" }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "receipt-not-raw-material",
            "Receipts are for raw materials only.");
        Assert.Equal(before, await CountMovementsAsync());
        Assert.Equal(0m, await WarehouseStockAsync(product.Id));
    }

    /// <summary>
    /// Design 2.2: field checks come before the raw-material rule, so a finished product with a bad quantity gets the
    /// field error.
    /// </summary>
    [Fact]
    public async Task Field_errors_come_before_the_raw_material_rule()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var response = await manager.PostAsJsonAsync(ReceiptsPath,
            new { itemId = product.Id, quantity = 0, receiptDate = "2026-11-01" }, Cancel);

        await ApiAssert.ValidationAsync(response, "quantity", "Quantity must be greater than 0.");
    }

    /// <summary>
    /// Architecture 4.1 item 2: no body, the JSON literal <c>null</c>, or invalid JSON are <c>400</c> before any rule
    /// runs, and nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("", "A request body is required.")]
    [InlineData("null", "A request body is required.")]
    [InlineData("{ \"itemId\": ", "The request body is not valid JSON.")]
    public async Task Unreadable_body_is_rejected(string json, string message)
    {
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var before = await CountMovementsAsync();

        var response = await warehouse.PostAsync(ReceiptsPath, ApiJson.Content(json), Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Equal(before, await CountMovementsAsync());
    }

    // ---- PRD section 5: concurrency -----------------------------------------------------------------------------

    /// <summary>
    /// PRD section 5, T-008: ten receipts for the same item sent at the same moment, each from its own client, all
    /// succeed, and the stock equals their sum. Write transactions queue one behind another (architecture 5.2), so no
    /// receipt is lost or counted twice.
    /// </summary>
    [Fact]
    public async Task Ten_simultaneous_receipts_all_succeed_and_stock_is_their_sum()
    {
        var item = await CreateRawMaterialAsync();
        var email = Seed.UniqueEmail("parallel");
        await factory.CreateUserAsync(email, Roles.Warehouse);
        var token = await factory.SignInAsync(email);
        var clients = Enumerable.Range(0, 10).Select(_ => factory.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(clients.Select((client, index) =>
            client.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = index + 0.5m, receiptDate = "2026-11-01" }, Cancel)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var ids = new List<int>();
        foreach (var response in responses)
        {
            ids.Add((await ApiJson.ReadAsync(response)).GetProperty("id").GetInt32());
        }

        Assert.Equal(10, ids.Distinct().Count());

        // 0.5 + 1.5 + ... + 9.5 = 50.
        Assert.Equal(50m, await WarehouseStockAsync(item.Id));
    }

    // ---- AC-01.5 and AC-05.1: access ----------------------------------------------------------------------------

    /// <summary>AC-01.5: <c>POST /receipts</c> without a token is <c>401</c> <c>not-authenticated</c>, and nothing is saved.</summary>
    [Fact]
    public async Task Receipt_without_a_token_is_401()
    {
        var item = await CreateRawMaterialAsync();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(ReceiptsPath, new { itemId = item.Id, quantity = 10, receiptDate = "2026-11-01" }, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
        Assert.Equal(0m, await WarehouseStockAsync(item.Id));
    }

    /// <summary>
    /// AC-05.1 (PRD 4.2, "Record a stock receipt": Warehouse or Manager): a Production-only user gets <c>403</c> naming
    /// the allowed roles, and no stock changes. Design 2.2: the role check comes before every other check, so an unknown
    /// item or invalid body still gets the <c>403</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Receipt_by_a_production_only_user_is_403(bool validBody)
    {
        var item = await CreateRawMaterialAsync();
        var production = await factory.CreateClientAsAsync(Roles.Production);
        object body = validBody
            ? new { itemId = item.Id, quantity = 10, receiptDate = "2026-11-01" }
            : new { itemId = 999999, quantity = 0 };

        var response = await production.PostAsJsonAsync(ReceiptsPath, body, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseOrManager);
        Assert.Equal(0m, await WarehouseStockAsync(item.Id));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private async Task<CreatedItem> CreateRawMaterialAsync()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        return await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
    }

    private static async Task PostReceiptAsync(HttpClient client, int itemId, decimal quantity)
    {
        var response = await client.PostAsJsonAsync(ReceiptsPath, new { itemId, quantity, receiptDate = "2026-11-01" }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The item's Warehouse stock, summed straight from the database (D-2), independent of the stock endpoint.</summary>
    private async Task<decimal> WarehouseStockAsync(int itemId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var changes = await db.Movements.Where(movement => movement.ItemId == itemId)
            .Select(movement => movement.WarehouseChange).ToListAsync(Cancel);
        return changes.Sum() ?? 0m;
    }

    private async Task<int> CountMovementsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.CountAsync(Cancel);
    }
}
