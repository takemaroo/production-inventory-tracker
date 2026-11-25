using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// BUG-002, decisions #170 and #171: the one name rule, through the API, for all three name lists: units (AC-06.2),
/// items (AC-07.1) and customers (AC-29.2). A name is compared and stored in Unicode NFC, so the same letters in
/// another Unicode form are a duplicate; a name with nothing visible is "Name is required."; the length counts what is
/// stored. Each test runs once per list, for create and for rename.
/// </summary>
/// <remarks>
/// Tests share one database (<see cref="IClassFixture{TFixture}"/>), so every name carries a unique suffix. Special
/// characters are written as escapes so the two Unicode forms cannot be swapped by an editor.
/// </remarks>
public class NameUnicodeTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The three lists that share the rule; each test is a theory over them.</summary>
    public static TheoryData<string> Lists => ["units", "items", "customers"];

    /// <summary>Every list, with each invisible-only name decision #171 must refuse (written as JSON escapes).</summary>
    public static TheoryData<string, string> ListsAndInvisibleNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var list in new[] { "units", "items", "customers" })
        {
            data.Add(list, @"\u200B");             // zero width space alone (BUG-002 notes)
            data.Add(list, @"  \u200B \t ");       // white space around it
            data.Add(list, @"\uFEFF\u2060\u200D"); // byte order mark, word joiner, zero width joiner
            data.Add(list, @"\t\u0000");           // tab and NUL (control characters)
        }

        return data;
    }

    /// <summary>
    /// Decision #170, AC-06.2 / AC-07.1 / AC-29.2: a name typed with a combining accent is a duplicate of the same name
    /// typed with the precomposed letter. <c>409</c> with the list's name-taken code and message, the name quoted in NFC
    /// (it looks exactly as typed), and only one row has that name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Create_with_a_look_alike_of_an_existing_name_is_409(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(manager, list, precomposed)).StatusCode);

        var response = await CreateAsync(manager, list, decomposed);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, TakenCode(list), TakenMessage(list, precomposed));
        Assert.Equal(1, await CountWithKeyAsync(list, precomposed));
    }

    /// <summary>
    /// Decision #170, AC-06.3 / AC-07.3 / AC-29.3 (rename under the create rules): renaming one record to a look-alike of
    /// another record's name is <c>409</c>, and the renamed record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Rename_to_a_look_alike_of_another_name_is_409(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(manager, list, precomposed)).StatusCode);
        var otherName = Seed.UniqueName("other");
        var other = await IdOfAsync(await CreateAsync(manager, list, otherName));

        var response = await RenameAsync(manager, list, other, decomposed);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, TakenCode(list), TakenMessage(list, precomposed));
        Assert.Equal(otherName, (await StoredAsync(list, other)).Name);
    }

    /// <summary>
    /// Decision #170: a decomposed name is stored, returned and keyed in NFC on create. A rename of the record to its
    /// own name in another Unicode form and letter case is allowed (its own name never counts as taken) and is stored in
    /// NFC too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Decomposed_name_is_stored_in_NFC_on_create_and_rename(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();

        var created = await CreateAsync(manager, list, $"  {decomposed}  ");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(precomposed, (await ApiJson.ReadAsync(created)).GetProperty("name").GetString());
        var id = await IdOfAsync(created);
        Assert.Equal((precomposed, precomposed.ToUpperInvariant()), await StoredAsync(list, id));

        // "CAFE" + combining accent + " SUR": the record's own name, decomposed and in upper case.
        var renamed = await RenameAsync(manager, list, id, decomposed.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var upperNfc = precomposed.ToUpperInvariant();
        Assert.Equal(upperNfc, (await ApiJson.ReadAsync(renamed)).GetProperty("name").GetString());
        Assert.Equal((upperNfc, upperNfc), await StoredAsync(list, id));
    }

    /// <summary>
    /// Decision #171, AC-06.2 / AC-07.2 / AC-29.2: a name with no visible character is refused on create with the same
    /// <c>400</c> "Name is required." as an empty name, and nothing is saved.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListsAndInvisibleNames))]
    public async Task Create_with_an_invisible_only_name_is_name_required(string list, string jsonName)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, jsonName);
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name is required.");
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #171, AC-06.3 / AC-07.3 / AC-29.3: renaming to a name with no visible character is the same <c>400</c>
    /// "Name is required.", and the record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListsAndInvisibleNames))]
    public async Task Rename_to_an_invisible_only_name_is_name_required(string list, string jsonName)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));
        var json = await BodyJsonAsync(manager, list, jsonName);

        var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name is required.");
        Assert.Equal(name, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Decision #171: invisible characters among visible ones are allowed and kept exactly as sent (a zero width joiner
    /// and a leading zero width space here), on create and on rename.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Invisible_characters_among_visible_ones_are_kept(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var withJoiner = $"Caf\u00E9\u200DSur {Suffix()}";
        var withSpace = $"\u200BCaf\u00E9 Sur {Suffix()}";

        var created = await CreateAsync(manager, list, withJoiner);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await IdOfAsync(created);
        Assert.Equal(withJoiner, (await StoredAsync(list, id)).Name);

        var renamed = await RenameAsync(manager, list, id, withSpace);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(withSpace, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Design 2.6 with decision #170: the length limit (units 50, items and customers 100, decision #116 code points) is
    /// checked on the stored NFC text. A name of exactly the limit after NFC is accepted although the decomposed text as
    /// sent is longer; one more letter is refused with the length message.
    /// </summary>
    [Theory]
    [InlineData("units", 50)]
    [InlineData("items", 100)]
    [InlineData("customers", 100)]
    public async Task Length_limit_counts_the_name_after_NFC(string list, int limit)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var suffix = Suffix(); // 8 letters, so the rest is limit - 8 decomposed "\u00E9" (2 code points each as sent)
        var atLimit = suffix + string.Concat(Enumerable.Repeat("e\u0301", limit - suffix.Length));
        var overLimit = Suffix() + string.Concat(Enumerable.Repeat("e\u0301", limit - suffix.Length + 1));

        var accepted = await CreateAsync(manager, list, atLimit);
        var refused = await CreateAsync(manager, list, overLimit);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var stored = (await ApiJson.ReadAsync(accepted)).GetProperty("name").GetString()!;
        Assert.Equal(limit, stored.Length); // every "\u00E9" is one UTF-16 unit and one code point once in NFC
        await ApiAssert.ValidationAsync(refused, "name", $"Name must be {limit} characters or fewer.");
    }

    /// <summary>
    /// Decision #170 safety net, first of two cases where <c>string.Normalize</c> would throw: a lone surrogate (half
    /// of an emoji's UTF-16 pair). Such a name never reaches the rule: the JSON reader refuses it with <c>400</c>
    /// <c>validation</c>, so it cannot become a <c>500</c>, and nothing is saved. The second case, U+FFFE, does reach
    /// the rule and is covered by the two tests below (decision #174).
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Name_with_a_lone_surrogate_is_400_not_500(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, @"Caf\uD800 Sur"); // JSON escape of a high surrogate with no pair
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ApiJson.ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #174 (BUG-002 review), AC-06.2 / AC-07.1 / AC-29.2: a name holding U+FFFE (valid JSON, sent here as
    /// the escape) is refused on create with <c>400</c> "Name contains a character that is not allowed." under
    /// <c>name</c>, not a <c>500</c>, and nothing is saved.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Create_with_U_FFFE_in_the_name_is_400_not_allowed(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, @"Caf\uFFFE Sur");
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #174, AC-06.3 / AC-07.3 / AC-29.3: renaming to a name holding U+FFFE is the same <c>400</c>, and the
    /// record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Rename_with_U_FFFE_in_the_name_is_400_not_allowed(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));
        var json = await BodyJsonAsync(manager, list, @"Caf\uFFFE Sur");

        var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
        Assert.Equal(name, (await StoredAsync(list, id)).Name);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A unique name in its two Unicode forms: precomposed "é" (U+00E9) and "e" + U+0301.</summary>
    private static (string Precomposed, string Decomposed) LookAlikes()
    {
        var suffix = Suffix();
        return ($"Caf\u00E9 Sur {suffix}", $"Cafe\u0301 Sur {suffix}");
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string PathOf(string list) => $"/api/v1/{list}";

    private static string TakenCode(string list) => list switch
    {
        "units" => "unit-name-taken",
        "items" => "item-name-taken",
        _ => "customer-name-taken",
    };

    private static string TakenMessage(string list, string name) => list switch
    {
        "units" => $"A unit named '{name}' already exists.",
        "items" => $"An item named '{name}' already exists.",
        _ => $"A customer named '{name}' already exists.",
    };

    /// <summary>POST a new record named <paramref name="name"/>; an item gets a new unit and the RawMaterial type.</summary>
    private async Task<HttpResponseMessage> CreateAsync(HttpClient manager, string list, string name) =>
        await manager.PostAsJsonAsync(PathOf(list), await BodyAsync(manager, list, name), Cancel);

    /// <summary>
    /// PUT the record's full body with a new name. An item gets a fresh unit, which is allowed because nothing uses the
    /// item yet (AC-07.3).
    /// </summary>
    private async Task<HttpResponseMessage> RenameAsync(HttpClient manager, string list, int id, string name) =>
        await manager.PutAsJsonAsync($"{PathOf(list)}/{id}", await BodyAsync(manager, list, name), Cancel);

    private static async Task<object> BodyAsync(HttpClient manager, string list, string name) => list switch
    {
        "items" => new { name, type = "RawMaterial", unitId = (await Seed.CreateUnitAsync(manager)).Id },
        _ => new { name },
    };

    /// <summary>
    /// A raw JSON body whose name is <paramref name="jsonName"/> as written (JSON escapes kept), every other field valid,
    /// so the only problem in the request is the name.
    /// </summary>
    private static async Task<string> BodyJsonAsync(HttpClient manager, string list, string jsonName) => list switch
    {
        "items" => $$"""{ "name": "{{jsonName}}", "type": "RawMaterial", "unitId": {{(await Seed.CreateUnitAsync(manager)).Id}} }""",
        _ => $$"""{ "name": "{{jsonName}}" }""",
    };

    private static async Task<int> IdOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("id").GetInt32();
    }

    /// <summary>The stored Name and NameNormalized of one record, read straight from the database.</summary>
    private async Task<(string Name, string Normalized)> StoredAsync(string list, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = list switch
        {
            "units" => await db.Units.Where(u => u.Id == id).Select(u => new { u.Name, u.NameNormalized }).SingleAsync(Cancel),
            "items" => await db.Items.Where(i => i.Id == id).Select(i => new { i.Name, i.NameNormalized }).SingleAsync(Cancel),
            _ => await db.Customers.Where(c => c.Id == id).Select(c => new { c.Name, c.NameNormalized }).SingleAsync(Cancel),
        };
        return (row.Name, row.NameNormalized);
    }

    /// <summary>How many records of the list have the comparison key of <paramref name="name"/>.</summary>
    private async Task<int> CountWithKeyAsync(string list, string name)
    {
        var key = NameNormalizer.Normalize(name);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return list switch
        {
            "units" => await db.Units.CountAsync(u => u.NameNormalized == key, Cancel),
            "items" => await db.Items.CountAsync(i => i.NameNormalized == key, Cancel),
            _ => await db.Customers.CountAsync(c => c.NameNormalized == key, Cancel),
        };
    }

    private async Task<int> CountAsync(string list)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return list switch
        {
            "units" => await db.Units.CountAsync(Cancel),
            "items" => await db.Items.CountAsync(Cancel),
            _ => await db.Customers.CountAsync(Cancel),
        };
    }
}
