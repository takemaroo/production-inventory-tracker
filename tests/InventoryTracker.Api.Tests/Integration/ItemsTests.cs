using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-006: the <c>/api/v1/items</c> endpoints (US-07, AC-05.1 for these endpoints, design 5.4 and 7.2 "Items").
/// </summary>
/// <remarks>
/// Tests in this class share one database, so every test uses unique names. The "in use" tests (AC-07.3, AC-07.4)
/// insert the referencing rows (movement, recipe, order line and so on) directly through the DbContext, because the
/// endpoints that create them come in later tasks. Tests that need an exact list create their own factory.
/// </remarks>
public class ItemsTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string ItemsPath = "/api/v1/items";
    private const string TypeAndUnitLockedMessage = "Type and unit cannot change once the item is in use.";
    private const string TypeMessage = "Type must be RawMaterial or FinishedProduct.";

    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-07.1: create ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-07.1, AC-07.5, design 5.4: a Manager creates a raw material. <c>201</c>, <c>Location</c> pointing at it, and the
    /// body has exactly the seven design fields: trimmed name, null description, type, unit id and unit name, and a UTC
    /// creation time in whole seconds.
    /// </summary>
    [Fact]
    public async Task Manager_creates_an_item()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var name = Seed.UniqueName("Cheese");

        var response = await manager.PostAsJsonAsync(ItemsPath,
            new { name = $"  {name}  ", type = "RawMaterial", unitId = unit.Id }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"{ItemsPath}/{id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.Equal("RawMaterial", body.GetProperty("type").GetString());
        Assert.Equal(unit.Id, body.GetProperty("unitId").GetInt32());
        Assert.Equal(unit.Name, body.GetProperty("unit").GetString());
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), body.GetProperty("createdAt").GetString());
        Assert.Equal(7, body.EnumerateObject().Count());

        var read = await manager.GetAsync(response.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(read)).GetRawText());
    }

    /// <summary>
    /// AC-07.1: the description is optional and trimmed (blank is stored as none); the type is accepted in any letter
    /// case and returned in the design's form (design 2.3). The creation time comes from the app's clock.
    /// </summary>
    [Fact]
    public async Task Item_with_description_and_type_in_any_letter_case()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start.AddMilliseconds(700)) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);

        var described = await manager.PostAsJsonAsync(ItemsPath, new
        {
            name = "Shredded cheese 1 kg bag", description = "  Mozzarella blend, retail pack  ", type = "finishedPRODUCT", unitId = unit.Id,
        }, Cancel);
        var blank = await manager.PostAsJsonAsync(ItemsPath,
            new { name = "Potato starch", description = "   ", type = "rawmaterial", unitId = unit.Id }, Cancel);

        var describedBody = await ApiJson.ReadAsync(described);
        Assert.Equal(HttpStatusCode.Created, described.StatusCode);
        Assert.Equal("Mozzarella blend, retail pack", describedBody.GetProperty("description").GetString());
        Assert.Equal("FinishedProduct", describedBody.GetProperty("type").GetString());
        Assert.Equal("2026-11-11T09:30:00Z", describedBody.GetProperty("createdAt").GetString());

        var blankBody = await ApiJson.ReadAsync(blank);
        Assert.Equal(HttpStatusCode.Created, blank.StatusCode);
        Assert.Equal(JsonValueKind.Null, blankBody.GetProperty("description").ValueKind);
        Assert.Equal("RawMaterial", blankBody.GetProperty("type").GetString());
    }

    /// <summary>Design 2.6: a name of 100 characters and a description of 500 are accepted (counted after trimming).</summary>
    [Fact]
    public async Task Longest_name_and_description_are_accepted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var name = Seed.UniqueName("n").PadRight(100, 'x');

        var response = await manager.PostAsJsonAsync(ItemsPath,
            new { name = $" {name} ", description = new string('d', 500), type = "RawMaterial", unitId = unit.Id }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(name, (await ApiJson.ReadAsync(response)).GetProperty("name").GetString());
    }

    // ---- AC-07.2: invalid input and duplicates ---------------------------------------------------------------

    /// <summary>
    /// AC-07.2: a missing name, an unknown type and an unknown unit are each rejected with <c>400</c>; when all three are
    /// wrong, one response names every field, in body order (design 6), and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Missing_name_unknown_type_and_unknown_unit_are_reported_together()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var before = await CountItemsAsync();

        var response = await manager.PostAsJsonAsync(ItemsPath, new { name = "   ", type = "Gadget", unitId = 999999 }, Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"Name is required. {TypeMessage} Unit 999999 does not exist.");
        Assert.Equal(["Name is required."], ApiJson.Messages(body, "name"));
        Assert.Equal([TypeMessage], ApiJson.Messages(body, "type"));
        Assert.Equal(["Unit 999999 does not exist."], ApiJson.Messages(body, "unitId"));
        Assert.Equal(before, await CountItemsAsync());
    }

    /// <summary>
    /// AC-07.2 and design 7.2: each field rule on its own, with the field named in <c>errors</c>. A missing type and a
    /// number given as the type name are "unknown" too (architecture 4.1 item 3).
    /// </summary>
    [Theory]
    [InlineData("""{ "type": "RawMaterial", "unitId": {unit} }""", "name", "Name is required.")]
    [InlineData("""{ "name": "{name}", "unitId": {unit} }""", "type", TypeMessage)]
    [InlineData("""{ "name": "{name}", "type": "1", "unitId": {unit} }""", "type", TypeMessage)]
    [InlineData("""{ "name": "{name}", "type": "", "unitId": {unit} }""", "type", TypeMessage)]
    [InlineData("""{ "name": "{name}", "type": "RawMaterial" }""", "unitId", "Unit is required.")]
    [InlineData("""{ "name": "{name}", "type": "RawMaterial", "unitId": 999999 }""", "unitId", "Unit 999999 does not exist.")]
    [InlineData("""{ "name": "{long-name}", "type": "RawMaterial", "unitId": {unit} }""", "name", "Name must be 100 characters or fewer.")]
    [InlineData("""{ "name": "{name}", "description": "{long-description}", "type": "RawMaterial", "unitId": {unit} }""", "description", "Description must be 500 characters or fewer.")]
    [InlineData("""{ "name": "{name}", "type": 1, "unitId": {unit} }""", "type", "type has the wrong type.")]
    [InlineData("""{ "name": "{name}", "type": "RawMaterial", "unitId": "{unit}" }""", "unitId", "unitId has the wrong type.")]
    public async Task Invalid_field_is_rejected(string template, string path, string message)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var json = template
            .Replace("{unit}", unit.Id.ToString())
            .Replace("{long-name}", new string('x', 101))
            .Replace("{long-description}", new string('d', 501))
            .Replace("{name}", Seed.UniqueName("Bad"));
        var before = await CountItemsAsync();

        var response = await manager.PostAsync(ItemsPath, ApiJson.Content(json), Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Equal([message], ApiJson.Messages(body, path));
        Assert.Equal(before, await CountItemsAsync());
    }

    /// <summary>
    /// AC-07.1, AC-07.2: a duplicate name, compared without regard to case and after trimming, is <c>409</c>
    /// <c>item-name-taken</c>, even with a different type and unit, and no second item is saved.
    /// </summary>
    [Fact]
    public async Task Duplicate_name_ignoring_case_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var first = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var otherUnit = await Seed.CreateUnitAsync(manager);
        var duplicate = $"  {first.Name.ToLowerInvariant()} ";

        var response = await manager.PostAsJsonAsync(ItemsPath,
            new { name = duplicate, type = "FinishedProduct", unitId = otherUnit.Id }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "item-name-taken",
            $"An item named '{first.Name.ToLowerInvariant()}' already exists.");
        Assert.Equal(1, await CountItemsNamedAsync(first.Name));
    }

    // ---- AC-07.5: list and view -----------------------------------------------------------------------------

    /// <summary>AC-07.5 and AC-05.1: every role can list items and view one; each shows its type and unit.</summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_list_and_view_items(string role)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var reader = await factory.CreateClientAsAsync(role);

        var list = await reader.GetAsync($"{ItemsPath}?pageSize=100", Cancel);
        var one = await reader.GetAsync($"{ItemsPath}/{item.Id}", Cancel);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = Assert.Single((await ApiJson.ReadAsync(list)).GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("id").GetInt32() == item.Id);
        Assert.Equal("FinishedProduct", row.GetProperty("type").GetString());
        Assert.Equal(item.UnitName, row.GetProperty("unit").GetString());
        Assert.Equal(7, row.EnumerateObject().Count());

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var body = await ApiJson.ReadAsync(one);
        Assert.Equal("FinishedProduct", body.GetProperty("type").GetString());
        Assert.Equal(item.UnitId, body.GetProperty("unitId").GetInt32());
        Assert.Equal(item.UnitName, body.GetProperty("unit").GetString());
    }

    /// <summary>
    /// PRD 4.1, design 2.5: items are listed newest first by creation time, then by id for the same second, in pages
    /// with the total count. Design 5.4: <c>?type=</c> filters by type, in any letter case.
    /// </summary>
    [Fact]
    public async Task Items_are_listed_newest_first_and_filtered_by_type()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start) };
        var manager = await timed.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var first = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);
        var sameSecond = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit);
        timed.FakeTime!.Advance(TimeSpan.FromMinutes(1));
        var newest = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, unit);

        // Created last, so it has the highest id, but a creation time one day earlier: time decides, not id.
        var olderId = await InsertItemAsync(timed, unit.Id, Start.UtcDateTime.AddDays(-1));

        var all = await ApiJson.ReadAsync(await manager.GetAsync(ItemsPath, Cancel));
        Assert.Equal([newest.Id, sameSecond.Id, first.Id, olderId], ApiJson.Ids(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        Assert.Equal("2026-11-11T09:31:00Z", all.GetProperty("items")[0].GetProperty("createdAt").GetString());

        var secondPage = await ApiJson.ReadAsync(await manager.GetAsync($"{ItemsPath}?page=2&pageSize=3", Cancel));
        Assert.Equal([olderId], ApiJson.Ids(secondPage));
        Assert.Equal(4, secondPage.GetProperty("totalCount").GetInt32());

        var raw = await ApiJson.ReadAsync(await manager.GetAsync($"{ItemsPath}?type=RawMaterial", Cancel));
        Assert.Equal([newest.Id, first.Id, olderId], ApiJson.Ids(raw));
        Assert.Equal(3, raw.GetProperty("totalCount").GetInt32());

        var finished = await ApiJson.ReadAsync(await manager.GetAsync($"{ItemsPath}?type=finishedproduct", Cancel));
        Assert.Equal([sameSecond.Id], ApiJson.Ids(finished));
        Assert.Equal(1, finished.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// Design 2.5: a filter value in the wrong format is <c>400</c> (numbers are not type names), and it is reported
    /// together with a bad page size.
    /// </summary>
    [Theory]
    [InlineData("Gadget")]
    [InlineData("1")]
    public async Task Unknown_type_filter_is_rejected(string type)
    {
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var response = await reader.GetAsync($"{ItemsPath}?type={type}&pageSize=0", Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "pageSize must be between 1 and 100. type must be RawMaterial or FinishedProduct.");
        Assert.Equal(["type must be RawMaterial or FinishedProduct."], ApiJson.Messages(body, "type"));
        Assert.Equal(["pageSize must be between 1 and 100."], ApiJson.Messages(body, "pageSize"));
    }

    /// <summary>Design 7.2: viewing an item that does not exist is <c>404</c> "Item {id} not found.".</summary>
    [Fact]
    public async Task Viewing_an_unknown_item_is_404()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await reader.GetAsync($"{ItemsPath}/999999", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    // ---- AC-07.3: change ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-07.3: while an item is not in use, a Manager can change every field, including type and unit. The response
    /// and later reads show the new values; the creation time does not change.
    /// </summary>
    [Fact]
    public async Task Every_field_can_change_while_the_item_is_not_in_use()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var newUnit = await Seed.CreateUnitAsync(manager);
        var newName = Seed.UniqueName("Renamed");

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/{item.Id}",
            new { name = $" {newName} ", description = "Now a product", type = "FinishedProduct", unitId = newUnit.Id }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(item.Id, body.GetProperty("id").GetInt32());
        Assert.Equal(newName, body.GetProperty("name").GetString());
        Assert.Equal("Now a product", body.GetProperty("description").GetString());
        Assert.Equal("FinishedProduct", body.GetProperty("type").GetString());
        Assert.Equal(newUnit.Id, body.GetProperty("unitId").GetInt32());
        Assert.Equal(newUnit.Name, body.GetProperty("unit").GetString());
        Assert.Equal(item.CreatedAt, body.GetProperty("createdAt").GetString());
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(await manager.GetAsync($"{ItemsPath}/{item.Id}", Cancel))).GetRawText());

        var stored = await LoadItemAsync(item.Id);
        Assert.Equal(NameNormalizer.Normalize(newName), stored!.NameNormalized);
    }

    /// <summary>AC-07.3 (full replacement, design 5.4): leaving out the description removes it.</summary>
    [Fact]
    public async Task Leaving_out_the_description_removes_it()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, description: "Old text");

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/{item.Id}",
            new { name = item.Name, type = "RawMaterial", unitId = item.UnitId }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ApiJson.ReadAsync(response)).GetProperty("description").ValueKind);
        Assert.Null((await LoadItemAsync(item.Id))!.Description);
    }

    /// <summary>
    /// AC-07.3 and AC-07.4, with every reference architecture 3.2 counts as "in use": a movement, a recipe line, the item's
    /// own recipe, an order line, a production order for it, a complementary-order line, a return and (v1.1, AC-28.5) a
    /// sales order line, here on a Cancelled sales order to show "whatever the sales order's status". For each one: name
    /// and description still change (<c>200</c>); a type or unit change is <c>409</c> with the PRD message; a delete is
    /// <c>409</c> with the design 7.2 message; and the item keeps its type and unit.
    /// </summary>
    [Theory]
    [InlineData("movement", ItemType.RawMaterial)]
    [InlineData("recipe line", ItemType.RawMaterial)]
    [InlineData("own recipe", ItemType.FinishedProduct)]
    [InlineData("order line", ItemType.RawMaterial)]
    [InlineData("production order", ItemType.FinishedProduct)]
    [InlineData("complementary order line", ItemType.RawMaterial)]
    [InlineData("return", ItemType.RawMaterial)]
    [InlineData("sales order line", ItemType.FinishedProduct)]
    public async Task Item_in_use_keeps_its_type_and_unit_and_cannot_be_deleted(string reference, ItemType type)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, type);
        var otherUnit = await Seed.CreateUnitAsync(manager);
        var otherType = type == ItemType.RawMaterial ? "FinishedProduct" : "RawMaterial";
        await AddReferenceAsync(reference, item.Id, item.UnitId);
        var path = $"{ItemsPath}/{item.Id}";
        var newName = Seed.UniqueName("Still");

        // AC-07.3: name and description can be changed at any time; sending the stored type and unit again is not a change.
        var rename = await manager.PutAsJsonAsync(path,
            new { name = newName, description = "Changed while in use", type = type.ToString(), unitId = item.UnitId }, Cancel);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(newName, (await ApiJson.ReadAsync(rename)).GetProperty("name").GetString());

        var typeChange = await manager.PutAsJsonAsync(path,
            new { name = newName, type = otherType, unitId = item.UnitId }, Cancel);
        await ApiAssert.ProblemAsync(typeChange, HttpStatusCode.Conflict, "item-in-use", TypeAndUnitLockedMessage);

        var unitChange = await manager.PutAsJsonAsync(path,
            new { name = newName, type = type.ToString(), unitId = otherUnit.Id }, Cancel);
        await ApiAssert.ProblemAsync(unitChange, HttpStatusCode.Conflict, "item-in-use", TypeAndUnitLockedMessage);

        // AC-07.4, (v1.1) AC-28.5: the design 7.2 message names sales order lines too.
        var delete = await manager.DeleteAsync(path, Cancel);
        await ApiAssert.ProblemAsync(delete, HttpStatusCode.Conflict, "item-in-use",
            $"{newName} has movements, recipe lines, order lines or sales order lines and cannot be deleted.");

        var stored = await LoadItemAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(type, stored.Type);
        Assert.Equal(item.UnitId, stored.UnitId);
        Assert.Equal("Changed while in use", stored.Description);
    }

    /// <summary>
    /// AC-07.3, AC-07.4, (v1.1) AC-28.5: <c>ItemService.IsInUseAsync</c> checks a hand-written list of eight references. This test reads
    /// every foreign key that points at <see cref="Item"/> from the EF Core model and requires exactly that list, so a
    /// foreign key added later fails here until the service (and the theory above) check it too. Otherwise the new
    /// reference would let type and unit change under it, and a delete would hit the Restrict foreign key and answer
    /// <c>500</c> instead of <c>409</c> (decision #46).
    /// </summary>
    [Fact]
    public async Task Item_in_use_check_covers_every_foreign_key_to_items()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;

        var references = model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Item))
            .Select(foreignKey =>
                $"{foreignKey.DeclaringEntityType.ClrType.Name}.{string.Join(",", foreignKey.Properties.Select(property => property.Name))}")
            .Order(StringComparer.Ordinal)
            .ToList();

        // Exactly the eight references ItemService.IsInUseAsync checks, sorted here by name.
        Assert.Equal(
            [
                "ComplementaryOrderLine.RawMaterialId",
                "LeftoverReturn.ItemId",
                "Movement.ItemId",
                "ProductionOrder.FinishedProductId",
                "ProductionOrderLine.RawMaterialId",
                "Recipe.FinishedProductId",
                "RecipeLine.RawMaterialId",
                "SalesOrderLine.ItemId",
            ],
            references);
    }

    /// <summary>AC-07.3 "under the same rules": renaming to another item's name (ignoring case) is <c>409</c>; the item keeps its name.</summary>
    [Fact]
    public async Task Renaming_to_another_items_name_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/{item.Id}",
            new { name = other.Name.ToUpperInvariant(), type = "RawMaterial", unitId = item.UnitId }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "item-name-taken",
            $"An item named '{other.Name.ToUpperInvariant()}' already exists.");
        Assert.Equal(item.Name, (await LoadItemAsync(item.Id))!.Name);
    }

    /// <summary>AC-07.3: an item may be renamed to its own name in another letter case.</summary>
    [Fact]
    public async Task Renaming_to_its_own_name_in_another_case_is_allowed()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/{item.Id}",
            new { name = item.Name.ToUpperInvariant(), type = "RawMaterial", unitId = item.UnitId }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(item.Name.ToUpperInvariant(), (await ApiJson.ReadAsync(response)).GetProperty("name").GetString());
    }

    /// <summary>AC-07.2 applies to changes too: an invalid body is <c>400</c> and the item is unchanged.</summary>
    [Fact]
    public async Task Invalid_change_is_rejected_and_nothing_changes()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/{item.Id}",
            new { name = Seed.UniqueName("New"), type = "Gadget", unitId = 999999 }, Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"{TypeMessage} Unit 999999 does not exist.");
        Assert.Equal([TypeMessage], ApiJson.Messages(body, "type"));
        var stored = await LoadItemAsync(item.Id);
        Assert.Equal(item.Name, stored!.Name);
        Assert.Equal(item.UnitId, stored.UnitId);
    }

    /// <summary>Design 2.2: changing an unknown item is <c>404</c>, even when the body is also invalid.</summary>
    [Fact]
    public async Task Changing_an_unknown_item_is_404_even_with_an_invalid_body()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PutAsJsonAsync($"{ItemsPath}/999999", new { name = "" }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    /// <summary>
    /// Architecture 4.1 item 2: a create or change with no body, or with the JSON literal <c>null</c>, is <c>400</c>
    /// "A request body is required.", not a server error, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("POST", "")]
    [InlineData("POST", "null")]
    [InlineData("PUT", "")]
    [InlineData("PUT", "null")]
    public async Task Missing_body_is_rejected(string method, string json)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var before = await CountItemsAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? ItemsPath : $"{ItemsPath}/{item.Id}")
        {
            Content = ApiJson.Content(json),
        };

        var response = await manager.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", "A request body is required.");
        Assert.Equal(before, await CountItemsAsync());
        Assert.Equal(item.Name, (await LoadItemAsync(item.Id))!.Name);
    }

    // ---- AC-07.4: delete ------------------------------------------------------------------------------------

    /// <summary>AC-07.4: an item with no references is deleted: <c>204</c> with no body, and later reads are <c>404</c>.</summary>
    [Fact]
    public async Task Manager_deletes_an_unused_item()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        var response = await manager.DeleteAsync($"{ItemsPath}/{item.Id}", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Null(await LoadItemAsync(item.Id));
        await ApiAssert.ProblemAsync(await manager.GetAsync($"{ItemsPath}/{item.Id}", Cancel),
            HttpStatusCode.NotFound, "not-found", $"Item {item.Id} not found.");
    }

    /// <summary>Design 7.2: deleting an item that does not exist is <c>404</c>.</summary>
    [Fact]
    public async Task Deleting_an_unknown_item_is_404()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.DeleteAsync($"{ItemsPath}/999999", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Item 999999 not found.");
    }

    // ---- AC-01.5 and AC-05.1: access -------------------------------------------------------------------------

    /// <summary>AC-01.5: every items endpoint answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Theory]
    [InlineData("GET", ItemsPath)]
    [InlineData("GET", ItemsPath + "/1")]
    [InlineData("POST", ItemsPath)]
    [InlineData("PUT", ItemsPath + "/1")]
    [InlineData("DELETE", ItemsPath + "/1")]
    public async Task Requests_without_a_token_are_401(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = ApiJson.Content("""{ "name": "Cheese", "type": "RawMaterial", "unitId": 1 }""");
        }

        var response = await anonymous.SendAsync(request, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// AC-05.1 (PRD 4.2, "Manage items"): creating, changing and deleting items are Manager-only. Users without the Manager
    /// role, even with both other roles, get <c>403</c> naming the Manager role, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("POST", Roles.Warehouse)]
    [InlineData("POST", Roles.Production)]
    [InlineData("POST", "Warehouse,Production")]
    [InlineData("PUT", Roles.Warehouse)]
    [InlineData("PUT", Roles.Production)]
    [InlineData("PUT", "Warehouse,Production")]
    [InlineData("DELETE", Roles.Warehouse)]
    [InlineData("DELETE", Roles.Production)]
    [InlineData("DELETE", "Warehouse,Production")]
    public async Task Changes_by_users_without_the_manager_role_are_403(string method, string roles)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var before = await CountItemsAsync();
        var caller = await factory.CreateClientAsAsync(roles.Split(','));
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? ItemsPath : $"{ItemsPath}/{item.Id}");
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { name = Seed.UniqueName("Sneaky"), type = "FinishedProduct", unitId = item.UnitId });
        }

        var response = await caller.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        Assert.Equal(before, await CountItemsAsync());
        var stored = await LoadItemAsync(item.Id);
        Assert.Equal(item.Name, stored!.Name);
        Assert.Equal(ItemType.RawMaterial, stored.Type);
    }

    /// <summary>Design 2.2: the role check (<c>403</c>) comes before the record check (<c>404</c>).</summary>
    [Fact]
    public async Task Role_check_comes_before_the_404()
    {
        var production = await factory.CreateClientAsAsync(Roles.Production);

        var response = await production.DeleteAsync($"{ItemsPath}/999999", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>Adds an item straight to the database, so a test can give it any creation time.</summary>
    private static async Task<int> InsertItemAsync(TestApiFactory app, int unitId, DateTime createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = NewItem(ItemType.RawMaterial, unitId);
        item.CreatedAt = createdAt;
        db.Items.Add(item);
        await db.SaveChangesAsync(Cancel);
        return item.Id;
    }

    /// <summary>
    /// Inserts one row that refers to the item, of the kind named by <paramref name="reference"/> (architecture 3.2,
    /// Item: "in use"). The other rows it needs (a product, an order, a user) are created alongside.
    /// </summary>
    private async Task AddReferenceAsync(string reference, int itemId, int unitId)
    {
        var user = await factory.CreateUserAsync($"{Guid.NewGuid():N}@inventory.test", Roles.Manager);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        switch (reference)
        {
            case "movement":
                db.Movements.Add(new Movement
                {
                    Type = MovementType.Received, ItemId = itemId, WarehouseChange = 10m, OccurredAt = now, UserId = user.Id,
                });
                break;

            case "recipe line":
            {
                // The item is a raw material on another product's recipe.
                var product = await SaveNewItemAsync(db, ItemType.FinishedProduct, unitId);
                db.Recipes.Add(new Recipe
                {
                    FinishedProductId = product.Id, UpdatedAt = now, UpdatedById = user.Id,
                    Lines = [new RecipeLine { LineNo = 1, RawMaterialId = itemId, AmountPerUnit = 1m }],
                });
                break;
            }

            case "own recipe":
            {
                // The item is a finished product with a recipe of its own.
                var material = await SaveNewItemAsync(db, ItemType.RawMaterial, unitId);
                db.Recipes.Add(new Recipe
                {
                    FinishedProductId = itemId, UpdatedAt = now, UpdatedById = user.Id,
                    Lines = [new RecipeLine { LineNo = 1, RawMaterialId = material.Id, AmountPerUnit = 0.015m }],
                });
                break;
            }

            case "order line":
            {
                var product = await SaveNewItemAsync(db, ItemType.FinishedProduct, unitId);
                var order = NewOrder(product.Id, user.Id, now);
                order.Lines.Add(new ProductionOrderLine { LineNo = 1, RawMaterialId = itemId, AmountPerUnit = 1m, PlannedAmount = 1000m });
                db.ProductionOrders.Add(order);
                break;
            }

            case "production order":
                db.ProductionOrders.Add(NewOrder(itemId, user.Id, now));
                break;

            case "complementary order line":
            {
                var product = await SaveNewItemAsync(db, ItemType.FinishedProduct, unitId);
                db.ComplementaryOrders.Add(new ComplementaryOrder
                {
                    ParentOrder = NewOrder(product.Id, user.Id, now),
                    Status = ComplementaryOrderStatus.PendingApproval,
                    Reason = "Hopper spill at start of run",
                    RequestedAt = now,
                    RequestedById = user.Id,
                    Lines = [new ComplementaryOrderLine { LineNo = 1, RawMaterialId = itemId, RequestedAmount = 20m }],
                });
                break;
            }

            case "sales order line":
            {
                // (v1.1) AC-28.5: a line on a Cancelled sales order still counts, so no status makes the item free again.
                // Inserted through the DbContext because the sales order endpoints come in later tasks (T-024, T-025).
                var customerName = Seed.UniqueName("Customer");
                db.SalesOrders.Add(new SalesOrder
                {
                    Customer = new Customer
                    {
                        Name = customerName, NameNormalized = NameNormalizer.Normalize(customerName), CreatedAt = now,
                        CreatedById = user.Id,
                    },
                    Status = SalesOrderStatus.Cancelled,
                    CreatedAt = now,
                    CreatedById = user.Id,
                    CancelledAt = now,
                    CancelledById = user.Id,
                    CancellationReason = "Customer changed the order",
                    StatusChangedAt = now,
                    Lines = [new SalesOrderLine { LineNo = 1, ItemId = itemId, Quantity = 600m }],
                });
                break;
            }

            case "return":
                // In a write transaction of its own, as the return endpoints would write it (T-020 tidy). Through the
                // shared helper rather than POST /returns, which would first need free leftover of the item (AC-22.2).
                await OrderFlow.InsertPendingReturnAsync(factory, itemId, 20m, user.Id);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(reference), reference, "Unknown reference kind.");
        }

        await db.SaveChangesAsync(Cancel);
    }

    /// <summary>
    /// A Created production order. (Created, not InProduction: the database allows only one InProduction order, and
    /// these tests share a database.)
    /// </summary>
    private static ProductionOrder NewOrder(int finishedProductId, string userId, DateTime now) => new()
    {
        FinishedProductId = finishedProductId,
        OrderedQuantity = 1000m,
        Status = OrderStatus.Created,
        CreatedAt = now,
        CreatedById = userId,
        StatusChangedAt = now,
    };

    private static Item NewItem(ItemType type, int unitId)
    {
        var name = Seed.UniqueName(type.ToString());
        return new Item
        {
            Name = name, NameNormalized = NameNormalizer.Normalize(name), Type = type, UnitId = unitId, CreatedAt = DateTime.UtcNow,
        };
    }

    private static async Task<Item> SaveNewItemAsync(AppDbContext db, ItemType type, int unitId)
    {
        var item = NewItem(type, unitId);
        db.Items.Add(item);
        await db.SaveChangesAsync(Cancel);
        return item;
    }

    private async Task<Item?> LoadItemAsync(int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Items.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, Cancel);
    }

    private async Task<int> CountItemsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Items.CountAsync(Cancel);
    }

    private async Task<int> CountItemsNamedAsync(string name)
    {
        var normalized = NameNormalizer.Normalize(name);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Items
            .CountAsync(item => item.NameNormalized == normalized, Cancel);
    }
}
