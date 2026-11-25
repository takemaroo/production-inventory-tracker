using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Customers;
using InventoryTracker.Api.Tests.Infrastructure;
using InventoryTracker.Api.Tests.Integration.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-024: the seven <c>/api/v1/customers</c> endpoints (US-29, AC-29.1 to AC-29.7; AC-05.3 for who created a
/// customer and when; design 3.12, 5.11 and 7.2 "Customers"; architecture 5.1 check order; decision #163 logging). T-033
/// adds the "who changed a customer" log lines (SEC-26, decisions #177 and #179).
/// </summary>
/// <remarks>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>), so they use unique names. A test that
/// checks a PRD sentence word for word ("Deli Norte"), an exact list order or a count creates its own
/// <see cref="TestApiFactory"/>, because a fixed name can exist only once in a database.
/// </remarks>
public class CustomersTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>The design 5.11 example's creation time, 08:30 UTC on 1 December 2026.</summary>
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 8, 30, 0, TimeSpan.Zero);

    /// <summary>A character outside the Basic Multilingual Plane: two UTF-16 code units, one code point (decision #116).</summary>
    private const string Emoji = "\U0001F9C0";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-29.1: create ------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.1, AC-05.3, design 5.11: a Seller creates a customer. <c>201</c>, <c>Location: /api/v1/customers/{id}</c>, and
    /// exactly the design body: the trimmed name and notes, active, the creation time from the app's clock in whole
    /// seconds, and the creator as <c>{ id, email }</c>. The same body is read back at the <c>Location</c>.
    /// </summary>
    [Fact]
    public async Task Seller_creates_a_customer_with_the_design_body()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start.AddMilliseconds(700)) };
        var (seller, user) = await OrderFlow.SignedInAsync(timed, Roles.Seller);

        var response = await seller.PostAsJsonAsync(CustomersPath,
            new { name = "  Deli Norte  ", notes = "  Deliveries before 10:00, ask for the kitchen.  " }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"{CustomersPath}/{id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(["id", "name", "notes", "isActive", "createdAt", "createdBy"], body.EnumerateObject().Select(field => field.Name));
        Assert.Equal("Deli Norte", body.GetProperty("name").GetString());
        Assert.Equal("Deliveries before 10:00, ask for the kitchen.", body.GetProperty("notes").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal("2026-12-01T08:30:00Z", body.GetProperty("createdAt").GetString());
        var createdBy = body.GetProperty("createdBy");
        Assert.Equal(["id", "email"], createdBy.EnumerateObject().Select(field => field.Name));
        Assert.Equal(user.Id, createdBy.GetProperty("id").GetString());
        Assert.Equal(user.Email, createdBy.GetProperty("email").GetString());

        var read = await seller.GetAsync(response.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.GetRawText(), (await ApiJson.ReadAsync(read)).GetRawText());

        // Architecture 3.1: the stored normalized name is what the unique index compares.
        var stored = await LoadCustomerAsync(timed, id);
        Assert.Equal(NameNormalizer.Normalize("Deli Norte"), stored!.NameNormalized);
    }

    /// <summary>
    /// AC-29.1, design 2.6: notes are optional. Left out, sent as <c>null</c>, empty or only spaces, they are stored and
    /// returned as <c>null</c>. A Manager may create a customer too.
    /// </summary>
    [Theory]
    [InlineData("""{ "name": "{name}" }""")]
    [InlineData("""{ "name": "{name}", "notes": null }""")]
    [InlineData("""{ "name": "{name}", "notes": "" }""")]
    [InlineData("""{ "name": "{name}", "notes": "    " }""")]
    public async Task Notes_are_optional_and_blank_becomes_null(string template)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("Customer");

        var response = await manager.PostAsync(CustomersPath, ApiJson.Content(template.Replace("{name}", name)), Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notes").ValueKind);
        Assert.Null((await LoadCustomerAsync(factory, body.GetProperty("id").GetInt32()))!.Notes);
    }

    // ---- AC-29.2: name and notes rules --------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.2, PRD example: a name another customer already has, ignoring case, is <c>409</c> <c>customer-name-taken</c>
    /// "A customer named 'Deli Norte' already exists.", quoting the name as sent, trimmed. No second customer is saved.
    /// </summary>
    [Fact]
    public async Task Name_another_customer_has_ignoring_case_is_409()
    {
        await using var fresh = new TestApiFactory();
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        await CreateCustomerAsync(seller, "DELI NORTE");

        var response = await seller.PostAsJsonAsync(CustomersPath, new { name = "  Deli Norte  " }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "customer-name-taken", ExpectedMessages.DeliNorteNameTaken);
        Assert.Equal(1, await CountCustomersAsync(fresh));
    }

    /// <summary>AC-29.2, design 7.2: a missing, null, empty or blank name is <c>400</c> "Name is required." under <c>name</c>.</summary>
    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "name": null }""")]
    [InlineData("""{ "name": "" }""")]
    [InlineData("""{ "name": "    ", "notes": "Fine notes" }""")]
    public async Task Missing_or_blank_name_is_rejected(string json)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var before = await CountCustomersAsync(factory);

        var response = await seller.PostAsync(CustomersPath, ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name is required.");
        Assert.Equal(before, await CountCustomersAsync(factory));
    }

    /// <summary>
    /// AC-29.1, AC-29.2, design 2.6: a name of 100 characters and notes of 500 are accepted, counted after trimming. A name
    /// of 101 is "Name must be 100 characters or fewer." under <c>name</c>, notes of 501 are "Notes must be 500 characters
    /// or fewer." under <c>notes</c>, and both at once are reported together in one <c>400</c>. Nothing is saved.
    /// </summary>
    [Fact]
    public async Task Name_up_to_100_and_notes_up_to_500_are_accepted()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var hundred = Seed.UniqueName("c").PadRight(100, 'x');
        var fiveHundred = new string('n', 500);
        var hundredAndOne = Seed.UniqueName("c").PadRight(101, 'x');
        var before = await CountCustomersAsync(factory);

        var accepted = await seller.PostAsJsonAsync(CustomersPath, new { name = $"  {hundred}  ", notes = $"  {fiveHundred}  " }, Cancel);
        var longName = await seller.PostAsJsonAsync(CustomersPath, new { name = hundredAndOne }, Cancel);
        var longNotes = await seller.PostAsJsonAsync(CustomersPath,
            new { name = Seed.UniqueName("c"), notes = fiveHundred + "n" }, Cancel);
        var both = await seller.PostAsJsonAsync(CustomersPath, new { name = hundredAndOne, notes = fiveHundred + "n" }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var acceptedBody = await ApiJson.ReadAsync(accepted);
        Assert.Equal(hundred, acceptedBody.GetProperty("name").GetString());
        Assert.Equal(fiveHundred, acceptedBody.GetProperty("notes").GetString());
        await ApiAssert.ValidationAsync(longName, "name", "Name must be 100 characters or fewer.");
        await ApiAssert.ValidationAsync(longNotes, "notes", "Notes must be 500 characters or fewer.");
        var bothBody = await ApiAssert.ProblemAsync(both, HttpStatusCode.BadRequest, "validation",
            "Name must be 100 characters or fewer. Notes must be 500 characters or fewer.");
        Assert.Equal(["Name must be 100 characters or fewer."], ApiJson.Messages(bothBody, "name"));
        Assert.Equal(["Notes must be 500 characters or fewer."], ApiJson.Messages(bothBody, "notes"));
        Assert.Equal(before + 1, await CountCustomersAsync(factory));
    }

    /// <summary>
    /// AC-29.2, decision #116: lengths count Unicode code points, so an emoji (two UTF-16 code units) is one character. A
    /// name of 100 code points (167 code units) and notes of 500 emoji are accepted; one more of either is refused.
    /// </summary>
    [Fact]
    public async Task Lengths_count_code_points_so_an_emoji_is_one_character()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var prefix = Seed.UniqueName("c"); // 34 ASCII characters
        var hundred = prefix + string.Concat(Enumerable.Repeat(Emoji, 100 - prefix.Length));
        var notes500 = string.Concat(Enumerable.Repeat(Emoji, 500));
        Assert.True(hundred.Length > 100); // the old UTF-16 count would have refused it

        var accepted = await seller.PostAsJsonAsync(CustomersPath, new { name = hundred, notes = notes500 }, Cancel);
        var longName = await seller.PostAsJsonAsync(CustomersPath,
            new { name = Seed.UniqueName("c") + string.Concat(Enumerable.Repeat(Emoji, 67)) }, Cancel);
        var longNotes = await seller.PostAsJsonAsync(CustomersPath,
            new { name = Seed.UniqueName("c"), notes = notes500 + Emoji }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(hundred, (await ApiJson.ReadAsync(accepted)).GetProperty("name").GetString());
        await ApiAssert.ValidationAsync(longName, "name", "Name must be 100 characters or fewer.");
        await ApiAssert.ValidationAsync(longNotes, "notes", "Notes must be 500 characters or fewer.");
    }

    /// <summary>Architecture 4.1 item 2: a name or notes of the wrong JSON type is <c>400</c> naming the field.</summary>
    [Theory]
    [InlineData("""{ "name": 5 }""", "name")]
    [InlineData("""{ "name": "Fine", "notes": 5 }""", "notes")]
    public async Task Field_of_the_wrong_json_type_is_rejected(string json, string field)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await seller.PostAsync(CustomersPath, ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, field, $"{field} has the wrong type.");
    }

    /// <summary>
    /// Architecture 4.1 item 2: a create or edit with no body, or the JSON literal <c>null</c>, is <c>400</c> "A request
    /// body is required.", and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("POST", "")]
    [InlineData("POST", "null")]
    [InlineData("PUT", "")]
    [InlineData("PUT", "null")]
    public async Task Missing_body_is_rejected(string method, string json)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);
        var before = await CountCustomersAsync(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? CustomersPath : $"{CustomersPath}/{customer.Id}")
        {
            Content = ApiJson.Content(json),
        };

        var response = await seller.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", "A request body is required.");
        Assert.Equal(before, await CountCustomersAsync(factory));
        Assert.Equal(customer.Name, (await LoadCustomerAsync(factory, customer.Id))!.Name);
    }

    /// <summary>
    /// PRD section 5, AC-29.2: two users create "Deli Norte" and "DELI NORTE" at the same moment. Exactly one customer is
    /// saved; the other request gets the specific <c>409</c> <c>customer-name-taken</c>, not the <c>concurrent-change</c>
    /// fallback, because write transactions run one at a time and the second name check already sees the first customer.
    /// </summary>
    [Fact]
    public async Task Simultaneous_creates_of_one_name_save_only_one_customer()
    {
        await using var fresh = new TestApiFactory();
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var manager = await fresh.CreateClientAsAsync(Roles.Manager);

        var responses = await Task.WhenAll(
            seller.PostAsJsonAsync(CustomersPath, new { name = "Deli Norte" }, Cancel),
            manager.PostAsJsonAsync(CustomersPath, new { name = "DELI NORTE" }, Cancel));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var lostName = loser == responses[0] ? "Deli Norte" : "DELI NORTE";
        await ApiAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "customer-name-taken", $"A customer named '{lostName}' already exists.");
        Assert.Equal(1, await CountCustomersAsync(fresh));
    }

    /// <summary>
    /// Architecture 4.1 item 11, decision #46: if a duplicate name ever got past the service's check, the unique index on
    /// <c>NameNormalized</c> refuses the insert, the client gets <c>409</c> <c>concurrent-change</c>, and nothing is saved.
    /// Writes are serialised, so the test forces it: an interceptor inserts the same normalized name inside the request's
    /// open transaction, after the check and just before EF Core saves the new customer.
    /// </summary>
    [Fact]
    public async Task Duplicate_that_gets_past_the_check_is_409_concurrent_change_and_saves_nothing()
    {
        var name = Seed.UniqueName("sneaked");
        var otherSpelling = name.ToUpperInvariant();
        var normalized = NameNormalizer.Normalize(name);
        string? userId = null;
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<Customer>().Any(entry => entry.State == EntityState.Added),
            work: (context, cancellationToken) => context.Database.ExecuteSqlAsync(
                $"INSERT INTO Customers (Name, NameNormalized, IsActive, CreatedAt, CreatedById) VALUES ({otherSpelling}, {normalized}, 1, {DateTime.UtcNow}, {userId})",
                cancellationToken));
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var (seller, user) = await OrderFlow.SignedInAsync(app, Roles.Seller);
        userId = user.Id;

        var response = await seller.PostAsJsonAsync(CustomersPath, new { name }, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "concurrent-change",
            "The data changed while your request was being processed. Read it again and retry.");
        Assert.True(interceptor.HasRun);
        Assert.Equal(0, await CountCustomersAsync(app));
    }

    // ---- AC-29.3: edit ------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.3: a Seller changes a customer's name and notes (trimmed); <c>200</c> with the customer, and later reads show
    /// the new values. The id, active status and who created it and when do not change.
    /// </summary>
    [Fact]
    public async Task Edit_changes_name_and_notes()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Old notes");
        var before = await ReadCustomerAsync(seller, customer.Id);
        var newName = Seed.UniqueName("Renamed SA");

        var response = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}",
            new { name = $"  {newName} ", notes = " Ring the bell twice. " }, Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(customer.Id, body.GetProperty("id").GetInt32());
        Assert.Equal(newName, body.GetProperty("name").GetString());
        Assert.Equal("Ring the bell twice.", body.GetProperty("notes").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(before.GetProperty("createdAt").GetString(), body.GetProperty("createdAt").GetString());
        Assert.Equal(before.GetProperty("createdBy").GetRawText(), body.GetProperty("createdBy").GetRawText());
        Assert.Equal(body.GetRawText(), (await ReadCustomerAsync(seller, customer.Id)).GetRawText());
        Assert.Equal(NameNormalizer.Normalize(newName), (await LoadCustomerAsync(factory, customer.Id))!.NameNormalized);
    }

    /// <summary>AC-29.3, design 5.11: renaming a customer to its own name in another letter case is not a duplicate.</summary>
    [Fact]
    public async Task Edit_to_its_own_name_in_another_case_is_allowed()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);

        var response = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}", new { name = customer.Name.ToUpperInvariant() }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(customer.Name.ToUpperInvariant(), (await ApiJson.ReadAsync(response)).GetProperty("name").GetString());
    }

    /// <summary>
    /// AC-29.3, design 5.11: <c>PUT</c> is a full replacement, so leaving <c>notes</c> out, or sending <c>null</c> or
    /// blank, clears the stored notes.
    /// </summary>
    [Theory]
    [InlineData("""{ "name": "{name}" }""")]
    [InlineData("""{ "name": "{name}", "notes": null }""")]
    [InlineData("""{ "name": "{name}", "notes": "   " }""")]
    public async Task Edit_without_notes_clears_them(string template)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Deliveries before 10:00");

        var response = await seller.PutAsync($"{CustomersPath}/{customer.Id}",
            ApiJson.Content(template.Replace("{name}", customer.Name)), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ApiJson.ReadAsync(response)).GetProperty("notes").ValueKind);
        Assert.Null((await LoadCustomerAsync(factory, customer.Id))!.Notes);
    }

    /// <summary>
    /// AC-29.3 "under the same rules": another customer's name (ignoring case) is <c>409</c>; a blank name, a name over 100
    /// or notes over 500 is <c>400</c>. The customer keeps its name and notes each time.
    /// </summary>
    [Fact]
    public async Task Edit_follows_the_same_rules_as_create()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Kept");
        var other = await CreateCustomerAsync(seller);
        var path = $"{CustomersPath}/{customer.Id}";

        var duplicate = await seller.PutAsJsonAsync(path, new { name = $" {other.Name.ToUpperInvariant()} " }, Cancel);
        await ApiAssert.ProblemAsync(duplicate, HttpStatusCode.Conflict, "customer-name-taken",
            $"A customer named '{other.Name.ToUpperInvariant()}' already exists.");

        var blank = await seller.PutAsJsonAsync(path, new { name = "   ", notes = "New" }, Cancel);
        await ApiAssert.ValidationAsync(blank, "name", "Name is required.");

        var tooLong = await seller.PutAsJsonAsync(path, new { name = new string('x', 101) }, Cancel);
        await ApiAssert.ValidationAsync(tooLong, "name", "Name must be 100 characters or fewer.");

        var longNotes = await seller.PutAsJsonAsync(path, new { name = customer.Name, notes = new string('n', 501) }, Cancel);
        await ApiAssert.ValidationAsync(longNotes, "notes", "Notes must be 500 characters or fewer.");

        var stored = await LoadCustomerAsync(factory, customer.Id);
        Assert.Equal(customer.Name, stored!.Name);
        Assert.Equal("Kept", stored.Notes);
    }

    // ---- AC-29.4: deactivate and reactivate -------------------------------------------------------------------------

    /// <summary>
    /// AC-29.4: deactivate gives <c>isActive: false</c>, and a second deactivate gives <c>200</c> with the same body.
    /// The deactivated customer is still readable and listed (in the full list and under <c>isActive=false</c>, not under
    /// <c>isActive=true</c>). Reactivate works the same way. Nothing but <c>isActive</c> changes.
    /// </summary>
    [Fact]
    public async Task Deactivate_and_reactivate_are_repeatable_and_keep_the_customer_listed()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Kitchen door");
        var created = await ReadCustomerAsync(seller, customer.Id);

        var deactivated = await PostActionAsync(seller, customer.Id, "deactivate");
        var again = await PostActionAsync(seller, customer.Id, "deactivate");

        Assert.False(deactivated.GetProperty("isActive").GetBoolean());
        Assert.Equal(deactivated.GetRawText(), again.GetRawText());
        Assert.Equal(deactivated.GetRawText(), (await ReadCustomerAsync(seller, customer.Id)).GetRawText());
        Assert.Equal(created.GetRawText().Replace("\"isActive\":true", "\"isActive\":false"), deactivated.GetRawText());
        Assert.Contains(customer.Id, await ListedIdsAsync(seller, ""));
        Assert.Contains(customer.Id, await ListedIdsAsync(seller, "?isActive=false"));
        Assert.DoesNotContain(customer.Id, await ListedIdsAsync(seller, "?isActive=true"));

        var reactivated = await PostActionAsync(seller, customer.Id, "reactivate");
        var reactivatedAgain = await PostActionAsync(seller, customer.Id, "reactivate");

        Assert.Equal(created.GetRawText(), reactivated.GetRawText());
        Assert.Equal(created.GetRawText(), reactivatedAgain.GetRawText());
        Assert.Contains(customer.Id, await ListedIdsAsync(seller, "?isActive=true"));
    }

    /// <summary>
    /// Architecture 4.1 item 15: deactivate and reactivate take no body; one that is sent is ignored (as for approve), so
    /// a body asking for the opposite changes nothing.
    /// </summary>
    [Fact]
    public async Task Deactivate_ignores_a_body()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);

        var response = await seller.PostAsync($"{CustomersPath}/{customer.Id}/deactivate",
            ApiJson.Content("""{ "isActive": true, "name": "Something else" }"""), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(customer.Name, body.GetProperty("name").GetString());
    }

    // ---- AC-29.5: delete --------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.5, PRD example "Pizzeria Lago": deleting a customer that never had a sales order gives <c>204</c> with no body.
    /// Afterwards <c>GET</c> is <c>404</c>, it is not in the list, a second delete is <c>404</c>, and its name can be used
    /// for a new customer, which gets a new id (decision #162: an id is never reused).
    /// </summary>
    [Fact]
    public async Task Deleting_an_unused_customer_frees_its_name_but_not_its_id()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var name = Seed.UniqueName("Pizzeria Lago");
        var customer = await CreateCustomerAsync(seller, name);

        var response = await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Null(await LoadCustomerAsync(factory, customer.Id));
        await ApiAssert.ProblemAsync(await seller.GetAsync($"{CustomersPath}/{customer.Id}", Cancel),
            HttpStatusCode.NotFound, "not-found", $"Customer {customer.Id} not found.");
        Assert.DoesNotContain(customer.Id, await ListedIdsAsync(seller, ""));
        Assert.Equal(HttpStatusCode.NotFound, (await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel)).StatusCode);

        var again = await CreateCustomerAsync(seller, name);
        Assert.True(again.Id > customer.Id, $"The new customer got id {again.Id}; the deleted one had {customer.Id}.");
    }

    /// <summary>
    /// AC-29.5, PRD example: "Deli Norte" has one Cancelled sales order; deleting it is <c>409</c>
    /// <c>customer-has-sales-orders</c> "Deli Norte has sales orders and cannot be deleted. Deactivate it instead."
    /// (decision #152). The customer stays, and the next step the message names, deactivate, works.
    /// </summary>
    [Fact]
    public async Task Deleting_a_customer_with_a_cancelled_sales_order_is_409()
    {
        await using var fresh = new TestApiFactory();
        var (manager, user) = await OrderFlow.SignedInAsync(fresh, Roles.Manager);
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, "Deli Norte");
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await InsertSalesOrderAsync(fresh, customer.Id, item.Id, 600m, SalesOrderStatus.Cancelled, user.Id);

        var response = await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "customer-has-sales-orders",
            ExpectedMessages.DeliNorteHasSalesOrders);
        Assert.NotNull(await LoadCustomerAsync(fresh, customer.Id));
        Assert.False((await PostActionAsync(seller, customer.Id, "deactivate")).GetProperty("isActive").GetBoolean());
    }

    /// <summary>
    /// AC-29.5, decision #148: a sales order in any status counts, so the delete is <c>409</c> with the customer's current
    /// name whether the sales order is Created, Shipped or Cancelled.
    /// </summary>
    [Theory]
    [InlineData(SalesOrderStatus.Created)]
    [InlineData(SalesOrderStatus.Shipped)]
    [InlineData(SalesOrderStatus.Cancelled)]
    public async Task Deleting_a_customer_with_a_sales_order_in_any_status_is_409(SalesOrderStatus status)
    {
        var (manager, user) = await OrderFlow.SignedInAsync(factory, Roles.Manager);
        var customer = await CreateCustomerAsync(manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await InsertSalesOrderAsync(factory, customer.Id, item.Id, 5m, status, user.Id);

        var response = await manager.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "customer-has-sales-orders",
            $"{customer.Name} has sales orders and cannot be deleted. Deactivate it instead.");
        Assert.NotNull(await LoadCustomerAsync(factory, customer.Id));
    }

    /// <summary>
    /// AC-29.5: <c>CustomerService.DeleteAsync</c> checks exactly one reference to a customer, <c>SalesOrder.CustomerId</c>.
    /// This test reads every foreign key that points at <see cref="Customer"/> from the EF Core model, so a reference added
    /// later fails here until the service checks it too; otherwise its <c>Restrict</c> key would answer <c>500</c> instead
    /// of <c>409</c> (decision #46).
    /// </summary>
    [Fact]
    public async Task Has_sales_orders_check_covers_every_foreign_key_to_customers()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;

        var references = model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Customer))
            .Select(foreignKey =>
                $"{foreignKey.DeclaringEntityType.ClrType.Name}.{string.Join(",", foreignKey.Properties.Select(property => property.Name))}")
            .ToList();

        Assert.Equal(["SalesOrder.CustomerId"], references);
    }

    /// <summary>
    /// AC-29.3, AC-29.5, AC-29.6, design 7.2: an unknown id in the URL is <c>404</c> "Customer {id} not found." on every
    /// endpoint that names one. On the edit it comes before the body checks (architecture 5.1), so an invalid body still
    /// gets the <c>404</c>.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("POST", "/deactivate")]
    [InlineData("POST", "/reactivate")]
    [InlineData("DELETE", "")]
    public async Task Unknown_customer_is_404(string method, string action)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{CustomersPath}/999999{action}");
        if (method == "PUT")
        {
            request.Content = ApiJson.Content("""{ "name": "", "notes": null }""");
        }

        var response = await seller.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "Customer 999999 not found.");
    }

    // ---- AC-29.6: list ----------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.6, design 2.5: the list is newest first by creation time, then by id for the same second, paged with the total
    /// count. A customer stored with an earlier time but the highest id comes last: time decides, not id. Every row has the
    /// design 5.11 shape.
    /// </summary>
    [Fact]
    public async Task Customers_are_listed_newest_first_in_pages()
    {
        await using var timed = new TestApiFactory { FakeTime = new FakeTimeProvider(Start) };
        var (seller, user) = await OrderFlow.SignedInAsync(timed, Roles.Seller);
        var first = await CreateCustomerAsync(seller);
        var sameSecond = await CreateCustomerAsync(seller);
        timed.FakeTime!.Advance(TimeSpan.FromMinutes(1));
        var newest = await CreateCustomerAsync(seller, notes: "Newest");
        var olderId = await InsertCustomerAsync(timed, user.Id, Start.UtcDateTime.AddDays(-1));

        var all = await ApiJson.ReadAsync(await seller.GetAsync(CustomersPath, Cancel));
        var secondPage = await ApiJson.ReadAsync(await seller.GetAsync($"{CustomersPath}?page=2&pageSize=3", Cancel));
        var pastTheEnd = await ApiJson.ReadAsync(await seller.GetAsync($"{CustomersPath}?page=3&pageSize=3", Cancel));

        Assert.Equal([newest.Id, sameSecond.Id, first.Id, olderId], ApiJson.Ids(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        var top = all.GetProperty("items")[0];
        Assert.Equal(["id", "name", "notes", "isActive", "createdAt", "createdBy"], top.EnumerateObject().Select(field => field.Name));
        Assert.Equal("2026-12-01T08:31:00Z", top.GetProperty("createdAt").GetString());
        Assert.Equal("Newest", top.GetProperty("notes").GetString());
        Assert.Equal(user.Email, top.GetProperty("createdBy").GetProperty("email").GetString());
        Assert.Equal([olderId], ApiJson.Ids(secondPage));
        Assert.Equal(3, secondPage.GetProperty("pageSize").GetInt32());
        Assert.Empty(ApiJson.Ids(pastTheEnd));
        Assert.Equal(4, pastTheEnd.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// AC-29.6, design 5.11: <c>isActive=true</c> lists only active customers and <c>isActive=false</c> only deactivated
    /// ones, in any letter case; left out, empty or only spaces, every customer is listed. Warehouse users read the list too.
    /// </summary>
    [Fact]
    public async Task List_filters_by_active_status_in_any_letter_case()
    {
        await using var fresh = new TestApiFactory();
        var seller = await fresh.CreateClientAsAsync(Roles.Seller);
        var warehouse = await fresh.CreateClientAsAsync(Roles.Warehouse);
        var active = await CreateCustomerAsync(seller);
        var inactive = await CreateCustomerAsync(seller);
        await PostActionAsync(seller, inactive.Id, "deactivate");

        foreach (var query in new[] { "?isActive=true", "?isActive=TRUE", "?isActive=True" })
        {
            var page = await ApiJson.ReadAsync(await warehouse.GetAsync(CustomersPath + query, Cancel));
            Assert.Equal([active.Id], ApiJson.Ids(page));
            Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        }

        foreach (var query in new[] { "?isActive=false", "?isActive=FALSE", "?isActive=fAlSe" })
        {
            Assert.Equal([inactive.Id], ApiJson.Ids(await ApiJson.ReadAsync(await warehouse.GetAsync(CustomersPath + query, Cancel))));
        }

        // Decision #102a: missing, empty or only spaces means "not set", so no filter: 200 with every customer, not a 400.
        foreach (var query in new[] { "", "?isActive=", "?isActive=%20%20" })
        {
            var response = await warehouse.GetAsync(CustomersPath + query, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([inactive.Id, active.Id], ApiJson.Ids(await ApiJson.ReadAsync(response)));
        }
    }

    /// <summary>
    /// AC-29.6, design 7.2, decision #102a: an <c>isActive</c> other than exactly <c>true</c> or <c>false</c> (a word, a
    /// number, surrounding spaces) is <c>400</c> "isActive must be true or false." under <c>isActive</c>.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("truee")]
    [InlineData("%20true")]
    [InlineData("false%20")]
    public async Task Is_active_that_is_not_true_or_false_is_rejected(string value)
    {
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await warehouse.GetAsync($"{CustomersPath}?isActive={value}", Cancel);

        await ApiAssert.ValidationAsync(response, "isActive", ExpectedMessages.IsActiveMustBeBoolean);
    }

    /// <summary>
    /// Architecture 4.1 item 19, design 6: a bad <c>isActive</c> is reported together with the paging errors in one
    /// <c>400</c>, each under its own parameter.
    /// </summary>
    [Fact]
    public async Task Is_active_and_paging_errors_come_in_one_response()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await seller.GetAsync($"{CustomersPath}?isActive=yes&page=0&pageSize=101", Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"page must be 1 or greater. pageSize must be between 1 and 100. {ExpectedMessages.IsActiveMustBeBoolean}");
        Assert.Equal(["page must be 1 or greater."], ApiJson.Messages(body, "page"));
        Assert.Equal(["pageSize must be between 1 and 100."], ApiJson.Messages(body, "pageSize"));
        Assert.Equal([ExpectedMessages.IsActiveMustBeBoolean], ApiJson.Messages(body, "isActive"));
    }

    // ---- AC-29.7: roles ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-29.7, design 7.1: a Production-only user gets <c>403</c> "Your roles do not allow this action. It needs the
    /// Warehouse, Manager or Seller role." on listing customers and reading one. A Warehouse user reads both.
    /// </summary>
    [Fact]
    public async Task Production_only_user_cannot_read_customers()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);
        var production = await factory.CreateClientAsAsync(Roles.Production);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);

        await ApiAssert.ProblemAsync(await production.GetAsync(CustomersPath, Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);
        await ApiAssert.ProblemAsync(await production.GetAsync($"{CustomersPath}/{customer.Id}", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync(CustomersPath, Cancel)).StatusCode);
        Assert.Equal(customer.Name, (await ReadCustomerAsync(warehouse, customer.Id)).GetProperty("name").GetString());
    }

    /// <summary>
    /// AC-29.7, design 7.1: a user holding neither Manager nor Seller (Warehouse, Production, or both) gets <c>403</c>
    /// "Your roles do not allow this action. It needs the Manager or Seller role." on creating, editing, deactivating,
    /// reactivating and deleting a customer, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("POST", "", Roles.Warehouse)]
    [InlineData("POST", "", Roles.Production)]
    [InlineData("PUT", "/{id}", Roles.Warehouse)]
    [InlineData("PUT", "/{id}", Roles.Production)]
    [InlineData("PUT", "/{id}", "Warehouse,Production")]
    [InlineData("POST", "/{id}/deactivate", Roles.Warehouse)]
    [InlineData("POST", "/{id}/deactivate", Roles.Production)]
    [InlineData("POST", "/{id}/reactivate", Roles.Warehouse)]
    [InlineData("POST", "/{id}/reactivate", Roles.Production)]
    [InlineData("DELETE", "/{id}", Roles.Warehouse)]
    [InlineData("DELETE", "/{id}", Roles.Production)]
    public async Task Changes_by_users_without_manager_or_seller_are_403(string method, string action, string roles)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Unchanged");
        var before = await ReadCustomerAsync(seller, customer.Id);
        var count = await CountCustomersAsync(factory);
        var caller = await factory.CreateClientAsAsync(roles.Split(','));
        using var request = new HttpRequestMessage(new HttpMethod(method), CustomersPath + action.Replace("{id}", customer.Id.ToString()));
        if (method is "POST" or "PUT" && !action.Contains("activate"))
        {
            request.Content = JsonContent.Create(new { name = Seed.UniqueName("Refused"), notes = "Changed" });
        }

        var response = await caller.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
        Assert.Equal(count, await CountCustomersAsync(factory));
        Assert.Equal(before.GetRawText(), (await ReadCustomerAsync(seller, customer.Id)).GetRawText());
    }

    /// <summary>
    /// AC-29.1 to AC-29.5 ("a Seller or Manager"): a user holding only the Seller role creates, edits, deactivates,
    /// reactivates and deletes a customer, with no Manager involved.
    /// </summary>
    [Fact]
    public async Task Seller_alone_manages_a_customer_from_create_to_delete()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller);

        var edit = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}", new { name = Seed.UniqueName("Edited") }, Cancel);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.False((await PostActionAsync(seller, customer.Id, "deactivate")).GetProperty("isActive").GetBoolean());
        Assert.True((await PostActionAsync(seller, customer.Id, "reactivate")).GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel)).StatusCode);
    }

    /// <summary>
    /// Design 2.2, architecture 4.1 item 13: the role check comes before the <c>404</c> and the query checks. A
    /// Production-only user's read of an unknown customer, or with a bad <c>isActive</c>, is <c>403</c>; a Warehouse-only
    /// user's edit or delete of an unknown customer is <c>403</c>.
    /// </summary>
    [Fact]
    public async Task Role_check_comes_before_the_404_and_400()
    {
        var production = await factory.CreateClientAsAsync(Roles.Production);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);

        await ApiAssert.ProblemAsync(await production.GetAsync($"{CustomersPath}/999999", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);
        await ApiAssert.ProblemAsync(await production.GetAsync($"{CustomersPath}?isActive=yes", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);
        await ApiAssert.ProblemAsync(await warehouse.PutAsJsonAsync($"{CustomersPath}/999999", new { name = "" }, Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
        await ApiAssert.ProblemAsync(await warehouse.DeleteAsync($"{CustomersPath}/999999", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOrSeller);
    }

    /// <summary>AC-01.5, AC-29.7: every customer endpoint answers <c>401</c> <c>not-authenticated</c> without a token.</summary>
    [Theory]
    [InlineData("GET", CustomersPath)]
    [InlineData("GET", CustomersPath + "/1")]
    [InlineData("POST", CustomersPath)]
    [InlineData("PUT", CustomersPath + "/1")]
    [InlineData("POST", CustomersPath + "/1/deactivate")]
    [InlineData("POST", CustomersPath + "/1/reactivate")]
    [InlineData("DELETE", CustomersPath + "/1")]
    public async Task Requests_without_a_token_are_401(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = ApiJson.Content("""{ "name": "Deli Norte" }""");
        }

        var response = await anonymous.SendAsync(request, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- Decision #163: logging -------------------------------------------------------------------------------------

    /// <summary>
    /// Decision #163, architecture 6 Logging: customer notes may hold contact details and never appear in a log line.
    /// Notes carrying a unique marker go through every endpoint, including refused requests (too long, duplicate name),
    /// and no log entry's message or exception mentions the marker.
    /// </summary>
    [Fact]
    public async Task No_log_line_contains_customer_notes()
    {
        var marker = $"phone-{Guid.NewGuid():N}";
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: $"Call {marker}");

        Assert.Equal(HttpStatusCode.OK, (await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}",
            new { name = customer.Name, notes = $"New {marker}" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync(CustomersPath,
            new { name = customer.Name, notes = marker }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync(CustomersPath,
            new { name = Seed.UniqueName("c"), notes = marker + new string('n', 500) }, Cancel)).StatusCode);
        await ReadCustomerAsync(seller, customer.Id);
        await ListedIdsAsync(seller, "");
        await PostActionAsync(seller, customer.Id, "deactivate");
        await PostActionAsync(seller, customer.Id, "reactivate");
        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel)).StatusCode);

        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Message.Contains(marker, StringComparison.Ordinal)
            || (entry.Exception?.ToString().Contains(marker, StringComparison.Ordinal) ?? false));
    }

    // ---- SEC-26, decision #177: who changed a customer ------------------------------------------------------------

    /// <summary>
    /// SEC-26, decisions #177 and #179: an edit writes exactly one Information line after the commit, "Customer {id}
    /// updated by user {userId} (changed: ...)", naming the acting user and only the fields that changed. A rename alone
    /// says <c>name</c>, a notes edit alone says <c>notes</c>, and both in one <c>PUT</c> give one line saying
    /// <c>name, notes</c>, not two lines.
    /// </summary>
    [Theory]
    [InlineData(true, false, "name")]
    [InlineData(false, true, "notes")]
    [InlineData(true, true, "name, notes")]
    public async Task Edit_is_logged_once_with_the_ids_and_the_changed_fields(bool rename, bool editNotes, string changed)
    {
        var (seller, user) = await OrderFlow.SignedInAsync(factory, Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Old notes");
        var name = rename ? Seed.UniqueName("Renamed") : customer.Name;
        var notes = editNotes ? "New notes" : "Old notes";

        var response = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}", new { name, notes }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(CustomerLines(customer.Id));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal($"Customer {customer.Id} updated by user {user.Id} (changed: {changed}).", line.Message);
    }

    /// <summary>
    /// SEC-26, decision #177: deactivate, reactivate and delete each write one Information line after the commit with the
    /// customer id and the acting user id. A Manager's changes name the Manager, a Seller's the Seller. Repeating a
    /// deactivate or reactivate changes nothing and writes no second line.
    /// </summary>
    [Fact]
    public async Task Deactivate_reactivate_and_delete_are_logged_once_each_with_the_ids()
    {
        var (seller, sellerUser) = await OrderFlow.SignedInAsync(factory, Roles.Seller);
        var (manager, managerUser) = await OrderFlow.SignedInAsync(factory, Roles.Manager);
        var customer = await CreateCustomerAsync(seller);

        await PostActionAsync(seller, customer.Id, "deactivate");
        await PostActionAsync(manager, customer.Id, "deactivate"); // no-op: already deactivated
        await PostActionAsync(manager, customer.Id, "reactivate");
        await PostActionAsync(seller, customer.Id, "reactivate"); // no-op: already active
        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel)).StatusCode);

        var lines = CustomerLines(customer.Id);
        Assert.Equal(
            [
                $"Customer {customer.Id} deactivated by user {sellerUser.Id}.",
                $"Customer {customer.Id} reactivated by user {managerUser.Id}.",
                $"Customer {customer.Id} deleted by user {sellerUser.Id}.",
            ],
            lines.Select(line => line.Message));
        Assert.All(lines, line => Assert.Equal(LogLevel.Information, line.Level));
    }

    /// <summary>
    /// SEC-26, decision #177: a <c>PUT</c> with the values the customer already has changes nothing, so it writes no line,
    /// like a repeated deactivate. (A change of letter case only is a real rename; see
    /// <see cref="Change_lines_never_contain_the_customer_name_or_notes"/>.)
    /// </summary>
    [Fact]
    public async Task Edit_that_changes_nothing_writes_no_line()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: "Same notes");

        // Sent with spaces around both: trimming makes them equal to what is stored, so nothing changes.
        var response = await seller.PutAsJsonAsync($"{CustomersPath}/{customer.Id}",
            new { name = $"  {customer.Name} ", notes = " Same notes " }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(CustomerLines(customer.Id));
    }

    /// <summary>
    /// SEC-26, decision #177 ("no name, no notes"): with unique markers in the old and new name and the old and new notes,
    /// a case-only rename (still logged as a rename), a notes edit, deactivate, reactivate and delete are all logged, and
    /// no log entry anywhere, message or exception, contains any of the four values. The refused delete of a customer with
    /// a sales order is included, because its <c>409</c> sentence quotes the name: the sentence goes to the client only.
    /// </summary>
    [Fact]
    public async Task Change_lines_never_contain_the_customer_name_or_notes()
    {
        var (manager, user) = await OrderFlow.SignedInAsync(factory, Roles.Manager);
        var oldName = $"Name {Guid.NewGuid():N}";
        var oldNotes = $"Old address {Guid.NewGuid():N}";
        var newNotes = $"New address {Guid.NewGuid():N}";
        var newName = oldName.ToUpperInvariant();
        var customer = await CreateCustomerAsync(manager, oldName, oldNotes);
        var used = await CreateCustomerAsync(manager, $"Used {Guid.NewGuid():N}", oldNotes);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await InsertSalesOrderAsync(factory, used.Id, item.Id, 5m, SalesOrderStatus.Created, user.Id);

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"{CustomersPath}/{customer.Id}",
            new { name = newName, notes = newNotes }, Cancel)).StatusCode);
        await PostActionAsync(manager, customer.Id, "deactivate");
        await PostActionAsync(manager, customer.Id, "reactivate");
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{CustomersPath}/{used.Id}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"{CustomersPath}/{customer.Id}", Cancel)).StatusCode);

        // The lines were written, so the check below is not passing on an empty log.
        Assert.Equal(4, CustomerLines(customer.Id).Count);
        Assert.Equal($"Customer {customer.Id} updated by user {user.Id} (changed: name, notes).", CustomerLines(customer.Id)[0].Message);
        foreach (var secret in new[] { oldName, newName, oldNotes, newNotes, used.Name })
        {
            Assert.DoesNotContain(factory.Logs.Entries, entry =>
                entry.Message.Contains(secret, StringComparison.Ordinal)
                || (entry.Exception?.ToString().Contains(secret, StringComparison.Ordinal) ?? false));
        }
    }

    /// <summary>
    /// SEC-26, decision #177: a refused change writes no line. Tried on one customer: <c>400</c> (blank name, notes too
    /// long), <c>409</c> (another customer's name; delete with a sales order), <c>403</c> (a Warehouse user's edit,
    /// deactivate, reactivate and delete) and <c>404</c> (every change endpoint on an unknown id). Afterwards the customer
    /// service has logged nothing at all, and the customer is unchanged.
    /// </summary>
    [Fact]
    public async Task Refused_changes_write_no_line()
    {
        var (manager, user) = await OrderFlow.SignedInAsync(factory, Roles.Manager);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var customer = await CreateCustomerAsync(manager, notes: "Kept");
        var other = await CreateCustomerAsync(manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        await InsertSalesOrderAsync(factory, customer.Id, item.Id, 5m, SalesOrderStatus.Cancelled, user.Id);
        var before = await ReadCustomerAsync(manager, customer.Id);
        var path = $"{CustomersPath}/{customer.Id}";
        var linesBefore = ServiceLineCount();

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync(path, new { name = "   ", notes = "New" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await manager.PutAsJsonAsync(path, new { name = customer.Name, notes = new string('n', 501) }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync(path, new { name = other.Name }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PutAsJsonAsync(path, new { name = Seed.UniqueName("Refused") }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PostAsync($"{path}/deactivate", content: null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PostAsync($"{path}/reactivate", content: null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.DeleteAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"{CustomersPath}/999999", new { name = Seed.UniqueName("Gone") }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"{CustomersPath}/999999/deactivate", content: null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"{CustomersPath}/999999/reactivate", content: null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync($"{CustomersPath}/999999", Cancel)).StatusCode);

        Assert.Equal(linesBefore, ServiceLineCount());
        Assert.Equal(before.GetRawText(), (await ReadCustomerAsync(manager, customer.Id)).GetRawText());
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// SEC-26: the lines <see cref="CustomerService"/> logged about customer <paramref name="id"/>,
    /// oldest first. Filtered by the logger's category and the "Customer {id} " prefix (with the space, so customer 12
    /// does not match 123).
    /// </summary>
    private List<CapturingLoggerProvider.LogEntry> CustomerLines(int id) =>
        factory.Logs.Entries
            .Where(entry => entry.Category == typeof(CustomerService).FullName
                && entry.Message.StartsWith($"Customer {id} ", StringComparison.Ordinal))
            .ToList();

    /// <summary>How many lines the customer service has logged so far, about any customer.</summary>
    private int ServiceLineCount() =>
        factory.Logs.Entries.Count(entry => entry.Category == typeof(CustomerService).FullName);

    /// <summary><c>GET /customers/{id}</c>, checked for <c>200</c>; returns the body.</summary>
    private static async Task<JsonElement> ReadCustomerAsync(HttpClient client, int id)
    {
        var response = await client.GetAsync($"{CustomersPath}/{id}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary><c>POST /customers/{id}/{action}</c> with no body, checked for <c>200</c>; returns the body.</summary>
    private static async Task<JsonElement> PostActionAsync(HttpClient client, int id, string action)
    {
        var response = await client.PostAsync($"{CustomersPath}/{id}/{action}", content: null, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The ids of every customer in the list with <paramref name="query"/> (all pages).</summary>
    private static async Task<List<int>> ListedIdsAsync(HttpClient client, string query) =>
        (await ScenarioApi.AllRowsAsync(client, CustomersPath + query)).Select(row => row.GetProperty("id").GetInt32()).ToList();

    private static async Task<Customer?> LoadCustomerAsync(TestApiFactory app, int id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.AsNoTracking()
            .SingleOrDefaultAsync(customer => customer.Id == id, Cancel);
    }

    private static async Task<int> CountCustomersAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.CountAsync(Cancel);
    }

    /// <summary>
    /// Inserts a customer straight into the database with the given creation time, so a test can store one whose time and
    /// id disagree (an older time with a higher id). Returns its id.
    /// </summary>
    private static async Task<int> InsertCustomerAsync(TestApiFactory app, string userId, DateTime createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = Seed.UniqueName("Older");
        var customer = new Customer
        {
            Name = name, NameNormalized = NameNormalizer.Normalize(name), CreatedAt = createdAt, CreatedById = userId,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(Cancel);
        return customer.Id;
    }
}
