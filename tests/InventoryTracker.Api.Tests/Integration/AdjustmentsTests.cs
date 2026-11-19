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
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-015: <c>POST /api/v1/adjustments</c> (US-12, AC-12.1 to AC-12.7, decisions #11, #18 and #80, PRD section 5;
/// design 3.10, 5.6 and 7.2 "Receipts and adjustments").
/// </summary>
/// <remarks>
/// <para>
/// Tests on the shared factory each create their own items, so stock figures never mix. Item names are unique per
/// database, so a test that needs the PRD's exact name "Cheese" in a message creates its own factory. So does every test
/// that sends an order, because only one order can be InProduction per database (AC-15.10).
/// </para>
/// <para>
/// Stock reaches the Warehouse through receipts and Production storage through this endpoint itself, so every figure in
/// these tests was made by real requests.
/// </para>
/// </remarks>
public class AdjustmentsTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string AdjustmentsPath = "/api/v1/adjustments";

    /// <summary>Design 7.2, decision #80: the message for an adjustment below -999,999,999.999, typed out so a wrong text fails.</summary>
    private const string QuantityBelowMin = "Quantity must be between -999,999,999.999 and 999,999,999.999.";

    /// <summary>Decision #98 (f): the Warning a completion logs when the stock consumed and the actual usage differ.</summary>
    private const string MismatchWarningText = "differs from actual usage";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-12.1: an adjustment changes one location by the signed quantity ------------------------------------------

    /// <summary>
    /// AC-12.1, AC-05.3, design 5.6: a Manager's adjustment of +7.5 kg in the Warehouse with a reason gives <c>201</c> and
    /// <c>Location: /api/v1/movements/{id}</c>. The body is the Adjustment movement with exactly the fifteen design
    /// fields: the Warehouse change, Production storage null, the trimmed reason, no note, date or links, the Manager and
    /// the UTC time in whole seconds. <c>GET /movements/{id}</c> returns the same body, and the Warehouse stock rises by
    /// exactly the quantity while Production storage is unchanged.
    /// </summary>
    [Fact]
    public async Task Manager_adjusts_warehouse_stock_and_gets_the_movement()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(EightAm.AddMilliseconds(600)) };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        await ReceiveAsync(manager, cheese.Id, 100m);

        var response = await PostAsync(manager, cheese.Id, "Warehouse", 7.5m, "  Physical count found more  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/movements/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Adjustment", body.GetProperty("type").GetString());
        Assert.Equal(cheese.Id, body.GetProperty("itemId").GetInt32());
        Assert.Equal(cheese.Name, body.GetProperty("itemName").GetString());
        Assert.Equal("kg", body.GetProperty("unit").GetString());
        Assert.Equal(7.5m, body.GetProperty("warehouseChange").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("productionStorageChange").ValueKind);
        Assert.Equal("2026-11-11T08:00:00Z", body.GetProperty("occurredAt").GetString());
        Assert.Equal(managerUser.Id, body.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(managerUser.Email, body.GetProperty("user").GetProperty("email").GetString());
        foreach (var empty in new[] { "orderNumber", "complementaryOrderNumber", "returnNumber", "receiptDate", "note" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(empty).ValueKind);
        }

        Assert.Equal("Physical count found more", body.GetProperty("reason").GetString());
        Assert.Equal(15, body.EnumerateObject().Count());

        // Design 4: the Location header names a real resource, and it shows the very same movement.
        var byId = await manager.GetAsync(response.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(byId)).GetRawText());

        var stock = await StockAsync(manager, cheese.Id);
        Assert.Equal(107.5m, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, stock.GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-12.1, D-2: a positive adjustment in Production storage, then a negative one, change only Production storage,
    /// each by its signed quantity; the Warehouse is untouched and the movements carry only
    /// <c>productionStorageChange</c>. The location is accepted in any letter case (design 2.3).
    /// </summary>
    [Fact]
    public async Task Positive_and_negative_adjustments_change_production_storage_only()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await ReceiveAsync(manager, starch.Id, 40m);

        var up = await PostAsync(manager, starch.Id, "productionstorage", 25.125m, "Found on the line");
        var down = await PostAsync(manager, starch.Id, "PRODUCTIONSTORAGE", -5.125m, "Spilled");

        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        var downBody = await ApiJson.ReadAsync(down);
        Assert.Equal(-5.125m, downBody.GetProperty("productionStorageChange").GetDecimal());
        Assert.Equal(JsonValueKind.Null, downBody.GetProperty("warehouseChange").ValueKind);
        var stock = await StockAsync(manager, starch.Id);
        Assert.Equal(20m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(40m, stock.GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// AC-12.6, PRD assumption 10 and PRD section 2 ("finished products enter stock only through order completion or a
    /// Manager adjustment"): a finished product can be adjusted in the Warehouse, up and then down.
    /// </summary>
    [Fact]
    public async Task Finished_products_can_be_adjusted_in_the_warehouse()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var bags = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var up = await PostAsync(manager, bags.Id, "Warehouse", 30m, "Opening count");
        var down = await PostAsync(manager, bags.Id, "warehouse", -4m, "Damaged in storage");

        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        Assert.Equal(26m, (await StockAsync(manager, bags.Id)).GetProperty("warehouse").GetDecimal());
    }

    // ---- AC-12.2 and decision #80: input checks ---------------------------------------------------------------------

    /// <summary>
    /// AC-12.2, design 7.2, decision #95: each bad field gives <c>400</c> <c>validation</c> with exactly one message under
    /// its own path, and no movement is created. Covers zero, a missing quantity, 4 decimal places, a missing, empty and
    /// blank reason, a reason of 501 characters after trimming, a missing and an unknown item, and a missing, unknown and
    /// numeric location.
    /// </summary>
    [Theory]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": 0, "reason": "Count" }""", "quantity", "Quantity cannot be zero.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": -0.000, "reason": "Count" }""", "quantity", "Quantity cannot be zero.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "reason": "Count" }""", "quantity", "Quantity is required.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": -1.0005, "reason": "Count" }""", "quantity", "Quantity can have at most 3 decimal places.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": 1 }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": 1, "reason": "" }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": 1, "reason": "   " }""", "reason", "Reason is required.")]
    [InlineData("""{ "itemId": {item}, "location": "Warehouse", "quantity": 1, "reason": "{reason501}" }""", "reason", "Reason must be 500 characters or fewer.")]
    [InlineData("""{ "location": "Warehouse", "quantity": 1, "reason": "Count" }""", "itemId", "Item is required.")]
    [InlineData("""{ "itemId": 2147483647, "location": "Warehouse", "quantity": 1, "reason": "Count" }""", "itemId", "Item 2147483647 does not exist.")]
    [InlineData("""{ "itemId": {item}, "quantity": 1, "reason": "Count" }""", "location", "Location must be Warehouse or ProductionStorage.")]
    [InlineData("""{ "itemId": {item}, "location": "Shelf", "quantity": 1, "reason": "Count" }""", "location", "Location must be Warehouse or ProductionStorage.")]
    [InlineData("""{ "itemId": {item}, "location": "1", "quantity": 1, "reason": "Count" }""", "location", "Location must be Warehouse or ProductionStorage.")]
    public async Task Bad_fields_are_rejected_with_their_message(string template, string path, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await ReceiveAsync(manager, item.Id, 10m);
        var json = template.Replace("{item}", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{reason501}", new string('r', 501));

        var response = await manager.PostAsync(AdjustmentsPath, ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, path, message);
        Assert.Empty(await AdjustmentsOfAsync(factory, item.Id));
    }

    /// <summary>
    /// AC-12.1, design 2.6: a reason of exactly 500 characters after trimming is accepted and stored trimmed; 12.5000 is
    /// 12.5 (design 2.4: trailing zeros do not count), and the smallest step, -0.001, is accepted.
    /// </summary>
    [Fact]
    public async Task Boundary_values_are_accepted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var reason = new string('r', 500);

        var trailingZeros = await manager.PostAsync(AdjustmentsPath, ApiJson.Content(
            $$"""{ "itemId": {{item.Id}}, "location": "Warehouse", "quantity": 12.5000, "reason": "  {{reason}}  " }"""), Cancel);
        var smallest = await PostAsync(manager, item.Id, "Warehouse", -0.001m, "Rounding on the scale");

        Assert.Equal(HttpStatusCode.Created, trailingZeros.StatusCode);
        Assert.Equal(reason, (await ApiJson.ReadAsync(trailingZeros)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Created, smallest.StatusCode);
        Assert.Equal(12.499m, (await StockAsync(manager, item.Id)).GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// Design 2.2 step 4, design 6: a body that breaks every field rule gets one <c>400</c> listing each field, in body
    /// order, with <c>detail</c> holding all the messages.
    /// </summary>
    [Fact]
    public async Task Every_bad_field_is_reported_in_one_response()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsync(AdjustmentsPath, ApiJson.Content(
            """{ "location": "Shelf", "quantity": 0, "reason": " " }"""), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Item is required. Location must be Warehouse or ProductionStorage. Quantity cannot be zero. Reason is required.");
        Assert.Equal(["itemId", "location", "quantity", "reason"],
            body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    /// <summary>
    /// Design 2.4, architecture 4.1 item 2: a quantity sent as a string is refused by the framework with <c>400</c>
    /// <c>validation</c> before the service runs; nothing is stored.
    /// </summary>
    [Fact]
    public async Task Quantity_sent_as_text_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PostAsync(AdjustmentsPath, ApiJson.Content(
            $$"""{ "itemId": {{item.Id}}, "location": "Warehouse", "quantity": "5", "reason": "Count" }"""), Cancel);

        await ApiAssert.ValidationAsync(response, "quantity", "quantity has the wrong type.");
        Assert.Empty(await AdjustmentsOfAsync(factory, item.Id));
    }

    /// <summary>
    /// Decision #80, PRD row 20, design 7.2: the limit is on the size of the signed quantity, through the shared
    /// <c>Quantity.IsWithinMax</c> (T-008a). 999,999,999.999 and -999,999,999.999 pass the input check and are recorded
    /// (the negative one takes a Warehouse of exactly that much down to 0). 1,000,000,000 gives "Quantity must be
    /// 999,999,999.999 or less." and -1,000,000,000 gives "Quantity must be between -999,999,999.999 and
    /// 999,999,999.999.", both under <c>quantity</c>, and neither creates a movement.
    /// </summary>
    [Fact]
    public async Task Limit_applies_to_the_size_of_the_signed_quantity()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var up = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var down = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var refused = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await ReceiveAsync(manager, down.Id, 999_999_999.999m);

        var largest = await PostAsync(manager, up.Id, "Warehouse", 999_999_999.999m, "Opening count");
        var lowest = await PostAsync(manager, down.Id, "Warehouse", -999_999_999.999m, "Whole lot rejected");
        var above = await PostAsync(manager, refused.Id, "Warehouse", 1_000_000_000m, "Too much");
        var below = await PostAsync(manager, refused.Id, "Warehouse", -1_000_000_000m, "Too little");

        Assert.Equal(HttpStatusCode.Created, largest.StatusCode);
        Assert.Equal(HttpStatusCode.Created, lowest.StatusCode);
        Assert.Equal(999_999_999.999m, (await StockAsync(manager, up.Id)).GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, (await StockAsync(manager, down.Id)).GetProperty("warehouse").GetDecimal());
        await ApiAssert.ValidationAsync(above, "quantity", ExpectedMessages.QuantityAboveMax);
        await ApiAssert.ValidationAsync(below, "quantity", QuantityBelowMin);
        Assert.Empty(await AdjustmentsOfAsync(factory, refused.Id));
    }

    // ---- AC-12.3: never below zero ----------------------------------------------------------------------------------

    /// <summary>
    /// AC-12.3, the PRD example: Cheese in Production storage is 12 kg. −13 kg is <c>409</c> <c>insufficient-stock</c>
    /// "Not enough stock: Cheese in Production storage has 12 kg." and changes nothing; −12 kg is accepted and leaves 0
    /// kg. The same rule holds in the Warehouse, named as such.
    /// </summary>
    [Fact]
    public async Task Negative_adjustment_larger_than_the_stock_is_rejected_and_exactly_zero_is_allowed()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, cheese.Id, "ProductionStorage", 12m, "Count")).StatusCode);
        await ReceiveAsync(manager, cheese.Id, 3m);

        var tooMuch = await PostAsync(manager, cheese.Id, "ProductionStorage", -13m, "Spoiled");
        var tooMuchInWarehouse = await PostAsync(manager, cheese.Id, "Warehouse", -3.001m, "Spoiled");

        await ApiAssert.ProblemAsync(tooMuch, HttpStatusCode.Conflict, "insufficient-stock",
            "Not enough stock: Cheese in Production storage has 12 kg.");
        await ApiAssert.ProblemAsync(tooMuchInWarehouse, HttpStatusCode.Conflict, "insufficient-stock",
            "Not enough stock: Cheese in Warehouse has 3 kg.");
        Assert.Single(await AdjustmentsOfAsync(app, cheese.Id));

        var exact = await PostAsync(manager, cheese.Id, "ProductionStorage", -12m, "Spoiled");

        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        var stock = await StockAsync(manager, cheese.Id);
        Assert.Equal(0m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(3m, stock.GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// PRD section 5, architecture 5.2, the T-015 concurrency criterion: two simultaneous −8 kg adjustments (separate
    /// clients) on 12 kg of stock give exactly one <c>201</c> and one <c>409</c> <c>insufficient-stock</c> naming the 4 kg
    /// left; stock ends at 4, never below. Repeated over five rounds with new items, in Production storage and in the
    /// Warehouse.
    /// </summary>
    [Fact]
    public async Task Simultaneous_adjustments_never_take_stock_below_zero()
    {
        var email = Seed.UniqueEmail("manager");
        await factory.CreateUserAsync(email, Roles.Manager);
        var token = await factory.SignInAsync(email);
        using var setup = factory.CreateClientWithToken(token);

        for (var round = 0; round < 5; round++)
        {
            var location = round % 2 == 0 ? "ProductionStorage" : "Warehouse";
            var unit = await Seed.CreateUnitAsync(setup);
            var item = await Seed.CreateItemAsync(setup, ItemType.RawMaterial, unit);
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(setup, item.Id, location, 12m, "Count")).StatusCode);
            using var first = factory.CreateClientWithToken(token);
            using var second = factory.CreateClientWithToken(token);

            var responses = await Task.WhenAll(
                PostAsync(first, item.Id, location, -8m, "Spoiled"), PostAsync(second, item.Id, location, -8m, "Spoiled"));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            var locationName = location == "Warehouse" ? "Warehouse" : "Production storage";
            await ApiAssert.ProblemAsync(Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Created),
                HttpStatusCode.Conflict, "insufficient-stock", $"Not enough stock: {item.Name} in {locationName} has 4 {unit.Name}.");
            var stock = await StockAsync(setup, item.Id);
            Assert.Equal(4m, stock.GetProperty(location == "Warehouse" ? "warehouse" : "productionStorage").GetDecimal());
            Assert.Equal(2, (await AdjustmentsOfAsync(factory, item.Id)).Count);
        }
    }

    /// <summary>
    /// Decision #18: adjustments are not blocked by a Pending return of the same material (inserted through the
    /// DbContext; returns are T-019), so AC-12.3 is unchanged: a write-off of all the Production storage stock succeeds
    /// even though part of it is waiting to go back to the Warehouse.
    /// </summary>
    [Fact]
    public async Task Pending_return_does_not_block_an_adjustment()
    {
        var (manager, managerUser) = await SignedInAsync(factory, Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, item.Id, "ProductionStorage", 20m, "Count")).StatusCode);
        await InTransactionAsync(factory, async (db, _, now) =>
        {
            db.Returns.Add(new LeftoverReturn
            {
                ItemId = item.Id, Quantity = 15m, Reason = "Not needed", Status = ReturnStatus.Pending,
                CreatedAt = now, CreatedById = managerUser.Id,
            });
            await db.SaveChangesAsync(Cancel);
        });

        var response = await PostAsync(manager, item.Id, "ProductionStorage", -20m, "Spoiled");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0m, (await StockAsync(manager, item.Id)).GetProperty("productionStorage").GetDecimal());
    }

    // ---- AC-12.4: write-off and the next order's estimate ------------------------------------------------------------

    /// <summary>
    /// AC-12.4, design 3.10: 20 kg of Cheese leftover in Production storage has spoiled. A Created order planning 1,000 kg
    /// shows leftover applied 20, nominal send 980, range 960 to 1,000. An adjustment of −20 kg in Production storage with
    /// reason "Spoiled" leaves 0 kg, and the same order's estimate drops by 20 kg: leftover 0, nominal 1,000, range 980 to
    /// 1,020. A Created order does not block the adjustment (only InProduction does, AC-12.5).
    /// </summary>
    [Fact]
    public async Task Write_off_lowers_the_next_orders_leftover_estimate()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, cheese.Id, "ProductionStorage", 20m, "Leftover")).StatusCode);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        AssertEstimate(Line(await ReadOrderAsync(manager, number), cheese.Id), leftover: 20m, nominal: 980m, min: 960m, max: 1000m);

        var writeOff = await PostAsync(manager, cheese.Id, "ProductionStorage", -20m, "Spoiled");

        Assert.Equal(HttpStatusCode.Created, writeOff.StatusCode);
        Assert.Equal("Spoiled", (await ApiJson.ReadAsync(writeOff)).GetProperty("reason").GetString());
        Assert.Equal(0m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
        AssertEstimate(Line(await ReadOrderAsync(manager, number), cheese.Id), leftover: 0m, nominal: 1000m, min: 980m, max: 1020m);
    }

    // ---- AC-12.5, decision #11: material of the InProduction order ---------------------------------------------------

    /// <summary>
    /// AC-12.5, decision #11: while order #1 is InProduction with Cheese on it, a Production storage adjustment of Cheese
    /// is <c>409</c> <c>material-in-use</c> "Cheese in production storage is in use by order #1. Adjust it after the order
    /// is completed or cancelled.", for a write-off and for an increase alike, and nothing changes. The rule is state
    /// (design 2.2 step 5), so it comes before the stock check (a −5,000 kg write-off gets it too, not
    /// <c>insufficient-stock</c>) and after the field checks (a zero quantity is still <c>400</c>). The Warehouse stock of
    /// the same material, and Production storage of a material not on the order, can still be adjusted.
    /// </summary>
    [Fact]
    public async Task Production_storage_adjustment_of_a_material_in_use_is_rejected()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1100m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m))).StatusCode);
        var inUse = $"Cheese in production storage is in use by order #{number}. Adjust it after the order is completed or cancelled.";

        var writeOff = await PostAsync(manager, cheese.Id, "ProductionStorage", -5m, "Spoiled");
        var increase = await PostAsync(manager, cheese.Id, "ProductionStorage", 10m, "Recount");
        var tooMuch = await PostAsync(manager, cheese.Id, "ProductionStorage", -5000m, "Spoiled");
        var zero = await PostAsync(manager, cheese.Id, "ProductionStorage", 0m, "Spoiled");

        await ApiAssert.ProblemAsync(writeOff, HttpStatusCode.Conflict, "material-in-use", inUse);
        await ApiAssert.ProblemAsync(increase, HttpStatusCode.Conflict, "material-in-use", inUse);
        await ApiAssert.ProblemAsync(tooMuch, HttpStatusCode.Conflict, "material-in-use", inUse);
        await ApiAssert.ValidationAsync(zero, "quantity", "Quantity cannot be zero.");
        Assert.Empty(await AdjustmentsOfAsync(app, cheese.Id));
        Assert.Equal(1000m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());

        var warehouse = await PostAsync(manager, cheese.Id, "Warehouse", -100m, "Spoiled in the warehouse");
        var otherMaterial = await PostAsync(manager, salt.Id, "ProductionStorage", 3m, "Found on the line");

        Assert.Equal(HttpStatusCode.Created, warehouse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherMaterial.StatusCode);
        Assert.Equal(0m, (await StockAsync(manager, cheese.Id)).GetProperty("warehouse").GetDecimal());
    }

    /// <summary>
    /// AC-12.5 ("adjust it after the order is completed or cancelled"): once the order is completed through the API, the
    /// leftover can be written off; the same holds for an InProduction order that is then cancelled (status set directly;
    /// cancelling is T-016).
    /// </summary>
    [Fact]
    public async Task Material_can_be_adjusted_after_the_order_is_completed_or_cancelled()
    {
        await using var app = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 2000m);

        var completed = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, completed, (cheese.Id, 1000m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(manager, completed, 1000m, "End of run", (cheese.Id, 10m))).StatusCode);
        var afterCompletion = await PostAsync(manager, cheese.Id, "ProductionStorage", -10m, "Spoiled");

        var cancelled = await CreateOrderAsync(manager, product.Id, 500m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, cancelled, (cheese.Id, 500m))).StatusCode);
        await SetStatusAsync(app, cancelled, OrderStatus.Cancelled, managerUser.Id);
        var afterCancellation = await PostAsync(manager, cheese.Id, "ProductionStorage", -500m, "Spoiled");

        Assert.Equal(HttpStatusCode.Created, afterCompletion.StatusCode);
        Assert.Equal(HttpStatusCode.Created, afterCancellation.StatusCode);
        Assert.Equal(0m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
    }

    /// <summary>
    /// AC-12.5, decision #98 (f), AC-19.2, AC-19.3: the T-011 mismatch scene, now through HTTP. The order is sent 1,000 kg
    /// and a +10 kg Production storage adjustment during the run is refused, so when the order completes with 25 kg left
    /// the ConsumedInProduction movement equals the actual usage (975 kg), production storage ends at 25 kg, and the
    /// completion logs no mismatch Warning. This is the guarantee decision #98 (f) relies on.
    /// </summary>
    [Fact]
    public async Task Blocked_adjustment_keeps_the_completion_figures_exact()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 1000m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m))).StatusCode);

        var adjustment = await PostAsync(manager, cheese.Id, "ProductionStorage", 10m, "Recount");
        var completion = await CompleteAsync(manager, number, 1000m, "Recount", (cheese.Id, 25m));

        Assert.Equal(HttpStatusCode.Conflict, adjustment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        Assert.Equal(975m, Line(await ApiJson.ReadAsync(completion), cheese.Id).GetProperty("actualUsage").GetDecimal());
        Assert.Equal(-975m, (await ConsumedAsync(app, number)).ProductionStorageChange);
        Assert.Equal(25m, (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal());
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(MismatchWarningText, StringComparison.Ordinal));
    }

    /// <summary>
    /// AC-12.5, decision #11, architecture 5.2 ("Leftover counted once"): a −20 kg Production storage adjustment and a send
    /// arrive at the same moment, five rounds. 50 kg of free leftover is in production storage and the order plans 1,000
    /// kg; the send of 960 kg is in range whichever goes first. Exactly one of two things happens each round: the send went
    /// first and the adjustment is <c>409</c> <c>material-in-use</c> (leftover applied 50, storage 1,010 kg), or the
    /// adjustment went first and the send applied only the 30 kg left (storage 990 kg). Either way production storage
    /// equals leftover applied + carried leftover + send, and completing with 10 kg left gives a ConsumedInProduction
    /// equal to the actual usage, with no mismatch Warning (decision #98 (f)).
    /// </summary>
    [Fact]
    public async Task Adjustment_racing_a_send_never_distorts_the_order()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var token = await ManagerTokenAsync(app);

        for (var round = 0; round < 5; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, cheese.Id, "ProductionStorage", 50m, "Leftover")).StatusCode);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            using var adjuster = app.CreateClientWithToken(token);
            using var sender = app.CreateClientWithToken(token);

            var adjustTask = PostAsync(adjuster, cheese.Id, "ProductionStorage", -20m, "Spoiled");
            var sendTask = SendAsync(sender, number, (cheese.Id, 960m));
            await Task.WhenAll(adjustTask, sendTask);
            var (adjust, send) = (await adjustTask, await sendTask);

            Assert.Equal(HttpStatusCode.OK, send.StatusCode);
            var line = Line(await ApiJson.ReadAsync(send), cheese.Id);
            var leftoverApplied = line.GetProperty("leftoverApplied").GetDecimal();
            var storage = (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal();
            if (adjust.StatusCode == HttpStatusCode.Created)
            {
                Assert.Equal(30m, leftoverApplied);
                Assert.Equal(990m, storage);
            }
            else
            {
                await ApiAssert.ProblemAsync(adjust, HttpStatusCode.Conflict, "material-in-use",
                    $"{cheese.Name} in production storage is in use by order #{number}. Adjust it after the order is completed or cancelled.");
                Assert.Equal(50m, leftoverApplied);
                Assert.Equal(1010m, storage);
            }

            Assert.Equal(0m, line.GetProperty("carriedLeftover").GetDecimal());
            Assert.Equal(storage, leftoverApplied + line.GetProperty("carriedLeftover").GetDecimal() + line.GetProperty("warehouseSend").GetDecimal());

            var completion = await CompleteAsync(manager, number, 1000m, "End of run", (cheese.Id, 10m));

            Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
            var actualUsage = Line(await ApiJson.ReadAsync(completion), cheese.Id).GetProperty("actualUsage").GetDecimal();
            Assert.Equal(storage - 10m, actualUsage);
            Assert.Equal(-actualUsage, (await ConsumedAsync(app, number)).ProductionStorageChange);
        }

        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(MismatchWarningText, StringComparison.Ordinal));
    }

    /// <summary>
    /// AC-12.5, decision #98 (f), architecture 5.2: a −5 kg Production storage adjustment and the completion of the order
    /// using that material arrive at the same moment, five rounds (sent 1,000 kg, completed with 10 kg left). Either the
    /// adjustment went first and is <c>409</c> <c>material-in-use</c>, so storage ends at 10 kg; or the completion went
    /// first and the adjustment is then allowed, recorded after the ConsumedInProduction movement, and storage ends at 5
    /// kg. The completion succeeds every time with ConsumedInProduction equal to the actual usage (990 kg), and no
    /// mismatch Warning is ever logged.
    /// </summary>
    [Fact]
    public async Task Adjustment_racing_a_completion_never_distorts_the_order()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var token = await ManagerTokenAsync(app);

        for (var round = 0; round < 5; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m))).StatusCode);
            using var adjuster = app.CreateClientWithToken(token);
            using var completer = app.CreateClientWithToken(token);

            var adjustTask = PostAsync(adjuster, cheese.Id, "ProductionStorage", -5m, "Spoiled");
            var completeTask = CompleteAsync(completer, number, 1000m, "End of run", (cheese.Id, 10m));
            await Task.WhenAll(adjustTask, completeTask);
            var (adjust, completion) = (await adjustTask, await completeTask);

            Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
            Assert.Equal(990m, Line(await ApiJson.ReadAsync(completion), cheese.Id).GetProperty("actualUsage").GetDecimal());
            var consumed = await ConsumedAsync(app, number);
            Assert.Equal(-990m, consumed.ProductionStorageChange);
            var storage = (await StockAsync(manager, cheese.Id)).GetProperty("productionStorage").GetDecimal();
            if (adjust.StatusCode == HttpStatusCode.Created)
            {
                Assert.True((await ApiJson.ReadAsync(adjust)).GetProperty("id").GetInt32() > consumed.Id);
                Assert.Equal(5m, storage);
            }
            else
            {
                await ApiAssert.ProblemAsync(adjust, HttpStatusCode.Conflict, "material-in-use",
                    $"{cheese.Name} in production storage is in use by order #{number}. Adjust it after the order is completed or cancelled.");
                Assert.Equal(10m, storage);
            }
        }

        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(MismatchWarningText, StringComparison.Ordinal));
    }

    // ---- AC-12.6: finished products in Production storage ---------------------------------------------------------------

    /// <summary>
    /// AC-12.6, design 7.2: an adjustment of a finished product in Production storage is <c>400</c>
    /// <c>finished-product-in-production-storage</c> "Finished products are adjusted in the Warehouse only.", for either
    /// sign, and nothing is stored. Field errors come first (decision #78's order): with a zero quantity the answer is the
    /// <c>validation</c> error.
    /// </summary>
    [Fact]
    public async Task Finished_product_in_production_storage_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var bags = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var up = await PostAsync(manager, bags.Id, "ProductionStorage", 5m, "Found");
        var down = await PostAsync(manager, bags.Id, "ProductionStorage", -5m, "Spoiled");
        var zero = await PostAsync(manager, bags.Id, "ProductionStorage", 0m, "Found");

        foreach (var response in new[] { up, down })
        {
            await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "finished-product-in-production-storage",
                "Finished products are adjusted in the Warehouse only.");
        }

        await ApiAssert.ValidationAsync(zero, "quantity", "Quantity cannot be zero.");
        Assert.Empty(await AdjustmentsOfAsync(factory, bags.Id));
    }

    // ---- AC-12.7, AC-01.5: roles ------------------------------------------------------------------------------------

    /// <summary>AC-01.5: without a token the endpoint answers <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Adjustment_without_a_token_is_401()
    {
        using var anonymous = factory.CreateClient();

        var response = await PostAsync(anonymous, 1, "Warehouse", 1m, "Count");

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// AC-12.7, PRD 4.2 ("Make an adjustment": Manager only), AC-05.1: a Warehouse, a Production, and a Warehouse plus
    /// Production user each get <c>403</c> "It needs the Manager role.", and no movement is created. The role check comes
    /// before the body is looked at (design 2.2), so a valid body does not help.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData("Warehouse,Production")]
    public async Task Users_without_the_manager_role_get_403(string roles)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var caller = await factory.CreateClientAsAsync(roles.Split(','));

        var response = await PostAsync(caller, item.Id, "Warehouse", 5m, "Count");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        Assert.Empty(await AdjustmentsOfAsync(factory, item.Id));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Posts an adjustment body (design 5.6) and returns the response unchecked.</summary>
    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, int itemId, string location, decimal quantity, string reason) =>
        client.PostAsJsonAsync(AdjustmentsPath, new { itemId, location, quantity, reason }, Cancel);

    /// <summary>A token for a new Manager, so several clients can send as the same user at the same moment.</summary>
    private static async Task<string> ManagerTokenAsync(TestApiFactory app)
    {
        var email = Seed.UniqueEmail("manager");
        await app.CreateUserAsync(email, Roles.Manager);
        return await app.SignInAsync(email);
    }

    /// <summary>Every Adjustment movement of one item, as stored, oldest first.</summary>
    private static async Task<List<Movement>> AdjustmentsOfAsync(TestApiFactory app, int itemId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .Where(movement => movement.ItemId == itemId && movement.Type == MovementType.Adjustment)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>The single ConsumedInProduction movement of an order (one-material orders in these tests).</summary>
    private static async Task<Movement> ConsumedAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.AsNoTracking()
            .SingleAsync(movement => movement.ProductionOrderNumber == number
                && movement.Type == MovementType.ConsumedInProduction, Cancel);
    }
}
