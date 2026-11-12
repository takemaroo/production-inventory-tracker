using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-006: the <c>/api/v1/units</c> endpoints (US-06, AC-05.1 for these endpoints, design 5.3 and 7.2 "Units").
/// </summary>
/// <remarks>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>), so every test uses unique names.
/// Tests that need exact counts or a known order create their own <see cref="TestApiFactory"/>.
/// </remarks>
public class UnitsTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string UnitsPath = "/api/v1/units";
    private const string NotAuthenticatedMessage = "Your sign-in is missing or has expired. Sign in again.";
    private const string ManagerOnlyMessage = "Your roles do not allow this action. It needs the Manager role.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-06.1: create and list ---------------------------------------------------------------------------

    /// <summary>
    /// AC-06.1: a Manager creates a unit; leading and trailing spaces are removed. Design 2.2 and 5.3: <c>201</c>, the
    /// body is exactly <c>{ id, name }</c>, and <c>Location</c> points at the new unit, which can be read there.
    /// </summary>
    [Fact]
    public async Task Manager_creates_a_unit_with_a_trimmed_name()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = UniqueName("kg");

        var response = await manager.PostAsJsonAsync(UnitsPath, new { name = $"  {name}  " }, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal($"{UnitsPath}/{id}", response.Headers.Location?.AbsolutePath);

        var read = await manager.GetAsync(response.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.GetRawText(), (await ReadJsonAsync(read)).GetRawText());
    }

    /// <summary>
    /// AC-06.1: a name of 1 to 50 characters is accepted, counted after trimming; 51 is rejected with the design 7.2
    /// message, and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Name_of_50_characters_is_accepted_and_51_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var fifty = UniqueName("u").PadRight(50, 'x');
        var fiftyOne = UniqueName("u").PadRight(51, 'x');

        var accepted = await manager.PostAsJsonAsync(UnitsPath, new { name = $"   {fifty}   " }, Cancel);
        var oneLetter = await manager.PostAsJsonAsync(UnitsPath, new { name = UniqueLetter() }, Cancel);
        var rejected = await manager.PostAsJsonAsync(UnitsPath, new { name = fiftyOne }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(fifty, (await ReadJsonAsync(accepted)).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Created, oneLetter.StatusCode);
        await AssertValidationAsync(rejected, "name", "Name must be 50 characters or fewer.");
        Assert.Equal(0, await CountUnitsNamedAsync(fiftyOne));
    }

    /// <summary>AC-06.2: an empty name (missing, null, empty or only spaces) is rejected with <c>400</c> "Name is required.".</summary>
    [Theory]
    [InlineData("""{ "name": "" }""")]
    [InlineData("""{ "name": "    " }""")]
    [InlineData("""{ "name": null }""")]
    [InlineData("""{ }""")]
    public async Task Empty_name_is_rejected(string json)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var before = await CountUnitsAsync();

        var response = await manager.PostAsync(UnitsPath, Json(json), Cancel);

        await AssertValidationAsync(response, "name", "Name is required.");
        Assert.Equal(before, await CountUnitsAsync());
    }

    /// <summary>
    /// AC-06.2: a name that already exists, compared without regard to case and after trimming, is rejected with
    /// <c>409</c> <c>unit-name-taken</c>, and no second unit is saved.
    /// </summary>
    [Fact]
    public async Task Duplicate_name_ignoring_case_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = UniqueName("Bag");
        await CreateUnitAsync(manager, name);

        var response = await manager.PostAsJsonAsync(UnitsPath, new { name = $" {name.ToUpperInvariant()} " }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "unit-name-taken",
            $"A unit named '{name.ToUpperInvariant()}' already exists.");
        Assert.Equal(1, await CountUnitsNamedAsync(name));
    }

    /// <summary>
    /// PRD section 5 and AC-06.2: two Managers create the same name at the same moment; exactly one unit is saved and
    /// the other request gets the specific <c>409</c> <c>unit-name-taken</c>, not the <c>concurrent-change</c>
    /// fallback (design 7.2: "the losing one gets the specific message where one applies"). Write transactions run one
    /// at a time (architecture 5.2), so the second request's name check already sees the first unit.
    /// </summary>
    [Fact]
    public async Task Simultaneous_creates_of_one_name_save_only_one_unit()
    {
        var first = await factory.CreateClientAsAsync(Roles.Manager);
        var second = await factory.CreateClientAsAsync(Roles.Manager);
        var name = UniqueName("race");

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(UnitsPath, new { name }, Cancel),
            second.PostAsJsonAsync(UnitsPath, new { name = name.ToUpperInvariant() }, Cancel));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("unit-name-taken", (await ReadJsonAsync(loser)).GetProperty("code").GetString());
        Assert.Equal(1, await CountUnitsNamedAsync(name));
    }

    /// <summary>
    /// Architecture 4.1 item 11 and decision #46: if a duplicate name ever got past the service's check, the unique
    /// index on <c>NameNormalized</c> refuses the insert and the client gets <c>409</c> <c>concurrent-change</c>, logged
    /// at Warning, and nothing is saved. The check cannot normally be beaten (writes are serialised), so the test
    /// makes it happen: an interceptor inserts the same normalized name inside the request's open transaction, after
    /// the service's check and just before EF Core saves the new unit.
    /// </summary>
    [Fact]
    public async Task Duplicate_that_gets_past_the_check_is_409_concurrent_change_and_saves_nothing()
    {
        var name = UniqueName("sneaked");
        var otherSpelling = name.ToUpperInvariant();
        var normalized = NameNormalizer.Normalize(name);
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<UnitOfMeasure>().Any(entry => entry.State == EntityState.Added),
            work: (context, cancellationToken) => context.Database.ExecuteSqlAsync(
                $"INSERT INTO Units (Name, NameNormalized) VALUES ({otherSpelling}, {normalized})", cancellationToken));
        await using var app = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsJsonAsync(UnitsPath, new { name }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "concurrent-change",
            "The data changed while your request was being processed. Read it again and retry.");
        Assert.True(interceptor.HasRun);
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is DbUpdateException);

        // The rollback removes both rows: the one the interceptor inserted and the one EF Core tried to add.
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Units.CountAsync(Cancel));
    }

    /// <summary>
    /// AC-06.4: <c>UnitService.DeleteAsync</c> checks exactly one reference to a unit, <c>Item.UnitId</c>. This test
    /// reads the foreign keys that point at <see cref="UnitOfMeasure"/> from the EF Core model, so a foreign key added
    /// later makes it fail until the service checks the new reference too. Otherwise deleting a unit used only through
    /// the new reference would hit the Restrict foreign key and answer <c>500</c> instead of <c>409</c> (decision #46).
    /// </summary>
    [Fact]
    public async Task Unit_in_use_check_covers_every_foreign_key_to_units()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;

        var references = model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(UnitOfMeasure))
            .Select(foreignKey =>
                $"{foreignKey.DeclaringEntityType.ClrType.Name}.{string.Join(",", foreignKey.Properties.Select(property => property.Name))}")
            .ToList();

        Assert.Equal(["Item.UnitId"], references);
    }

    /// <summary>
    /// AC-06.1 and AC-05.1: every signed-in user can list units and read one, whatever their role (PRD 4.2, "View units").
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_list_and_read_units(string role)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("view"));
        var reader = await factory.CreateClientAsAsync(role);

        var list = await reader.GetAsync($"{UnitsPath}?pageSize=100", Cancel);
        var one = await reader.GetAsync($"{UnitsPath}/{unit.Id}", Cancel);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains((await ReadJsonAsync(list)).GetProperty("items").EnumerateArray(),
            row => row.GetProperty("id").GetInt32() == unit.Id && row.GetProperty("name").GetString() == unit.Name);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(unit.Name, (await ReadJsonAsync(one)).GetProperty("name").GetString());
    }

    /// <summary>
    /// PRD 4.1 and design 2.5: the list is paged, newest first, with the total count. Defaults are page 1 and 50; a page
    /// past the end is empty.
    /// </summary>
    [Fact]
    public async Task Units_are_listed_newest_first_in_pages()
    {
        await using var fresh = new TestApiFactory();
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);
        var oldest = await CreateUnitAsync(manager, "kg");
        var middle = await CreateUnitAsync(manager, "Bag");
        var newest = await CreateUnitAsync(manager, "Litre");

        var firstPage = await ReadJsonAsync(await manager.GetAsync($"{UnitsPath}?page=1&pageSize=2", Cancel));
        var secondPage = await ReadJsonAsync(await manager.GetAsync($"{UnitsPath}?page=2&pageSize=2", Cancel));
        var pastTheEnd = await ReadJsonAsync(await manager.GetAsync($"{UnitsPath}?page=3&pageSize=2", Cancel));
        var defaults = await ReadJsonAsync(await manager.GetAsync(UnitsPath, Cancel));

        Assert.Equal([newest.Id, middle.Id], Ids(firstPage));
        Assert.Equal(1, firstPage.GetProperty("page").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, firstPage.EnumerateObject().Count());
        Assert.Equal([oldest.Id], Ids(secondPage));
        Assert.Empty(Ids(pastTheEnd));
        Assert.Equal(3, pastTheEnd.GetProperty("totalCount").GetInt32());
        Assert.Equal([newest.Id, middle.Id, oldest.Id], Ids(defaults));
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(50, defaults.GetProperty("pageSize").GetInt32());
    }

    /// <summary>Design 2.5 and 7.2: paging values outside the limits are <c>400</c>, both reported at once.</summary>
    [Fact]
    public async Task Invalid_paging_is_rejected()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await reader.GetAsync($"{UnitsPath}?page=0&pageSize=101", Cancel);
        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "page must be 1 or greater. pageSize must be between 1 and 100.");

        Assert.Equal(["page must be 1 or greater."], Messages(body, "page"));
        Assert.Equal(["pageSize must be between 1 and 100."], Messages(body, "pageSize"));
    }

    /// <summary>Design 7.2: reading a unit that does not exist is <c>404</c> "Unit {id} not found.".</summary>
    [Fact]
    public async Task Reading_an_unknown_unit_is_404()
    {
        var reader = await factory.CreateClientAsAsync(Roles.Production);

        var response = await reader.GetAsync($"{UnitsPath}/999999", Cancel);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Unit 999999 not found.");
    }

    // ---- AC-06.3: rename ------------------------------------------------------------------------------------

    /// <summary>AC-06.3: a Manager renames a unit (trimmed); the response and later reads show the new name.</summary>
    [Fact]
    public async Task Manager_renames_a_unit()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("kilo"));
        var newName = UniqueName("kg");

        var response = await manager.PutAsJsonAsync($"{UnitsPath}/{unit.Id}", new { name = $" {newName} " }, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(unit.Id, body.GetProperty("id").GetInt32());
        Assert.Equal(newName, body.GetProperty("name").GetString());
        Assert.Equal(newName, (await GetUnitAsync(manager, unit.Id)).GetProperty("name").GetString());
        Assert.Equal(NameNormalizer.Normalize(newName), (await LoadUnitAsync(unit.Id))!.NameNormalized);
    }

    /// <summary>AC-06.3: renaming a unit to its own name in another letter case is not a duplicate.</summary>
    [Fact]
    public async Task Renaming_a_unit_to_its_own_name_in_another_case_is_allowed()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("bag"));

        var response = await manager.PutAsJsonAsync($"{UnitsPath}/{unit.Id}", new { name = unit.Name.ToUpperInvariant() }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(unit.Name.ToUpperInvariant(), (await ReadJsonAsync(response)).GetProperty("name").GetString());
    }

    /// <summary>
    /// AC-06.3 "under the same rules": renaming to another unit's name (ignoring case) is <c>409</c>; an empty or too long
    /// name is <c>400</c>. The unit keeps its name each time.
    /// </summary>
    [Fact]
    public async Task Rename_follows_the_same_rules_as_create()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("box"));
        var other = await CreateUnitAsync(manager, UniqueName("crate"));
        var path = $"{UnitsPath}/{unit.Id}";

        var duplicate = await manager.PutAsJsonAsync(path, new { name = other.Name.ToUpperInvariant() }, Cancel);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "unit-name-taken",
            $"A unit named '{other.Name.ToUpperInvariant()}' already exists.");

        var blank = await manager.PutAsJsonAsync(path, new { name = "   " }, Cancel);
        await AssertValidationAsync(blank, "name", "Name is required.");

        var tooLong = await manager.PutAsJsonAsync(path, new { name = new string('x', 51) }, Cancel);
        await AssertValidationAsync(tooLong, "name", "Name must be 50 characters or fewer.");

        Assert.Equal(unit.Name, (await GetUnitAsync(manager, unit.Id)).GetProperty("name").GetString());
    }

    /// <summary>Design 2.2: the unknown id in the URL (<c>404</c>) is reported before an invalid body (<c>400</c>).</summary>
    [Fact]
    public async Task Renaming_an_unknown_unit_is_404_even_with_an_invalid_name()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PutAsJsonAsync($"{UnitsPath}/999999", new { name = "" }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Unit 999999 not found.");
    }

    // ---- AC-06.4: delete ------------------------------------------------------------------------------------

    /// <summary>AC-06.4: a Manager deletes a unit that no item uses: <c>204</c> with no body, and the unit is gone.</summary>
    [Fact]
    public async Task Manager_deletes_an_unused_unit()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("tray"));

        var response = await manager.DeleteAsync($"{UnitsPath}/{unit.Id}", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Null(await LoadUnitAsync(unit.Id));
        var read = await manager.GetAsync($"{UnitsPath}/{unit.Id}", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    /// <summary>
    /// AC-06.4: deleting a unit used by an item is rejected with <c>409</c> "Unit is used by one or more items." (exact
    /// PRD text) and the unit stays. Once the item is deleted, the unit can be deleted.
    /// </summary>
    [Fact]
    public async Task Deleting_a_unit_used_by_an_item_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("sack"));
        var item = await manager.PostAsJsonAsync("/api/v1/items",
            new { name = UniqueName("Flour"), type = "RawMaterial", unitId = unit.Id }, Cancel);
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);

        var refused = await manager.DeleteAsync($"{UnitsPath}/{unit.Id}", Cancel);

        await AssertProblemAsync(refused, HttpStatusCode.Conflict, "unit-in-use", "Unit is used by one or more items.");
        Assert.NotNull(await LoadUnitAsync(unit.Id));

        var deleteItem = await manager.DeleteAsync(item.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.NoContent, deleteItem.StatusCode);
        var deleteUnit = await manager.DeleteAsync($"{UnitsPath}/{unit.Id}", Cancel);
        Assert.Equal(HttpStatusCode.NoContent, deleteUnit.StatusCode);
    }

    /// <summary>Design 7.2: deleting a unit that does not exist is <c>404</c>.</summary>
    [Fact]
    public async Task Deleting_an_unknown_unit_is_404()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.DeleteAsync($"{UnitsPath}/999999", Cancel);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Unit 999999 not found.");
    }

    // ---- AC-01.5 and AC-05.1: access -------------------------------------------------------------------------

    /// <summary>AC-01.5: every units endpoint answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Theory]
    [InlineData("GET", UnitsPath)]
    [InlineData("GET", UnitsPath + "/1")]
    [InlineData("POST", UnitsPath)]
    [InlineData("PUT", UnitsPath + "/1")]
    [InlineData("DELETE", UnitsPath + "/1")]
    public async Task Requests_without_a_token_are_401(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = Json("""{ "name": "kg" }""");
        }

        var response = await anonymous.SendAsync(request, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated", NotAuthenticatedMessage);
        Assert.Equal(new AuthenticationHeaderValue("Bearer").ToString(), response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>
    /// AC-05.1 (PRD 4.2, "Manage units"): creating, renaming and deleting are Manager-only. Users without the Manager role,
    /// even with both other roles, get <c>403</c> naming the Manager role, and nothing changes.
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
        var unit = await CreateUnitAsync(manager, UniqueName("pallet"));
        var before = await CountUnitsAsync();
        var caller = await factory.CreateClientAsAsync(roles.Split(','));
        var newName = UniqueName("renamed");
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? UnitsPath : $"{UnitsPath}/{unit.Id}");
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { name = newName });
        }

        var response = await caller.SendAsync(request, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ManagerOnlyMessage);
        Assert.Equal(before, await CountUnitsAsync());
        Assert.Equal(unit.Name, (await LoadUnitAsync(unit.Id))!.Name);
    }

    /// <summary>Design 2.2: the role check (<c>403</c>) comes before the record check (<c>404</c>).</summary>
    [Fact]
    public async Task Role_check_comes_before_the_404()
    {
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await warehouse.PutAsJsonAsync($"{UnitsPath}/999999", new { name = "kg" }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ManagerOnlyMessage);
    }

    /// <summary>AC-05.2: a user holding Manager together with another role can manage units.</summary>
    [Fact]
    public async Task Manager_with_another_role_can_change_units()
    {
        var caller = await factory.CreateClientAsAsync(Roles.Warehouse, Roles.Manager);

        var response = await caller.PostAsJsonAsync(UnitsPath, new { name = UniqueName("multi") }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Architecture 4.1 item 2: a name sent as a number is <c>400</c> naming the field.</summary>
    [Fact]
    public async Task Name_of_the_wrong_json_type_is_rejected()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsync(UnitsPath, Json("""{ "name": 5 }"""), Cancel);

        await AssertValidationAsync(response, "name", "name has the wrong type.");
    }

    /// <summary>
    /// Architecture 4.1 item 2: a create or rename with no body, or with the JSON literal <c>null</c> (valid JSON that
    /// holds no object), is <c>400</c> "A request body is required.", not a server error, and nothing changes. For a
    /// rename this comes before any check of the unit itself, because the framework reads the body first.
    /// </summary>
    [Theory]
    [InlineData("POST", "")]
    [InlineData("POST", "null")]
    [InlineData("PUT", "")]
    [InlineData("PUT", "null")]
    public async Task Missing_body_is_rejected(string method, string json)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await CreateUnitAsync(manager, UniqueName("keg"));
        var before = await CountUnitsAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? UnitsPath : $"{UnitsPath}/{unit.Id}")
        {
            Content = Json(json),
        };

        var response = await manager.SendAsync(request, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", "A request body is required.");
        Assert.Equal(before, await CountUnitsAsync());
        Assert.Equal(unit.Name, (await LoadUnitAsync(unit.Id))!.Name);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// An EF Core <em>interceptor</em>: an object EF Core calls at fixed points of its work, here just before
    /// <c>SaveChanges</c> sends its statements. It runs <c>work</c> once, on the request's own DbContext and so inside
    /// its open write transaction, the first time <c>shouldRun</c> is true. Tests use it to change the database between
    /// a service's checks and its save, which is the only way to reach the unique-index fallback (decision #46).
    /// </summary>
    /// <remarks>
    /// Register it through <c>TestApiFactory.ReplaceServices</c> with
    /// <c>services.ConfigureDbContext&lt;AppDbContext&gt;(options =&gt; options.AddInterceptors(interceptor))</c>.
    /// <c>shouldRun</c> is needed because the same DbContext type also saves users and sign-ins while a test sets up.
    /// Kept here until the shared test helpers move to <c>Infrastructure/</c>.
    /// </remarks>
    public sealed class BeforeSaveInterceptor(
        Func<DbContext, bool> shouldRun, Func<DbContext, CancellationToken, Task> work) : SaveChangesInterceptor
    {
        private int _hasRun;

        /// <summary>True once <c>work</c> has run.</summary>
        public bool HasRun => Volatile.Read(ref _hasRun) == 1;

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && shouldRun(context) && Interlocked.Exchange(ref _hasRun, 1) == 0)
            {
                // The interceptor exists to act inside the request's transaction; fail loudly if there is none.
                if (context.Database.CurrentTransaction is null)
                {
                    throw new InvalidOperationException("BeforeSaveInterceptor expected an open write transaction.");
                }

                await work(context, cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>A unit created through the API.</summary>
    private sealed record CreatedUnit(int Id, string Name);

    private static async Task<CreatedUnit> CreateUnitAsync(HttpClient manager, string name)
    {
        var response = await manager.PostAsJsonAsync(UnitsPath, new { name }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return new CreatedUnit(body.GetProperty("id").GetInt32(), body.GetProperty("name").GetString()!);
    }

    private static async Task<JsonElement> GetUnitAsync(HttpClient client, int id)
    {
        var response = await client.GetAsync($"{UnitsPath}/{id}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<UnitOfMeasure?> LoadUnitAsync(int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Units.AsNoTracking().SingleOrDefaultAsync(unit => unit.Id == id, Cancel);
    }

    private async Task<int> CountUnitsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Units.CountAsync(Cancel);
    }

    private async Task<int> CountUnitsNamedAsync(string name)
    {
        var normalized = NameNormalizer.Normalize(name);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Units
            .CountAsync(unit => unit.NameNormalized == normalized, Cancel);
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>A one-character name that no other test uses: a random letter outside the ASCII range.</summary>
    private static string UniqueLetter() => ((char)Random.Shared.Next(0x4E00, 0x9FFF)).ToString();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static IEnumerable<int> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();

    private static IEnumerable<string?> Messages(JsonElement body, string path) =>
        body.GetProperty("errors").GetProperty(path).EnumerateArray().Select(message => message.GetString()).ToList();

    private static async Task AssertValidationAsync(HttpResponseMessage response, string path, string message)
    {
        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Equal([message], Messages(body, path));
    }

    /// <summary>Checks the design 6 error body: status, content type, fixed title, code and detail.</summary>
    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        var title = status switch
        {
            HttpStatusCode.BadRequest => "Invalid request",
            HttpStatusCode.Unauthorized => "Not signed in",
            HttpStatusCode.Forbidden => "Not allowed",
            HttpStatusCode.NotFound => "Not found",
            _ => "Conflict with current state",
        };
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        return body;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
