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
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-025: create and view sales orders, <c>POST /api/v1/sales-orders</c>, <c>GET /api/v1/sales-orders</c> and
/// <c>GET /api/v1/sales-orders/{number}</c> (US-24 AC-24.1 to AC-24.9, US-25 AC-25.1 to AC-25.4; AC-29.3, AC-29.4 and
/// AC-29.8 for the customer inside a sales order; AC-05.3; design 3.13, 5.12 and 7.2 "Sales orders"; architecture 5.1
/// check order; decision #163 logging).
/// </summary>
/// <remarks>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>), so they use unique names and filter
/// lists by their own customer. A test that checks a PRD sentence word for word ("Deli Norte"), the first sales order
/// number, or a list across all customers creates its own <see cref="TestApiFactory"/>, because a fixed name can exist
/// only once in a database and other tests' sales orders would appear in the list.
/// </remarks>
public class SalesOrdersTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The design 5.12 example's creation time, 09:00 UTC on 1 December 2026.</summary>
    private static readonly DateTimeOffset NineAm = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A character outside the Basic Multilingual Plane: two UTF-16 code units, one code point (decision #116).</summary>
    private const string Emoji = "\U0001F9C0";

    private const int NoSuchRecord = 999_999;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-24.1: create --------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-24.1, AC-05.3, D-10, design 5.12: a Seller creates a sales order after two production orders exist. <c>201</c>,
    /// <c>Location: /api/v1/sales-orders/1</c> (its own sequence, so number 1), status Created, the trimmed customer
    /// reference, who and when from the token and the app's clock, the lines in the order entered with item type and unit,
    /// <c>null</c> for the shipment note and cancellation reason (AC-25.2), and one Created history entry. The same body is
    /// read back at the <c>Location</c>.
    /// </summary>
    [Fact]
    public async Task Seller_creates_sales_order_1_with_the_design_detail()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(NineAm.AddMilliseconds(400)) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var (seller, user) = await OrderFlow.SignedInAsync(timed, Roles.Seller);
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, name: "Cheese");
        await OrderFlow.PutRecipeAsync(manager, product.Id, (cheese.Id, 1m));
        Assert.Equal(2, await TwoProductionOrdersAsync(manager, product.Id));
        var customer = await CreateCustomerAsync(seller, "Deli Norte");

        var response = await seller.PostAsJsonAsync(SalesOrdersPath, new
        {
            customerId = customer.Id,
            customerReference = "  PO-2291  ",
            lines = new object[] { new { itemId = product.Id, quantity = 600m }, new { itemId = cheese.Id, quantity = 12.5m } },
        }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"{SalesOrdersPath}/1", response.Headers.Location?.AbsolutePath);
        Assert.Equal(
            ["number", "customer", "customerReference", "status", "createdAt", "createdBy", "shipmentNote", "cancellationReason", "lines", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, body.GetProperty("number").GetInt32());
        AssertCustomer(body, customer.Id, "Deli Norte", isActive: true);
        Assert.Equal("PO-2291", body.GetProperty("customerReference").GetString());
        Assert.Equal("Created", body.GetProperty("status").GetString());
        Assert.Equal("2026-12-01T09:00:00Z", body.GetProperty("createdAt").GetString());
        AssertPerson(body.GetProperty("createdBy"), user);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipmentNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancellationReason").ValueKind);

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["itemId", "itemName", "itemType", "unit", "quantity"], lines[0].EnumerateObject().Select(field => field.Name));
        AssertLine(lines[0], product.Id, "Shredded cheese 1 kg bag", "FinishedProduct", "Bag", 600m);
        AssertLine(lines[1], cheese.Id, "Cheese", "RawMaterial", cheese.UnitName, 12.5m);

        var history = Assert.Single(body.GetProperty("history").EnumerateArray());
        Assert.Equal(["status", "at", "by", "note"], history.EnumerateObject().Select(field => field.Name));
        Assert.Equal("Created", history.GetProperty("status").GetString());
        Assert.Equal("2026-12-01T09:00:00Z", history.GetProperty("at").GetString());
        AssertPerson(history.GetProperty("by"), user);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("note").ValueKind);

        var read = await seller.GetAsync(response.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(read)).GetRawText());

        // Architecture 3.2: LineNo is the position in the body, from 1.
        var stored = await StoredSalesOrderAsync(timed, 1);
        Assert.Equal(new[] { (1, product.Id), (2, cheese.Id) }, stored.Lines.OrderBy(line => line.LineNo).Select(line => (line.LineNo, line.ItemId)));
        Assert.Equal(stored.CreatedAt, stored.StatusChangedAt);
    }

    /// <summary>
    /// AC-24.1, design 2.6: the customer reference is optional. Left out, sent as <c>null</c>, empty or only spaces, it is
    /// stored and returned as <c>null</c>. A Manager may create a sales order too.
    /// </summary>
    [Theory]
    [InlineData("""{ "customerId": {customer}, "lines": [ { "itemId": {item}, "quantity": 1 } ] }""")]
    [InlineData("""{ "customerId": {customer}, "customerReference": null, "lines": [ { "itemId": {item}, "quantity": 1 } ] }""")]
    [InlineData("""{ "customerId": {customer}, "customerReference": "", "lines": [ { "itemId": {item}, "quantity": 1 } ] }""")]
    [InlineData("""{ "customerId": {customer}, "customerReference": "    ", "lines": [ { "itemId": {item}, "quantity": 1 } ] }""")]
    public async Task Customer_reference_is_optional_and_blank_becomes_null(string template)
    {
        var scene = await SceneAsync();

        var response = await scene.Manager.PostAsync(SalesOrdersPath, ApiJson.Content(Fill(template, scene)), Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("customerReference").ValueKind);
        Assert.Null((await StoredSalesOrderAsync(factory, body.GetProperty("number").GetInt32())).CustomerReference);
    }

    // ---- AC-24.2: no stock checked or moved ------------------------------------------------------------------------

    /// <summary>
    /// AC-24.2, PRD example: the Warehouse holds 0 Bag of an item; a sales order for 600 Bag of it is <c>201</c>. No
    /// movement is created, and <c>GET /stock/{itemId}</c> still shows 0 in the Warehouse.
    /// </summary>
    [Fact]
    public async Task Sales_order_for_an_item_without_stock_is_created_and_moves_nothing()
    {
        var scene = await SceneAsync();

        var response = await PostSalesOrderAsync(scene.Seller, scene.Customer.Id, (scene.Product.Id, 600m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await MovementCountAsync(factory, scene.Product.Id));
        Assert.Equal(0m, (await OrderFlow.StockAsync(scene.Seller, scene.Product.Id)).GetProperty("warehouse").GetDecimal());
    }

    // ---- AC-24.3: field checks -------------------------------------------------------------------------------------

    /// <summary>
    /// AC-24.3, design 7.2: a missing or <c>null</c> customer is "Customer is required.", and an id that names no customer
    /// is "Customer 999999 does not exist.", both under <c>customerId</c>. <c>400</c>, not <c>404</c>: the id is in the
    /// body (design 2.2). Nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("""{ "lines": [ { "itemId": {item}, "quantity": 1 } ] }""", "Customer is required.")]
    [InlineData("""{ "customerId": null, "lines": [ { "itemId": {item}, "quantity": 1 } ] }""", "Customer is required.")]
    [InlineData("""{ "customerId": 999999, "lines": [ { "itemId": {item}, "quantity": 1 } ] }""", "Customer 999999 does not exist.")]
    public async Task Missing_or_unknown_customer_is_rejected(string template, string message)
    {
        var scene = await SceneAsync();
        var before = await CountSalesOrdersAsync(factory);

        var response = await scene.Seller.PostAsync(SalesOrdersPath, ApiJson.Content(Fill(template, scene)), Cancel);

        await ApiAssert.ValidationAsync(response, "customerId", message);
        Assert.Equal(before, await CountSalesOrdersAsync(factory));
    }

    /// <summary>
    /// AC-24.3, design 2.6, decision #116: a customer reference of 100 characters is accepted, counted after trimming and
    /// in code points (100 emoji are 200 UTF-16 units but 100 characters). 101 characters is "Customer reference must
    /// be 100 characters or fewer." under <c>customerReference</c>.
    /// </summary>
    [Fact]
    public async Task Customer_reference_up_to_100_characters_is_accepted()
    {
        var scene = await SceneAsync();
        var hundred = new string('r', 100);
        var hundredEmoji = string.Concat(Enumerable.Repeat(Emoji, 100));

        var accepted = await PostWithReferenceAsync(scene, $"  {hundred}  ");
        var acceptedEmoji = await PostWithReferenceAsync(scene, hundredEmoji);
        var refused = await PostWithReferenceAsync(scene, hundred + "r");
        var refusedEmoji = await PostWithReferenceAsync(scene, hundredEmoji + Emoji);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(hundred, (await ApiJson.ReadAsync(accepted)).GetProperty("customerReference").GetString());
        Assert.Equal(HttpStatusCode.Created, acceptedEmoji.StatusCode);
        await ApiAssert.ValidationAsync(refused, "customerReference", "Customer reference must be 100 characters or fewer.");
        await ApiAssert.ValidationAsync(refusedEmoji, "customerReference", "Customer reference must be 100 characters or fewer.");
    }

    /// <summary>
    /// AC-24.3, design 7.2: <c>lines</c> missing, <c>null</c> or empty is "At least one line is required." under
    /// <c>lines</c>.
    /// </summary>
    [Theory]
    [InlineData("""{ "customerId": {customer} }""")]
    [InlineData("""{ "customerId": {customer}, "lines": null }""")]
    [InlineData("""{ "customerId": {customer}, "lines": [] }""")]
    public async Task Missing_or_empty_lines_are_rejected(string template)
    {
        var scene = await SceneAsync();

        var response = await scene.Seller.PostAsync(SalesOrdersPath, ApiJson.Content(Fill(template, scene)), Cancel);

        await ApiAssert.ValidationAsync(response, "lines", "At least one line is required.");
    }

    /// <summary>
    /// AC-24.3, design 7.2 (the decision #100 pattern): a line sent as <c>null</c> gets both "Item is required." under
    /// <c>lines[0].itemId</c> and "Quantity is required." under <c>lines[0].quantity</c>, in one <c>400</c>.
    /// </summary>
    [Fact]
    public async Task Null_line_gets_both_required_messages()
    {
        var scene = await SceneAsync();

        var response = await scene.Seller.PostAsync(SalesOrdersPath,
            ApiJson.Content($$"""{ "customerId": {{scene.Customer.Id}}, "lines": [ null ] }"""), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", "Item is required. Quantity is required.");
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[0].itemId"));
        Assert.Equal(["Quantity is required."], ApiJson.Messages(body, "lines[0].quantity"));
    }

    /// <summary>
    /// AC-24.3, design 7.2: a line item missing is "Item is required.", and an id that names no item is "Item 999999 does
    /// not exist.", under <c>lines[0].itemId</c>.
    /// </summary>
    [Theory]
    [InlineData("""{ "quantity": 1 }""", "Item is required.")]
    [InlineData("""{ "itemId": null, "quantity": 1 }""", "Item is required.")]
    [InlineData("""{ "itemId": 999999, "quantity": 1 }""", "Item 999999 does not exist.")]
    public async Task Missing_or_unknown_item_is_rejected(string line, string message)
    {
        var scene = await SceneAsync();

        var response = await scene.Seller.PostAsync(SalesOrdersPath,
            ApiJson.Content($$"""{ "customerId": {{scene.Customer.Id}}, "lines": [ {{line}} ] }"""), Cancel);

        await ApiAssert.ValidationAsync(response, "lines[0].itemId", message);
    }

    /// <summary>
    /// AC-24.3, design 7.2, decision #75: the same item on several lines is reported on every later line naming it, "{item}
    /// appears on more than one line." under <c>lines[i].itemId</c>; the first line naming it is not flagged, and neither
    /// is a different item in between.
    /// </summary>
    [Fact]
    public async Task Same_item_twice_is_flagged_on_every_later_line()
    {
        var scene = await SceneAsync();

        var response = await PostSalesOrderAsync(scene.Seller, scene.Customer.Id,
            (scene.Product.Id, 1m), (scene.Material.Id, 2m), (scene.Product.Id, 3m), (scene.Product.Id, 4m));

        var message = $"{scene.Product.Name} appears on more than one line.";
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", $"{message} {message}");
        Assert.Equal(["lines[2].itemId", "lines[3].itemId"], body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        Assert.Equal([message], ApiJson.Messages(body, "lines[2].itemId"));
        Assert.Equal([message], ApiJson.Messages(body, "lines[3].itemId"));
    }

    /// <summary>
    /// AC-24.3, design 7.2, decisions #75, #80, #95: a line quantity missing, <c>null</c>, 0, negative, with 4 decimal
    /// places or of 1,000,000,000 gets one message under <c>lines[0].quantity</c>, from the first rule it fails: required,
    /// greater than 0, at most 3 decimal places, at most 999,999,999.999.
    /// </summary>
    [Theory]
    [InlineData("", "Quantity is required.")]
    [InlineData(""", "quantity": null""", "Quantity is required.")]
    [InlineData(""", "quantity": 0""", "Quantity must be greater than 0.")]
    [InlineData(""", "quantity": -5""", "Quantity must be greater than 0.")]
    [InlineData(""", "quantity": 1.2345""", "Quantity can have at most 3 decimal places.")]
    [InlineData(""", "quantity": 1000000000""", ExpectedMessages.QuantityAboveMax)]
    [InlineData(""", "quantity": 1000000000.5""", ExpectedMessages.QuantityAboveMax)]
    public async Task Bad_quantity_is_rejected(string quantity, string message)
    {
        var scene = await SceneAsync();

        var response = await scene.Seller.PostAsync(SalesOrdersPath, ApiJson.Content(
            $$"""{ "customerId": {{scene.Customer.Id}}, "lines": [ { "itemId": {{scene.Product.Id}}{{quantity}} } ] }"""), Cancel);

        await ApiAssert.ValidationAsync(response, "lines[0].quantity", message);
    }

    /// <summary>
    /// AC-24.3, design 2.4, decision #80: the largest quantity, 999,999,999.999, is accepted, and trailing zeros do not count
    /// as decimal places (12.5000 is stored as 12.5).
    /// </summary>
    [Fact]
    public async Task Largest_quantity_and_trailing_zeros_are_accepted()
    {
        var scene = await SceneAsync();

        var response = await scene.Seller.PostAsync(SalesOrdersPath, ApiJson.Content(
            $$"""{ "customerId": {{scene.Customer.Id}}, "lines": [ { "itemId": {{scene.Product.Id}}, "quantity": 999999999.999 }, { "itemId": {{scene.Material.Id}}, "quantity": 12.5000 } ] }"""),
            Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(999_999_999.999m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(12.5m, lines[1].GetProperty("quantity").GetDecimal());
    }

    /// <summary>
    /// AC-24.3, design 6: every problem in one <c>400</c>, each under its own JSON path, in the order customer, reference,
    /// then the lines in body order (architecture 4.1 item 16); <c>detail</c> joins them all with a space.
    /// </summary>
    [Fact]
    public async Task Every_problem_is_reported_in_one_response()
    {
        var scene = await SceneAsync();
        var json = $$"""
            {
              "customerReference": "{{new string('r', 101)}}",
              "lines": [
                null,
                { "itemId": 999999, "quantity": 0 },
                { "itemId": {{scene.Product.Id}}, "quantity": 1 },
                { "itemId": {{scene.Product.Id}}, "quantity": 1.0001 }
              ]
            }
            """;

        var response = await scene.Seller.PostAsync(SalesOrdersPath, ApiJson.Content(json), Cancel);

        var duplicate = $"{scene.Product.Name} appears on more than one line.";
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Customer is required. Customer reference must be 100 characters or fewer. Item is required. Quantity is required. "
            + $"Item 999999 does not exist. Quantity must be greater than 0. {duplicate} Quantity can have at most 3 decimal places.");
        Assert.Equal(
            ["customerId", "customerReference", "lines[0].itemId", "lines[0].quantity", "lines[1].itemId", "lines[1].quantity", "lines[3].itemId", "lines[3].quantity"],
            body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    // ---- AC-24.4: deactivated customer -----------------------------------------------------------------------------

    /// <summary>
    /// AC-24.4, AC-29.4, PRD example with decision #152: a sales order for a deactivated customer is <c>409</c>
    /// <c>customer-inactive</c> "Customer Deli Norte is deactivated. Reactivate the customer or choose another one.", and
    /// nothing is saved. After a reactivation the same request is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task Sales_order_for_a_deactivated_customer_is_409()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var customer = await CreateCustomerAsync(seller, "Deli Norte");
        await PostCustomerActionAsync(seller, customer.Id, "deactivate");

        var refused = await PostSalesOrderAsync(seller, customer.Id, (item.Id, 600m));

        await ApiAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "customer-inactive", ExpectedMessages.DeliNorteInactive);
        Assert.Equal(0, await CountSalesOrdersAsync(fresh));

        await PostCustomerActionAsync(seller, customer.Id, "reactivate");
        Assert.Equal(HttpStatusCode.Created, (await PostSalesOrderAsync(seller, customer.Id, (item.Id, 600m))).StatusCode);
    }

    /// <summary>
    /// AC-24.4, design 2.2 and 7.2 ("after every field check"): a body with a bad line <b>and</b> a deactivated customer
    /// gets the <c>400</c> only, naming the line; the <c>409</c> comes after the field checks.
    /// </summary>
    [Fact]
    public async Task Bad_line_with_a_deactivated_customer_gives_the_400_only()
    {
        var scene = await SceneAsync();
        await PostCustomerActionAsync(scene.Seller, scene.Customer.Id, "deactivate");

        var response = await PostSalesOrderAsync(scene.Seller, scene.Customer.Id, (scene.Product.Id, 0m));

        await ApiAssert.ValidationAsync(response, "lines[0].quantity", "Quantity must be greater than 0.");
    }

    // ---- AC-24.5: the line cap -------------------------------------------------------------------------------------

    /// <summary>
    /// AC-24.5, design 2.2 and 7.2: 501 lines get <c>400</c> <c>too-many-lines</c> "At most 500 lines are allowed." under
    /// <c>lines</c>, on its own, even with the customer missing, a reference too long and every line <c>null</c>: the cap
    /// comes before every other check. (That it is answered before the write transaction, and that 500 lines are accepted,
    /// is in <see cref="RequestLinesLimitTests"/>.)
    /// </summary>
    [Fact]
    public async Task Too_many_lines_is_answered_on_its_own()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var json = $$"""{ "customerReference": "{{new string('r', 101)}}", "lines": [ {{string.Join(",", Enumerable.Repeat("null", 501))}} ] }""";

        var response = await seller.PostAsync(SalesOrdersPath, ApiJson.Content(json), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "too-many-lines", ExpectedMessages.TooManyLines);
        Assert.Equal(["lines"], body.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        Assert.Equal([ExpectedMessages.TooManyLines], ApiJson.Messages(body, "lines"));
    }

    // ---- AC-24.6, AC-24.8: what may be sold -------------------------------------------------------------------------

    /// <summary>
    /// AC-24.6: several Created sales orders for the same customer and the same items are all <c>201</c>, with consecutive
    /// numbers, and so is one created while a production order is InProduction: no production order blocks a sales order.
    /// </summary>
    [Fact]
    public async Task Several_sales_orders_for_the_same_customer_and_items_are_all_created()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await OrderFlow.ProductWithRecipeAsync(manager, (material.Id, 1m));
        await OrderFlow.ReceiveAsync(manager, material.Id, 100m);

        var first = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 5m), (material.Id, 1m));
        var second = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 5m), (material.Id, 1m));
        var order = await OrderFlow.CreateOrderAsync(manager, product.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await OrderFlow.SendAsync(manager, order, (material.Id, 10m))).StatusCode);
        var duringProduction = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 5m), (material.Id, 90m));

        Assert.Equal([1, 2, 3], new[] { first, second, duringProduction });
        foreach (var number in new[] { first, second, duringProduction })
        {
            Assert.Equal("Created", (await ReadSalesOrderAsync(seller, number)).GetProperty("status").GetString());
        }
    }

    /// <summary>
    /// AC-24.8, decision #144, PRD example: Pizzeria Lago, an active customer, orders 120 kg of Cheese, a raw material:
    /// <c>201</c>, the line shown as RawMaterial in kg. A sales order with Cheese and "Shredded cheese 1 kg bag" together
    /// is <c>201</c> too.
    /// </summary>
    [Fact]
    public async Task Raw_materials_are_sold_alone_or_with_finished_products()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
        var pizzeria = await CreateCustomerAsync(seller, "Pizzeria Lago");

        var rawOnly = await PostSalesOrderAsync(seller, pizzeria.Id, (cheese.Id, 120m));
        var mixed = await PostSalesOrderAsync(seller, pizzeria.Id, (cheese.Id, 20m), (product.Id, 50m));

        Assert.Equal(HttpStatusCode.Created, rawOnly.StatusCode);
        var rawLine = Assert.Single((await ApiJson.ReadAsync(rawOnly)).GetProperty("lines").EnumerateArray());
        AssertLine(rawLine, cheese.Id, "Cheese", "RawMaterial", "kg", 120m);
        AssertCustomer(await ApiJson.ReadAsync(rawOnly), pizzeria.Id, "Pizzeria Lago", isActive: true);
        Assert.Equal(HttpStatusCode.Created, mixed.StatusCode);
        Assert.Equal(["RawMaterial", "FinishedProduct"], (await ApiJson.ReadAsync(mixed)).GetProperty("lines").EnumerateArray()
            .Select(line => line.GetProperty("itemType").GetString()));
    }

    // ---- AC-24.7: no edit, no delete -------------------------------------------------------------------------------

    /// <summary>
    /// AC-24.7, design 4 and 7.2, architecture 4.1 item 18: <c>PUT</c> and <c>DELETE /sales-orders/1</c> give <c>405</c>
    /// <c>method-not-allowed</c> "This method is not allowed on this path.", and the sales order is unchanged.
    /// </summary>
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Put_and_delete_are_405(string method)
    {
        var scene = await SceneAsync();
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (scene.Product.Id, 5m));
        var before = (await ReadSalesOrderAsync(scene.Manager, number)).GetRawText();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{SalesOrdersPath}/{number}");
        if (method == "PUT")
        {
            request.Content = ApiJson.Content($$"""{ "customerId": {{scene.Customer.Id}}, "lines": [] }""");
        }

        var response = await scene.Manager.SendAsync(request, Cancel);

        var body = await ApiJson.ReadAsync(response);
        ApiAssert.Problem(response, body, HttpStatusCode.MethodNotAllowed, ApiAssert.NoTitleCheck,
            "method-not-allowed", "This method is not allowed on this path.");
        Assert.Equal(before, (await ReadSalesOrderAsync(scene.Manager, number)).GetRawText());
    }

    // ---- AC-24.9, AC-25.4, design 2.2: roles -----------------------------------------------------------------------

    /// <summary>
    /// AC-24.9, design 7.1: a user holding neither Seller nor Manager gets <c>403</c> "Your roles do not allow this action.
    /// It needs the Manager or Seller role." on create: Warehouse plus Production (the PRD's example), Warehouse only and
    /// Production only. Nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("Warehouse,Production")]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    public async Task Users_without_Seller_or_Manager_cannot_create(string roles)
    {
        var scene = await SceneAsync();
        var client = await factory.CreateClientAsAsync(roles.Split(','));
        var before = await CountSalesOrdersAsync(factory);

        var response = await PostSalesOrderAsync(client, scene.Customer.Id, (scene.Product.Id, 5m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
        Assert.Equal(before, await CountSalesOrdersAsync(factory));
    }

    /// <summary>
    /// AC-25.4, design 2.2 and 7.1: a Production-only user gets <c>403</c> "Your roles do not allow this action. It needs
    /// the Warehouse, Manager or Seller role." on the list and on the detail, also for a number that does not exist and for
    /// a status that is not valid (the role check comes before the <c>404</c> and the <c>400</c>). A Warehouse user gets
    /// <c>200</c>, <c>404</c> and <c>400</c> for the same requests, so the <c>403</c> really comes first.
    /// </summary>
    [Fact]
    public async Task Production_only_gets_403_on_the_reads_before_404_and_400()
    {
        var scene = await SceneAsync();
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (scene.Product.Id, 5m));
        var production = await factory.CreateClientAsAsync(Roles.Production);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        string[] paths = [SalesOrdersPath, $"{SalesOrdersPath}/{number}", $"{SalesOrdersPath}/{NoSuchRecord}", $"{SalesOrdersPath}?status=Done"];

        foreach (var path in paths)
        {
            await ApiAssert.ProblemAsync(await production.GetAsync(path, Cancel),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);
        }

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync(paths[0], Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync(paths[1], Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await warehouse.GetAsync(paths[2], Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await warehouse.GetAsync(paths[3], Cancel)).StatusCode);
    }

    /// <summary>AC-01.5: each endpoint answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Theory]
    [InlineData("GET", SalesOrdersPath)]
    [InlineData("GET", SalesOrdersPath + "/1")]
    [InlineData("POST", SalesOrdersPath)]
    public async Task Requests_without_a_token_are_401(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = ApiJson.Content("""{ "customerId": 1, "lines": [ { "itemId": 1, "quantity": 1 } ] }""");
        }

        var response = await anonymous.SendAsync(request, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- AC-25.1: list ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-25.1, design 2.5 and 5.12: the list is newest first by <c>createdAt</c>, then by number for the same second,
    /// paged with the total count. A sales order stored with an earlier time but the highest number comes last: time
    /// decides, not number. Each row has the summary shape, with <c>lineCount</c>, <c>statusChangedAt</c> and the
    /// customer. Warehouse users read the list too.
    /// </summary>
    [Fact]
    public async Task Sales_orders_are_listed_newest_first_in_pages()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(NineAm) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var (seller, user) = await OrderFlow.SignedInAsync(timed, Roles.Seller);
        var warehouse = await timed.CreateClientAsAsync(Roles.Warehouse);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var customer = await CreateCustomerAsync(seller);
        var first = await CreateSalesOrderAsync(seller, customer.Id, (item.Id, 1m), (other.Id, 2m));
        var sameSecond = await CreateSalesOrderAsync(seller, customer.Id, (item.Id, 1m));
        timed.FakeTime!.Advance(TimeSpan.FromMinutes(1));
        var newest = await CreateSalesOrderAsync(seller, customer.Id, (item.Id, 1m));
        var older = await InsertSalesOrderAsync(timed, customer.Id, item.Id, 1m, SalesOrderStatus.Created, user.Id);
        await BackdateAsync(timed, older, NineAm.UtcDateTime.AddDays(-1));

        var all = await ApiJson.ReadAsync(await warehouse.GetAsync(SalesOrdersPath, Cancel));
        var secondPage = await ApiJson.ReadAsync(await warehouse.GetAsync($"{SalesOrdersPath}?page=2&pageSize=3", Cancel));
        var pastTheEnd = await ApiJson.ReadAsync(await warehouse.GetAsync($"{SalesOrdersPath}?page=3&pageSize=3", Cancel));

        Assert.Equal([newest, sameSecond, first, older], Numbers(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        var top = all.GetProperty("items")[0];
        Assert.Equal(
            ["number", "customer", "customerReference", "status", "createdAt", "statusChangedAt", "lineCount"],
            top.EnumerateObject().Select(field => field.Name));
        AssertCustomer(top, customer.Id, customer.Name, isActive: true);
        Assert.Equal(JsonValueKind.Null, top.GetProperty("customerReference").ValueKind);
        Assert.Equal("Created", top.GetProperty("status").GetString());
        Assert.Equal("2026-12-01T09:01:00Z", top.GetProperty("createdAt").GetString());
        Assert.Equal("2026-12-01T09:01:00Z", top.GetProperty("statusChangedAt").GetString());
        Assert.Equal([1, 1, 2, 1], all.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("lineCount").GetInt32()));
        Assert.Equal([older], Numbers(secondPage));
        Assert.Equal(3, secondPage.GetProperty("pageSize").GetInt32());
        Assert.Empty(Numbers(pastTheEnd));
        Assert.Equal(4, pastTheEnd.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// AC-25.1, design 2.5 and 5.12: the <c>status</c> (any letter case), <c>itemId</c> (at least one line for the item)
    /// and <c>customerId</c> filters each work and combine with AND. A filter that matches nothing, including ids that do
    /// not exist, is <c>200</c> with an empty list. <c>statusChangedAt</c> of a Cancelled sales order is the time of its
    /// last history entry.
    /// </summary>
    [Fact]
    public async Task List_filters_by_status_item_and_customer_and_they_combine()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var (seller, user) = await OrderFlow.SignedInAsync(fresh, Roles.Seller);
        var x = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var y = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var a = await CreateCustomerAsync(seller);
        var b = await CreateCustomerAsync(seller);
        var aX = await CreateSalesOrderAsync(seller, a.Id, (x.Id, 1m));
        var aY = await CreateSalesOrderAsync(seller, a.Id, (y.Id, 1m));
        var bXY = await CreateSalesOrderAsync(seller, b.Id, (y.Id, 1m), (x.Id, 1m));
        var aXCancelled = await InsertSalesOrderAsync(fresh, a.Id, x.Id, 1m, SalesOrderStatus.Cancelled, user.Id);

        async Task<List<int>> ListAsync(string query) =>
            Numbers(await ApiJson.ReadAsync(await seller.GetAsync($"{SalesOrdersPath}?{query}", Cancel)));

        Assert.Equal([bXY, aY, aX], await ListAsync("status=Created"));
        Assert.Equal([aXCancelled], await ListAsync("status=cANCELLED"));
        Assert.Empty(await ListAsync("status=Shipped"));
        Assert.Equal([aXCancelled, bXY, aX], await ListAsync($"itemId={x.Id}"));
        Assert.Equal([aXCancelled, aY, aX], await ListAsync($"customerId={a.Id}"));
        Assert.Equal([aXCancelled, aX], await ListAsync($"customerId={a.Id}&itemId={x.Id}"));
        Assert.Equal([aX], await ListAsync($"customerId={a.Id}&itemId={x.Id}&status=created"));
        Assert.Empty(await ListAsync($"customerId={b.Id}&status=Cancelled"));
        Assert.Empty(await ListAsync($"itemId={NoSuchRecord}"));
        Assert.Empty(await ListAsync($"customerId={NoSuchRecord}"));

        var cancelledRow = (await ApiJson.ReadAsync(await seller.GetAsync($"{SalesOrdersPath}?status=Cancelled", Cancel))).GetProperty("items")[0];
        var cancelledDetail = await ReadSalesOrderAsync(seller, aXCancelled);
        Assert.Equal(
            cancelledDetail.GetProperty("history").EnumerateArray().Last().GetProperty("at").GetString(),
            cancelledRow.GetProperty("statusChangedAt").GetString());
    }

    /// <summary>
    /// AC-25.1, design 7.2 and 6: <c>status=Done</c> is <c>400</c> "status must be Created, Shipped or Cancelled." under
    /// <c>status</c>, reported together with a paging error in one response.
    /// </summary>
    [Fact]
    public async Task Unknown_status_is_400_together_with_paging_errors()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var alone = await seller.GetAsync($"{SalesOrdersPath}?status=Done", Cancel);
        var withPaging = await seller.GetAsync($"{SalesOrdersPath}?status=Done&pageSize=0", Cancel);

        await ApiAssert.ValidationAsync(alone, "status", ExpectedMessages.SalesOrderStatusValues);
        var body = await ApiAssert.ProblemAsync(withPaging, HttpStatusCode.BadRequest, "validation",
            $"pageSize must be between 1 and 100. {ExpectedMessages.SalesOrderStatusValues}");
        Assert.Equal([ExpectedMessages.SalesOrderStatusValues], ApiJson.Messages(body, "status"));
        Assert.Equal(["pageSize must be between 1 and 100."], ApiJson.Messages(body, "pageSize"));
    }

    /// <summary>
    /// Architecture 4.1 item 19, decision #113: an id filter that is not a whole number is <c>400</c> <c>validation</c>
    /// with MVC's own sentence under the query name (accepted framework wording, pinned here).
    /// </summary>
    [Theory]
    [InlineData("itemId")]
    [InlineData("customerId")]
    public async Task Id_filter_that_is_not_a_number_is_400(string filter)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await seller.GetAsync($"{SalesOrdersPath}?{filter}=abc", Cancel);

        await ApiAssert.ValidationAsync(response, filter, "The value 'abc' is not valid.");
    }

    // ---- AC-25.2, AC-25.3: detail ----------------------------------------------------------------------------------

    /// <summary>
    /// AC-25.2, AC-05.3, design 5.12: the detail of a Cancelled sales order fills <c>cancellationReason</c> and adds a
    /// Cancelled history entry, with who, when and the reason as its note, after the Created one; <c>shipmentNote</c>
    /// stays <c>null</c>. A Shipped one adds a Shipped entry with the shipment note (here none, so <c>null</c>), and
    /// <c>cancellationReason</c> stays <c>null</c>. (The rows are inserted straight in those states, until the ship and
    /// cancel endpoints exist, T-027 and T-028.)
    /// </summary>
    [Theory]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Shipped)]
    public async Task Detail_shows_every_status_reached_with_who_when_and_note(SalesOrderStatus status)
    {
        var scene = await SceneAsync();
        var number = await InsertSalesOrderAsync(factory, scene.Customer.Id, scene.Product.Id, 3m, status, scene.SellerUser.Id);
        var stored = await StoredSalesOrderAsync(factory, number);

        var detail = await ReadSalesOrderAsync(scene.Seller, number);

        Assert.Equal(status.ToString(), detail.GetProperty("status").GetString());
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Created", status.ToString()], history.Select(entry => entry.GetProperty("status").GetString()));
        AssertPerson(history[1].GetProperty("by"), scene.SellerUser);
        Assert.Equal(ApiTime(stored.StatusChangedAt), history[1].GetProperty("at").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("shipmentNote").ValueKind);
        if (status == SalesOrderStatus.Cancelled)
        {
            Assert.Equal("Customer changed the order", detail.GetProperty("cancellationReason").GetString());
            Assert.Equal("Customer changed the order", history[1].GetProperty("note").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("cancellationReason").ValueKind);
            Assert.Equal(JsonValueKind.Null, history[1].GetProperty("note").ValueKind);
        }
    }

    /// <summary>
    /// AC-25.3, design 7.2: an unknown number is <c>404</c> <c>not-found</c> "Sales order #999999 not found.", for every
    /// role that may read.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Seller)]
    public async Task Unknown_number_is_404(string role)
    {
        var client = await factory.CreateClientAsAsync(role);

        var response = await client.GetAsync($"{SalesOrdersPath}/{NoSuchRecord}", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"Sales order #{NoSuchRecord} not found.");
    }

    // ---- AC-29.3, AC-29.4: the customer's current name and status ---------------------------------------------------

    /// <summary>
    /// AC-29.3, PRD example: after "Deli Norte" is renamed "Deli Norte SA", the older sales order's detail and its list row
    /// both show the new name. The customer is read at request time, never copied onto the sales order.
    /// </summary>
    [Fact]
    public async Task Renamed_customer_shows_its_new_name_on_older_sales_orders()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var customer = await CreateCustomerAsync(seller, "Deli Norte");
        var number = await CreateSalesOrderAsync(seller, customer.Id, (item.Id, 600m));

        var rename = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}", new { name = "Deli Norte SA" }, Cancel);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        AssertCustomer(await ReadSalesOrderAsync(seller, number), customer.Id, "Deli Norte SA", isActive: true);
        var row = (await ApiJson.ReadAsync(await seller.GetAsync(SalesOrdersPath, Cancel))).GetProperty("items")[0];
        AssertCustomer(row, customer.Id, "Deli Norte SA", isActive: true);
    }

    /// <summary>
    /// AC-29.4: after the customer is deactivated, its existing sales order shows <c>customer.isActive: false</c> in the
    /// detail and in the list row; it stays listed and readable.
    /// </summary>
    [Fact]
    public async Task Deactivated_customer_is_shown_inactive_on_its_sales_orders()
    {
        var scene = await SceneAsync();
        var number = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (scene.Product.Id, 5m));

        await PostCustomerActionAsync(scene.Seller, scene.Customer.Id, "deactivate");

        AssertCustomer(await ReadSalesOrderAsync(scene.Seller, number), scene.Customer.Id, scene.Customer.Name, isActive: false);
        var row = (await ApiJson.ReadAsync(await scene.Seller.GetAsync($"{SalesOrdersPath}?customerId={scene.Customer.Id}", Cancel)))
            .GetProperty("items")[0];
        Assert.Equal(number, row.GetProperty("number").GetInt32());
        AssertCustomer(row, scene.Customer.Id, scene.Customer.Name, isActive: false);
    }

    // ---- AC-29.8: delete racing a new sales order -------------------------------------------------------------------

    /// <summary>
    /// AC-29.8, architecture 5.2: a customer delete and a sales order create for that customer at the same time, with
    /// each ordering forced by <see cref="ForcedOrder"/>, held at the first read of <c>Customers</c> and, separately, of
    /// <c>SalesOrders</c> (the delete's has-sales-orders check, the create's insert). Exactly one succeeds. If the delete
    /// is first: <c>204</c>, and the create, which waited for the write lock, gets <c>400</c> "Customer {id} does not
    /// exist." under <c>customerId</c>. If the create is first: <c>201</c>, and the delete gets <c>409</c>
    /// <c>customer-has-sales-orders</c>. Either way no sales order names a customer that does not exist.
    /// </summary>
    [Theory]
    [InlineData("Customers", "delete")]
    [InlineData("Customers", "create")]
    [InlineData("SalesOrders", "delete")]
    [InlineData("SalesOrders", "create")]
    public async Task Customer_delete_and_new_sales_order_exactly_one_succeeds(string table, string first)
    {
        await using var race = new ForcedOrder(table);
        var manager = await race.App.CreateClientAsAsync(Roles.Manager);
        var seller = await race.App.CreateClientAsAsync(Roles.Seller);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var customer = await CreateCustomerAsync(seller);

        Task<HttpResponseMessage> Delete() => manager.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel);
        Task<HttpResponseMessage> Create() => PostSalesOrderAsync(seller, customer.Id, (item.Id, 5m));

        if (first == "delete")
        {
            var (delete, create) = await race.RunAsync(Delete, Create);

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            await ApiAssert.ValidationAsync(create, "customerId", $"Customer {customer.Id} does not exist.");
            Assert.Equal(0, await CountSalesOrdersAsync(race.App));
        }
        else
        {
            var (create, delete) = await race.RunAsync(Create, Delete);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            await ApiAssert.ProblemAsync(delete, HttpStatusCode.Conflict, "customer-has-sales-orders",
                $"{customer.Name} has sales orders and cannot be deleted. Deactivate it instead.");
            Assert.Equal(1, await CountSalesOrdersAsync(race.App));
        }

        Assert.Equal(0, await SalesOrdersWithoutCustomerAsync(race.App));
    }

    // ---- Decision #163: logging ------------------------------------------------------------------------------------

    /// <summary>
    /// Decision #163, architecture 6 Logging: a created sales order is logged once, after the commit, as "Sales order {n}
    /// created by user {userId}."; a refused one (<c>409</c>) is not logged as created. The customer's notes never appear
    /// in a log line.
    /// </summary>
    [Fact]
    public async Task Created_sales_order_is_logged_with_number_and_user_and_notes_are_not()
    {
        var marker = $"phone-{Guid.NewGuid():N}";
        var scene = await SceneAsync();
        var customer = await CreateCustomerAsync(scene.Seller, notes: $"Call {marker}");
        var inactive = await CreateCustomerAsync(scene.Seller, notes: $"Call {marker}");
        await PostCustomerActionAsync(scene.Seller, inactive.Id, "deactivate");

        var number = await CreateSalesOrderAsync(scene.Seller, customer.Id, (scene.Product.Id, 5m));
        var refused = await PostSalesOrderAsync(scene.Seller, inactive.Id, (scene.Product.Id, 5m));
        await ReadSalesOrderAsync(scene.Seller, number);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var created = factory.Logs.Entries.Where(entry => entry.Message.StartsWith("Sales order ", StringComparison.Ordinal)
            && entry.Message.Contains(" created by user ", StringComparison.Ordinal)
            && entry.Message.Contains(scene.SellerUser.Id, StringComparison.Ordinal)).ToList();
        var entry = Assert.Single(created);
        Assert.Equal($"Sales order {number} created by user {scene.SellerUser.Id}.", entry.Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.DoesNotContain(factory.Logs.Entries, logged =>
            logged.Message.Contains(marker, StringComparison.Ordinal)
            || (logged.Exception?.ToString().Contains(marker, StringComparison.Ordinal) ?? false));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The records most tests in the shared database need, new for each test so tests never share them: a Manager and a
    /// Seller (with the Seller's user record), an active customer, a finished product and a raw material.
    /// </summary>
    private async Task<Scene> SceneAsync()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(factory, Roles.Seller);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var customer = await CreateCustomerAsync(seller);
        return new Scene(manager, seller, sellerUser, customer, product, material);
    }

    /// <summary>Replaces <c>{customer}</c> and <c>{item}</c> in a JSON template with the scene's customer and product ids.</summary>
    private static string Fill(string template, Scene scene) => template
        .Replace("{customer}", scene.Customer.Id.ToString(), StringComparison.Ordinal)
        .Replace("{item}", scene.Product.Id.ToString(), StringComparison.Ordinal);

    private static Task<HttpResponseMessage> PostWithReferenceAsync(Scene scene, string customerReference) =>
        scene.Seller.PostAsJsonAsync(SalesOrdersPath, new
        {
            customerId = scene.Customer.Id,
            customerReference,
            lines = new[] { new { itemId = scene.Product.Id, quantity = 1m } },
        }, Cancel);

    /// <summary>Creates two production orders for <paramref name="productId"/> and returns the second one's number.</summary>
    private static async Task<int> TwoProductionOrdersAsync(HttpClient manager, int productId)
    {
        await OrderFlow.CreateOrderAsync(manager, productId, 10m);
        return await OrderFlow.CreateOrderAsync(manager, productId, 10m);
    }

    /// <summary><c>POST /customers/{id}/{action}</c> (deactivate or reactivate), checked for <c>200</c>.</summary>
    private static async Task PostCustomerActionAsync(HttpClient client, int customerId, string action) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{CustomersPath}/{customerId}/{action}", content: null, Cancel)).StatusCode);

    /// <summary>The <c>number</c> of every row in a page of sales orders, in order.</summary>
    private static List<int> Numbers(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("number").GetInt32()).ToList();

    /// <summary>A stored time as the API writes it: UTC, whole seconds, with <c>Z</c> (architecture 4.1 item 9).</summary>
    private static string ApiTime(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Design 2.3: the <c>customer</c> of a sales order or row is exactly <c>{ id, name, isActive }</c> with these values.</summary>
    private static void AssertCustomer(JsonElement record, int id, string name, bool isActive)
    {
        var customer = record.GetProperty("customer");
        Assert.Equal(["id", "name", "isActive"], customer.EnumerateObject().Select(field => field.Name));
        Assert.Equal(id, customer.GetProperty("id").GetInt32());
        Assert.Equal(name, customer.GetProperty("name").GetString());
        Assert.Equal(isActive, customer.GetProperty("isActive").GetBoolean());
    }

    /// <summary>Design 2.3 "People": <c>{ id, email }</c> of <paramref name="user"/>.</summary>
    private static void AssertPerson(JsonElement person, AppUser user)
    {
        Assert.Equal(["id", "email"], person.EnumerateObject().Select(field => field.Name));
        Assert.Equal(user.Id, person.GetProperty("id").GetString());
        Assert.Equal(user.Email, person.GetProperty("email").GetString());
    }

    private static void AssertLine(JsonElement line, int itemId, string itemName, string itemType, string unit, decimal quantity)
    {
        Assert.Equal(itemId, line.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, line.GetProperty("itemName").GetString());
        Assert.Equal(itemType, line.GetProperty("itemType").GetString());
        Assert.Equal(unit, line.GetProperty("unit").GetString());
        Assert.Equal(quantity, line.GetProperty("quantity").GetDecimal());
    }

    private static async Task<SalesOrder> StoredSalesOrderAsync(TestApiFactory app, int number)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SalesOrders.AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Number == number, Cancel);
    }

    private static async Task<int> CountSalesOrdersAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SalesOrders.CountAsync(Cancel);
    }

    private static async Task<int> MovementCountAsync(TestApiFactory app, int itemId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Movements.CountAsync(movement => movement.ItemId == itemId, Cancel);
    }

    /// <summary>AC-29.8: how many sales orders name a customer id that has no <c>Customers</c> row. Must always be 0.</summary>
    private static async Task<int> SalesOrdersWithoutCustomerAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SalesOrders.CountAsync(order => !db.Customers.Any(customer => customer.Id == order.CustomerId), Cancel);
    }

    /// <summary>
    /// Moves a stored sales order's creation (and status change) time to <paramref name="createdAt"/>, so a test can hold
    /// one whose time and number disagree (an older time with a higher number). The app's clock cannot go backwards.
    /// </summary>
    private static async Task BackdateAsync(TestApiFactory app, int number, DateTime createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SalesOrders
            .Where(order => order.Number == number)
            .ExecuteUpdateAsync(set => set.SetProperty(order => order.CreatedAt, createdAt).SetProperty(order => order.StatusChangedAt, createdAt), Cancel);
    }

    /// <summary>The per-test records of <see cref="SceneAsync"/>.</summary>
    private sealed record Scene(
        HttpClient Manager, HttpClient Seller, AppUser SellerUser, CreatedCustomer Customer, CreatedItem Product, CreatedItem Material);
}
