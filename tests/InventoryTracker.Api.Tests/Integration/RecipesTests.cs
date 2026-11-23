using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-007: the recipe of a finished product, <c>GET</c> and <c>PUT /api/v1/items/{id}/recipe</c> (US-08, AC-05.1 for
/// these endpoints, design 5.4 and 7.2 "Items and recipes").
/// </summary>
/// <remarks>
/// Tests in this class share one database, so every test makes its own items with unique names. Tests that need exact
/// names (the AC-08.3 example) or a controlled clock create their own factory. AC-08.4 (orders keep the amounts they
/// were created with) needs production orders, so it is proved in T-009.
/// </remarks>
public class RecipesTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 41, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string RecipePath(int itemId) => $"/api/v1/items/{itemId}/recipe";

    // ---- AC-08.1 and AC-08.3: set and view -------------------------------------------------------------------

    /// <summary>
    /// AC-08.3, AC-08.1: the PRD example round-trips. The recipe of "Shredded cheese 1 kg bag" (unit Bag) is 1 kg of
    /// Cheese (unit kg). <c>PUT</c> answers <c>200</c> with exactly the design 5.4 fields, each line with its unit;
    /// <c>GET</c> returns the same body; and the stored amount plans 1,000 kg of Cheese for 1,000 bags (D-3 planned
    /// amount, which T-009 uses when it creates an order).
    /// </summary>
    [Fact]
    public async Task Prd_example_round_trips_with_units()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(Start.AddMilliseconds(600)) };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await CreateNamedItemAsync(manager, "Cheese", ItemType.RawMaterial, kg.Id);
        var product = await CreateNamedItemAsync(manager, "Shredded cheese 1 kg bag", ItemType.FinishedProduct, bag.Id);

        var put = await manager.PutAsJsonAsync(RecipePath(product),
            new { lines = new[] { new { itemId = cheese, amountPerUnit = 1m } } }, Cancel);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await ApiJson.ReadAsync(put);
        Assert.Equal(5, body.EnumerateObject().Count());
        Assert.Equal(product, body.GetProperty("finishedProductId").GetInt32());
        Assert.Equal("Shredded cheese 1 kg bag", body.GetProperty("finishedProductName").GetString());
        Assert.Equal("Bag", body.GetProperty("unit").GetString());

        // The clock is at 09:41:00.600; design 2.3: whole seconds, UTC, with Z.
        Assert.Equal("2026-11-11T09:41:00Z", body.GetProperty("updatedAt").GetString());

        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal(4, line.EnumerateObject().Count());
        Assert.Equal(cheese, line.GetProperty("itemId").GetInt32());
        Assert.Equal("Cheese", line.GetProperty("itemName").GetString());
        Assert.Equal("kg", line.GetProperty("unit").GetString());
        Assert.Equal(1m, line.GetProperty("amountPerUnit").GetDecimal());

        var get = await manager.GetAsync(RecipePath(product), Cancel);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(get)).GetRawText());

        // AC-08.3: 1,000 bags at the stored amount per bag plan 1,000 kg of Cheese.
        var stored = Assert.Single(await LoadLinesAsync(app, product));
        Assert.Equal(1000m, ProductionCalculations.PlannedAmount(stored.AmountPerUnit, 1000m));
    }

    /// <summary>
    /// AC-08.1: a recipe with several lines keeps them in the submitted order (stored as <c>LineNo</c> 1, 2, 3, the
    /// "recipe line order" of design 7.3), each line shows its own raw material's unit, and the database records who
    /// saved the recipe (AC-05.3).
    /// </summary>
    [Fact]
    public async Task Lines_keep_the_submitted_order_and_the_saver_is_recorded()
    {
        var email = Seed.UniqueEmail("recipes");
        var user = await factory.CreateUserAsync(email, Roles.Manager);
        var manager = factory.CreateClientWithToken(await factory.SignInAsync(email));
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync(RecipePath(product.Id), new
        {
            lines = new[]
            {
                new { itemId = starch.Id, amountPerUnit = 0.015m },
                new { itemId = cheese.Id, amountPerUnit = 1m },
                new { itemId = salt.Id, amountPerUnit = 0.000001m },
            },
        }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await ApiJson.ReadAsync(response)).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([starch.Id, cheese.Id, salt.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
        Assert.Equal([starch.Name, cheese.Name, salt.Name], lines.Select(line => line.GetProperty("itemName").GetString()));
        Assert.Equal([starch.UnitName, cheese.UnitName, salt.UnitName], lines.Select(line => line.GetProperty("unit").GetString()));
        Assert.Equal([0.015m, 1m, 0.000001m], lines.Select(line => line.GetProperty("amountPerUnit").GetDecimal()));

        var stored = await LoadLinesAsync(factory, product.Id);
        Assert.Equal([1, 2, 3], stored.Select(line => line.LineNo));
        Assert.Equal([starch.Id, cheese.Id, salt.Id], stored.Select(line => line.RawMaterialId));
        var recipe = await LoadRecipeAsync(factory, product.Id);
        Assert.Equal(user.Id, recipe!.UpdatedById);
    }

    /// <summary>
    /// AC-08.1: saving again replaces all previous lines. A material kept from the old list (Cheese), a material dropped
    /// (Starch) and a material added (Salt) end up as exactly the new list, in its order; <c>updatedAt</c> and the
    /// saver move to the second save. Keeping a material across saves also proves the old lines are gone before the
    /// new ones are written (one material per recipe is a unique index, AC-08.2).
    /// </summary>
    [Fact]
    public async Task Saving_again_replaces_all_previous_lines()
    {
        await using var app = new TestApiFactory { FakeTime = new FakeTimeProvider(Start) };
        var firstEmail = Seed.UniqueEmail("first");
        var secondEmail = Seed.UniqueEmail("second");
        await app.CreateUserAsync(firstEmail, Roles.Manager);
        var second = await app.CreateUserAsync(secondEmail, Roles.Manager);
        var firstManager = app.CreateClientWithToken(await app.SignInAsync(firstEmail));
        var secondManager = app.CreateClientWithToken(await app.SignInAsync(secondEmail));
        var product = await Seed.CreateItemAsync(firstManager, ItemType.FinishedProduct);
        var cheese = await Seed.CreateItemAsync(firstManager, ItemType.RawMaterial);
        var starch = await Seed.CreateItemAsync(firstManager, ItemType.RawMaterial);
        var salt = await Seed.CreateItemAsync(firstManager, ItemType.RawMaterial);

        var first = await firstManager.PutAsJsonAsync(RecipePath(product.Id), new
        {
            lines = new[] { new { itemId = cheese.Id, amountPerUnit = 1m }, new { itemId = starch.Id, amountPerUnit = 0.015m } },
        }, Cancel);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        app.FakeTime!.Advance(TimeSpan.FromMinutes(5));
        var replaced = await secondManager.PutAsJsonAsync(RecipePath(product.Id), new
        {
            lines = new[] { new { itemId = salt.Id, amountPerUnit = 0.002m }, new { itemId = cheese.Id, amountPerUnit = 1.01m } },
        }, Cancel);

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var body = await ApiJson.ReadAsync(replaced);
        Assert.Equal("2026-11-11T09:46:00Z", body.GetProperty("updatedAt").GetString());
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([salt.Id, cheese.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
        Assert.Equal([0.002m, 1.01m], lines.Select(line => line.GetProperty("amountPerUnit").GetDecimal()));

        var read = await firstManager.GetAsync(RecipePath(product.Id), Cancel);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(read)).GetRawText());

        // Only the new lines are left in the table, numbered from 1 again.
        var stored = await LoadLinesAsync(app, product.Id);
        Assert.Equal([(1, salt.Id), (2, cheese.Id)], stored.Select(line => (line.LineNo, line.RawMaterialId)));
        Assert.Equal(second.Id, (await LoadRecipeAsync(app, product.Id))!.UpdatedById);
    }

    /// <summary>
    /// AC-08.1, design 7.3 "recipe line order": <c>GET</c> lists lines by their stored <c>LineNo</c>, not by the order
    /// the rows were written. The rows are inserted directly with <c>LineNo</c> 2 first, which the API itself never does.
    /// </summary>
    [Fact]
    public async Task Lines_are_read_in_line_number_order()
    {
        var user = await factory.CreateUserAsync(Seed.UniqueEmail("recipes"), Roles.Manager);
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var second = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var first = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Recipes.Add(new Recipe { FinishedProductId = product.Id, UpdatedAt = DateTime.UtcNow, UpdatedById = user.Id });
            await db.SaveChangesAsync(Cancel);
            db.RecipeLines.Add(new RecipeLine { FinishedProductId = product.Id, LineNo = 2, RawMaterialId = second.Id, AmountPerUnit = 2m });
            await db.SaveChangesAsync(Cancel);
            db.RecipeLines.Add(new RecipeLine { FinishedProductId = product.Id, LineNo = 1, RawMaterialId = first.Id, AmountPerUnit = 1m });
            await db.SaveChangesAsync(Cancel);
        }

        var response = await manager.GetAsync(RecipePath(product.Id), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await ApiJson.ReadAsync(response)).GetProperty("lines").EnumerateArray();
        Assert.Equal([first.Id, second.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
    }

    /// <summary>
    /// AC-08.2, design 2.4: trailing zeros do not count towards the 6 decimal places, so 12.5000000 is accepted as 12.5;
    /// exactly 6 places (0.000001) is accepted.
    /// </summary>
    [Fact]
    public async Task Trailing_zeros_do_not_count_as_decimal_places()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content($$"""
            { "lines": [ { "itemId": {{cheese.Id}}, "amountPerUnit": 12.5000000 },
                         { "itemId": {{salt.Id}}, "amountPerUnit": 0.000001 } ] }
            """), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([12.5m, 0.000001m], (await LoadLinesAsync(factory, product.Id)).Select(line => line.AmountPerUnit));
    }

    /// <summary>AC-08.1 and AC-05.1 (PRD 4.2: all roles read master data): every role can view a recipe.</summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_view_a_recipe(string role)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (product, cheese) = await CreateProductWithRecipeAsync(manager);
        var reader = await factory.CreateClientAsAsync(role);

        var response = await reader.GetAsync(RecipePath(product.Id), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(product.Name, body.GetProperty("finishedProductName").GetString());
        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal(cheese.Id, line.GetProperty("itemId").GetInt32());
        Assert.Equal(cheese.UnitName, line.GetProperty("unit").GetString());
    }

    // ---- Design 5.4 and 7.2: no recipe, unknown item --------------------------------------------------------

    /// <summary>
    /// Design 5.4, 7.2: <c>GET</c> for a finished product with no recipe, or for a raw material, is <c>404</c>
    /// <c>not-found</c> "{item} has no recipe.".
    /// </summary>
    [Theory]
    [InlineData(ItemType.FinishedProduct)]
    [InlineData(ItemType.RawMaterial)]
    public async Task Viewing_an_item_without_a_recipe_is_404(ItemType type)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, type);
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var response = await reader.GetAsync(RecipePath(item.Id), Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", $"{item.Name} has no recipe.");
    }

    /// <summary>Design 7.2: the recipe of an item that does not exist is <c>404</c> "Item {id} not found.", for both methods.</summary>
    [Fact]
    public async Task Recipe_of_an_unknown_item_is_404()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var get = await manager.GetAsync(RecipePath(999999), Cancel);
        var put = await manager.PutAsJsonAsync(RecipePath(999999),
            new { lines = new[] { new { itemId = cheese.Id, amountPerUnit = 1m } } }, Cancel);

        await ApiAssert.ProblemAsync(get, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
        await ApiAssert.ProblemAsync(put, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    /// <summary>Design 2.2: the item in the URL (<c>404</c>) is checked before the body (<c>400</c>).</summary>
    [Fact]
    public async Task Unknown_item_is_404_even_with_an_invalid_body()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PutAsJsonAsync(RecipePath(999999), new { lines = Array.Empty<object>() }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    // ---- AC-08.2: rejected with 400 -------------------------------------------------------------------------

    /// <summary>
    /// AC-08.2 "a recipe for an item of type RawMaterial": <c>400</c> <c>recipe-not-finished-product</c> "Recipes are
    /// for finished products only. {item} is a raw material.", and no recipe is saved.
    /// </summary>
    [Fact]
    public async Task Recipe_for_a_raw_material_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync(RecipePath(material.Id),
            new { lines = new[] { new { itemId = other.Id, amountPerUnit = 1m } } }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "recipe-not-finished-product",
            $"Recipes are for finished products only. {material.Name} is a raw material.");
        Assert.Null(await LoadRecipeAsync(factory, material.Id));
    }

    /// <summary>
    /// Check order chosen for T-007: the raw-material rule is about the item in the URL, so it comes straight after the
    /// <c>404</c> and before the lines. A raw material with an invalid body still gets
    /// <c>recipe-not-finished-product</c>, because no change to the lines could make that recipe valid.
    /// </summary>
    [Fact]
    public async Task Raw_material_rule_comes_before_the_line_checks()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync(RecipePath(material.Id), new { lines = Array.Empty<object>() }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "recipe-not-finished-product",
            $"Recipes are for finished products only. {material.Name} is a raw material.");
    }

    /// <summary>
    /// AC-08.2, each case alone: <c>400</c> <c>validation</c> with the design 7.2 message under the line's JSON path
    /// (design 6), and nothing saved. Cases: no lines (empty, missing or null list); a line whose item is a finished
    /// product, including the product itself; the same raw item on two lines (the later line is named); an unknown
    /// item; an amount of zero or less; more than 6 decimal places; an amount above 999,999.999999 (decision #80: 1,000,000,
    /// which is also the limit plus 0.000001, written both ways); and a missing item or amount.
    /// </summary>
    /// <param name="template">
    /// The body. <c>{raw}</c> and <c>{raw2}</c> become raw materials, <c>{other}</c> another finished product and
    /// <c>{self}</c> the product in the URL.
    /// </param>
    /// <param name="path">The JSON path the error is listed under.</param>
    /// <param name="message">The message; <c>{rawName}</c> and <c>{otherName}</c>/<c>{selfName}</c> become item names.</param>
    [Theory]
    [InlineData("""{ "lines": [] }""", "lines", "A recipe needs at least one line.")]
    [InlineData("""{ }""", "lines", "A recipe needs at least one line.")]
    [InlineData("""{ "lines": null }""", "lines", "A recipe needs at least one line.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 1 }, { "itemId": {other}, "amountPerUnit": 1 } ] }""",
        "lines[1].itemId", "Recipe lines must be raw materials. {otherName} is a finished product.")]
    [InlineData("""{ "lines": [ { "itemId": {self}, "amountPerUnit": 1 } ] }""",
        "lines[0].itemId", "Recipe lines must be raw materials. {selfName} is a finished product.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 1 }, { "itemId": {raw2}, "amountPerUnit": 1 }, { "itemId": {raw}, "amountPerUnit": 2 } ] }""",
        "lines[2].itemId", "{rawName} appears on more than one line.")]
    [InlineData("""{ "lines": [ { "itemId": 999999, "amountPerUnit": 1 } ] }""",
        "lines[0].itemId", "Item 999999 does not exist.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 0 } ] }""",
        "lines[0].amountPerUnit", "Amount per unit must be greater than 0.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": -0.5 } ] }""",
        "lines[0].amountPerUnit", "Amount per unit must be greater than 0.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 1 }, { "itemId": {raw2}, "amountPerUnit": 0.0000001 } ] }""",
        "lines[1].amountPerUnit", "Amount per unit can have at most 6 decimal places.")]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 1000000 } ] }""",
        "lines[0].amountPerUnit", ExpectedMessages.AmountPerUnitAboveMax)]
    [InlineData("""{ "lines": [ { "itemId": {raw}, "amountPerUnit": 1000000.000000 } ] }""",
        "lines[0].amountPerUnit", ExpectedMessages.AmountPerUnitAboveMax)]
    [InlineData("""{ "lines": [ { "amountPerUnit": 1 } ] }""", "lines[0].itemId", "Item is required.")]
    [InlineData("""{ "lines": [ { "itemId": {raw} } ] }""", "lines[0].amountPerUnit", "Amount per unit is required.")]
    public async Task Invalid_recipe_is_rejected(string template, string path, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var other = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var raw = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var raw2 = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var json = template
            .Replace("{raw}", raw.Id.ToString())
            .Replace("{raw2}", raw2.Id.ToString())
            .Replace("{other}", other.Id.ToString())
            .Replace("{self}", product.Id.ToString());
        var expected = message
            .Replace("{rawName}", raw.Name)
            .Replace("{otherName}", other.Name)
            .Replace("{selfName}", product.Name);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, path, expected);
        Assert.Null(await LoadRecipeAsync(factory, product.Id));
    }

    /// <summary>
    /// AC-08.2, design 6: several bad lines are reported in one response, each message under its own line's path and
    /// all of them joined in <c>detail</c> in line order. A line sent as JSON <c>null</c> is missing both fields.
    /// </summary>
    [Fact]
    public async Task All_line_problems_are_reported_together()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var other = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content($$"""
            { "lines": [ { "itemId": {{other.Id}}, "amountPerUnit": 0 },
                         null,
                         { "itemId": 999999, "amountPerUnit": 1.1234567 } ] }
            """), Cancel);

        var finishedProduct = $"Recipe lines must be raw materials. {other.Name} is a finished product.";
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", string.Join(" ",
            finishedProduct,
            "Amount per unit must be greater than 0.",
            "Item is required.",
            "Amount per unit is required.",
            "Item 999999 does not exist.",
            "Amount per unit can have at most 6 decimal places."));
        Assert.Equal([finishedProduct], ApiJson.Messages(body, "lines[0].itemId"));
        Assert.Equal(["Amount per unit must be greater than 0."], ApiJson.Messages(body, "lines[0].amountPerUnit"));
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[1].itemId"));
        Assert.Equal(["Amount per unit is required."], ApiJson.Messages(body, "lines[1].amountPerUnit"));
        Assert.Equal(["Item 999999 does not exist."], ApiJson.Messages(body, "lines[2].itemId"));
        Assert.Equal(["Amount per unit can have at most 6 decimal places."], ApiJson.Messages(body, "lines[2].amountPerUnit"));
        Assert.Equal(6, body.GetProperty("errors").EnumerateObject().Count());
    }

    // ---- Decision #80: the amount maximum (T-008a) -----------------------------------------------------------

    /// <summary>
    /// T-008a, decision #80: an amount per unit of exactly 999,999.999999 (the largest allowed, with all 6 decimal
    /// places) is saved, and the <c>PUT</c> response, the <c>GET</c> and the stored row all hold it unchanged.
    /// </summary>
    [Fact]
    public async Task Amount_of_exactly_the_maximum_is_saved_and_read_back_unchanged()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var put = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content(
            $$"""{ "lines": [ { "itemId": {{cheese.Id}}, "amountPerUnit": 999999.999999 } ] }"""), Cancel);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = Assert.Single((await ApiJson.ReadAsync(put)).GetProperty("lines").EnumerateArray());
        Assert.Equal(999_999.999999m, saved.GetProperty("amountPerUnit").GetDecimal());
        var read = Assert.Single((await ApiJson.ReadAsync(await manager.GetAsync(RecipePath(product.Id), Cancel)))
            .GetProperty("lines").EnumerateArray());
        Assert.Equal(999_999.999999m, read.GetProperty("amountPerUnit").GetDecimal());
        Assert.Equal(999_999.999999m, Assert.Single(await LoadLinesAsync(factory, product.Id)).AmountPerUnit);
    }

    /// <summary>
    /// T-008a, decision #80, design 6: on a two-line recipe where only the second amount is above the maximum, the one
    /// error is listed under that line's path alone; the valid first line gets no error, and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Amount_above_the_maximum_is_reported_only_on_its_own_line()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content($$"""
            { "lines": [ { "itemId": {{cheese.Id}}, "amountPerUnit": 999999.999999 },
                         { "itemId": {{salt.Id}}, "amountPerUnit": 1000000 } ] }
            """), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            ExpectedMessages.AmountPerUnitAboveMax);
        var error = Assert.Single(body.GetProperty("errors").EnumerateObject());
        Assert.Equal("lines[1].amountPerUnit", error.Name);
        Assert.Equal([ExpectedMessages.AmountPerUnitAboveMax], ApiJson.Messages(body, "lines[1].amountPerUnit"));
        Assert.Null(await LoadRecipeAsync(factory, product.Id));
    }

    /// <summary>
    /// T-008a, decision #75 and architecture 3.1 "Order of checks on one field": the maximum is the last rule on
    /// <c>amountPerUnit</c> (required, greater than 0, 6 decimal places, maximum). An amount that is both above the limit
    /// and breaks an earlier rule gets only the earlier rule's message.
    /// </summary>
    [Theory]
    [InlineData("1000000.0000001", "Amount per unit can have at most 6 decimal places.")]
    [InlineData("-1000000", "Amount per unit must be greater than 0.")]
    public async Task Maximum_is_the_last_rule_on_the_amount(string amount, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content(
            $$"""{ "lines": [ { "itemId": {{cheese.Id}}, "amountPerUnit": {{amount}} } ] }"""), Cancel);

        await ApiAssert.ValidationAsync(response, "lines[0].amountPerUnit", message);
        Assert.Null(await LoadRecipeAsync(factory, product.Id));
    }

    /// <summary>
    /// PRD 4.1 "rejected": nothing is saved. An invalid save leaves the previous recipe exactly as it was, including its
    /// lines and <c>updatedAt</c>.
    /// </summary>
    [Fact]
    public async Task Rejected_save_keeps_the_previous_recipe()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (product, cheese) = await CreateProductWithRecipeAsync(manager);
        var before = await ApiJson.ReadAsync(await manager.GetAsync(RecipePath(product.Id), Cancel));

        var response = await manager.PutAsJsonAsync(RecipePath(product.Id), new
        {
            lines = new[] { new { itemId = cheese.Id, amountPerUnit = 2m }, new { itemId = cheese.Id, amountPerUnit = 3m } },
        }, Cancel);

        await ApiAssert.ValidationAsync(response, "lines[1].itemId", $"{cheese.Name} appears on more than one line.");
        var after = await ApiJson.ReadAsync(await manager.GetAsync(RecipePath(product.Id), Cancel));
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal([1m], (await LoadLinesAsync(factory, product.Id)).Select(line => line.AmountPerUnit));
    }

    /// <summary>
    /// PRD section 5 ("completes fully or saves nothing"), architecture 5.2, AC-08.1: when the database write itself
    /// fails, after the old lines have already been marked for deletion, the transaction rolls back and the previous
    /// recipe is still there exactly as it was. The failure is forced by a <see cref="BeforeSaveInterceptor"/> on the
    /// first save carrying deleted recipe lines: inside the request's transaction it really deletes the old lines in
    /// the database, then throws. So the old lines survive only if the transaction is rolled back. The client gets the
    /// plain <c>500</c> <c>internal-error</c>.
    /// </summary>
    [Fact]
    public async Task Failed_write_keeps_the_previous_recipe()
    {
        // Set once the product exists; the interceptor is built before the app starts, so it reads this variable later.
        var productId = 0;
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<RecipeLine>().Any(entry => entry.State == EntityState.Deleted),
            work: async (context, cancellationToken) =>
            {
                // ExecuteSqlAsync turns the interpolated value into a SQL parameter, not text pasted into the query.
                await context.Database.ExecuteSqlAsync(
                    $"DELETE FROM RecipeLines WHERE FinishedProductId = {productId}", cancellationToken);
                throw new InvalidOperationException("Simulated database failure while replacing recipe lines.");
            });
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var (product, cheese) = await CreateProductWithRecipeAsync(manager);
        productId = product.Id;
        var salt = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var before = await ApiJson.ReadAsync(await manager.GetAsync(RecipePath(product.Id), Cancel));

        var response = await manager.PutAsJsonAsync(RecipePath(product.Id), new
        {
            lines = new[] { new { itemId = salt.Id, amountPerUnit = 0.5m } },
        }, Cancel);

        Assert.True(interceptor.HasRun);
        var body = await ApiJson.ReadAsync(response);
        ApiAssert.Problem(response, body, HttpStatusCode.InternalServerError, ApiAssert.NoTitleCheck, "internal-error",
            "An unexpected error occurred.");
        var after = await ApiJson.ReadAsync(await manager.GetAsync(RecipePath(product.Id), Cancel));
        Assert.Equal(before.GetRawText(), after.GetRawText());
        var stored = Assert.Single(await LoadLinesAsync(app, product.Id));
        Assert.Equal((1, cheese.Id, 1m), (stored.LineNo, stored.RawMaterialId, stored.AmountPerUnit));
    }

    /// <summary>
    /// Architecture 4.1 item 2, design 2.4: an amount sent as a string is <c>400</c> "lines[0].amountPerUnit has the
    /// wrong type.", and no body or a JSON <c>null</c> body is <c>400</c> "A request body is required.". Nothing is saved.
    /// </summary>
    [Theory]
    [InlineData("""{ "lines": [ { "itemId": 1, "amountPerUnit": "1" } ] }""", "lines[0].amountPerUnit has the wrong type.")]
    [InlineData("", "A request body is required.")]
    [InlineData("null", "A request body is required.")]
    public async Task Unreadable_body_is_rejected(string json, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var response = await manager.PutAsync(RecipePath(product.Id), ApiJson.Content(json), Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Null(await LoadRecipeAsync(factory, product.Id));
    }

    // ---- AC-07.3, AC-07.4: a recipe puts its items in use ----------------------------------------------------

    /// <summary>
    /// AC-07.3, AC-07.4 with recipes saved through the API: once a recipe exists, its raw material and its finished
    /// product are in use. Neither their type nor their unit can change (<c>409</c> <c>item-in-use</c>), and neither
    /// can be deleted.
    /// </summary>
    [Fact]
    public async Task Recipe_puts_its_product_and_materials_in_use()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (product, cheese) = await CreateProductWithRecipeAsync(manager);
        var otherUnit = await Seed.CreateUnitAsync(manager);

        var materialTypeChange = await manager.PutAsJsonAsync($"/api/v1/items/{cheese.Id}",
            new { name = cheese.Name, type = "FinishedProduct", unitId = cheese.UnitId }, Cancel);
        var productTypeChange = await manager.PutAsJsonAsync($"/api/v1/items/{product.Id}",
            new { name = product.Name, type = "RawMaterial", unitId = product.UnitId }, Cancel);
        var materialUnitChange = await manager.PutAsJsonAsync($"/api/v1/items/{cheese.Id}",
            new { name = cheese.Name, type = "RawMaterial", unitId = otherUnit.Id }, Cancel);
        var productUnitChange = await manager.PutAsJsonAsync($"/api/v1/items/{product.Id}",
            new { name = product.Name, type = "FinishedProduct", unitId = otherUnit.Id }, Cancel);
        var materialDelete = await manager.DeleteAsync($"/api/v1/items/{cheese.Id}", Cancel);
        var productDelete = await manager.DeleteAsync($"/api/v1/items/{product.Id}", Cancel);

        const string locked = "Type and unit cannot change once the item is in use.";
        await ApiAssert.ProblemAsync(materialTypeChange, HttpStatusCode.Conflict, "item-in-use", locked);
        await ApiAssert.ProblemAsync(productTypeChange, HttpStatusCode.Conflict, "item-in-use", locked);
        await ApiAssert.ProblemAsync(materialUnitChange, HttpStatusCode.Conflict, "item-in-use", locked);
        await ApiAssert.ProblemAsync(productUnitChange, HttpStatusCode.Conflict, "item-in-use", locked);
        await ApiAssert.ProblemAsync(materialDelete, HttpStatusCode.Conflict, "item-in-use",
            $"{cheese.Name} has movements, recipe lines, order lines or sales order lines and cannot be deleted.");
        await ApiAssert.ProblemAsync(productDelete, HttpStatusCode.Conflict, "item-in-use",
            $"{product.Name} has movements, recipe lines, order lines or sales order lines and cannot be deleted.");
    }

    // ---- AC-01.5 and AC-05.1: access -------------------------------------------------------------------------

    /// <summary>
    /// AC-01.5: both recipe endpoints answer <c>401</c> <c>not-authenticated</c> without a token. (<c>GET</c> is open to
    /// every role, so it has no <c>403</c> case: every user holds at least one role, AC-03.3.)
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Requests_without_a_token_are_401(string method)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), RecipePath(1));
        if (method == "PUT")
        {
            request.Content = ApiJson.Content("""{ "lines": [ { "itemId": 2, "amountPerUnit": 1 } ] }""");
        }

        var response = await anonymous.SendAsync(request, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// AC-05.1 (PRD 4.2, "Manage units, items, recipes"): setting a recipe is Manager-only. Users without the Manager
    /// role, even with both other roles, get <c>403</c> naming the Manager role, and the recipe does not change.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData("Warehouse,Production")]
    public async Task Setting_a_recipe_without_the_manager_role_is_403(string roles)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (product, _) = await CreateProductWithRecipeAsync(manager);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var caller = await factory.CreateClientAsAsync(roles.Split(','));

        var response = await caller.PutAsJsonAsync(RecipePath(product.Id),
            new { lines = new[] { new { itemId = other.Id, amountPerUnit = 5m } } }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        Assert.Equal([1m], (await LoadLinesAsync(factory, product.Id)).Select(line => line.AmountPerUnit));
    }

    /// <summary>Design 2.2: the role check (<c>403</c>) comes before the record check (<c>404</c>).</summary>
    [Fact]
    public async Task Role_check_comes_before_the_404()
    {
        var production = await factory.CreateClientAsAsync(Roles.Production);

        var response = await production.PutAsJsonAsync(RecipePath(999999),
            new { lines = new[] { new { itemId = 1, amountPerUnit = 1m } } }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates an item with an exact name through <c>POST /api/v1/items</c> and returns its id. Only for tests with
    /// their own factory: names are unique, so a fixed name could clash in the shared database.
    /// </summary>
    private static async Task<int> CreateNamedItemAsync(HttpClient manager, string name, ItemType type, int unitId)
    {
        var response = await manager.PostAsJsonAsync("/api/v1/items", new { name, type = type.ToString(), unitId }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("id").GetInt32();
    }

    /// <summary>A finished product whose recipe, saved through the API, is one line: a new raw material at 1 per unit.</summary>
    private static async Task<(CreatedItem Product, CreatedItem Material)> CreateProductWithRecipeAsync(HttpClient manager)
    {
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var response = await manager.PutAsJsonAsync(RecipePath(product.Id),
            new { lines = new[] { new { itemId = material.Id, amountPerUnit = 1m } } }, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (product, material);
    }

    /// <summary>The stored recipe row of a product, or null when it has none, read straight from the database.</summary>
    private static async Task<Recipe?> LoadRecipeAsync(TestApiFactory app, int productId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Recipes.AsNoTracking().SingleOrDefaultAsync(recipe => recipe.FinishedProductId == productId, Cancel);
    }

    /// <summary>The stored lines of a product's recipe, in <c>LineNo</c> order, read straight from the database.</summary>
    private static async Task<List<RecipeLine>> LoadLinesAsync(TestApiFactory app, int productId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RecipeLines.AsNoTracking()
            .Where(line => line.FinishedProductId == productId)
            .OrderBy(line => line.LineNo)
            .ToListAsync(Cancel);
    }
}
