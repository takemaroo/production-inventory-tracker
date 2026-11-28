using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.2) T-038: the API's JSON output escapes HTML-sensitive characters inside strings (PRD A-5, AC-37.9; architecture
/// 14, decision #230), on controller answers and on ProblemDetails error bodies alike, while every value parses back
/// exactly as stored and accented letters stay readable.
/// </summary>
/// <remarks>
/// <para>
/// These are raw-byte tests: each reads the response body as bytes, exactly as they travel, before parsing it. The rest
/// of the suite reads bodies parsed (<see cref="ApiJson.ReadAsync"/>), where an escape and the character it stands for
/// look the same, so only tests like these can see the change.
/// </para>
/// <para>
/// The expected escapes are built from parts (<see cref="Escaped"/>) and never typed as string literals. A literal
/// backslash-u sequence in C# source is turned into the character itself by the compiler (and can be by the tools that
/// edit files), so a typed escape could silently become a raw <c>&lt;</c> and the test would prove nothing.
/// </para>
/// <para>
/// Tests in this class share one database (<see cref="IClassFixture{TFixture}"/>). The fixed names of AC-37.1 and
/// "Crème" are each created by one test only, so they cannot clash; the notification test needs fixed order numbers and
/// a unit named "kg" of its own, so it starts its own app.
/// </para>
/// </remarks>
public class JsonEscapingTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    // AC-37.1's test data, typed out as the PRD gives it.
    private const string ScriptCustomerName = "</script><script>alert(1)</script>";
    private const string MarkupNotes = "<b>bold</b> & 'quote'";
    private const string ImageItemName = "<img src=x onerror=alert(1)>";
    private const string LinkReason = "<a href=\"https://example.com\">click</a>";

    /// <summary>The backslash, as a character code so no backslash-u JSON escape is typed in this file (see the remarks).</summary>
    private const char Backslash = (char)92;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-37.9: controller answers -----------------------------------------------------------------------------------

    /// <summary>
    /// AC-37.9, the PRD example: the raw bytes of <c>GET /customers/{id}</c> for the AC-37.1 customer named
    /// <c>&lt;/script&gt;&lt;script&gt;alert(1)&lt;/script&gt;</c> hold no <c>&lt;</c> (nor <c>&gt;</c>), and hold
    /// the escape for <c>&lt;</c> (backslash, <c>u003C</c>). After parsing, the name is exactly as created.
    /// </summary>
    [Fact]
    public async Task Customer_name_with_a_script_tag_has_no_raw_angle_bracket_and_parses_unchanged()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, ScriptCustomerName);

        var response = await seller.GetAsync($"{CustomersPath}/{customer.Id}", Cancel);
        var (raw, body) = await ReadRawAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain('<', raw);
        Assert.DoesNotContain('>', raw);
        Assert.Contains(Escaped('<'), raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Escaped('>'), raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScriptCustomerName, body.GetProperty("name").GetString());
    }

    /// <summary>
    /// AC-37.9 with the AC-37.1 notes <c>&lt;b&gt;bold&lt;/b&gt; &amp; 'quote'</c>: the raw bytes hold none of
    /// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> or <c>'</c>, each is there as its escape (the apostrophe as backslash,
    /// <c>u0027</c>), and the notes parse back unchanged.
    /// </summary>
    [Fact]
    public async Task Customer_notes_with_markup_hold_none_of_the_four_characters_raw()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, notes: MarkupNotes);

        var response = await seller.GetAsync($"{CustomersPath}/{customer.Id}", Cancel);
        var (raw, body) = await ReadRawAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var character in new[] { '<', '>', '&', '\'' })
        {
            Assert.DoesNotContain(character, raw);
            Assert.Contains(Escaped(character), raw, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(MarkupNotes, body.GetProperty("notes").GetString());
    }

    /// <summary>
    /// AC-37.9: "Accented letters stay readable as they are." The customer "Crème" appears in the raw UTF-8 bytes as
    /// "Crème", not as an escape, and parses back unchanged.
    /// </summary>
    [Fact]
    public async Task Accented_name_stays_readable_in_the_raw_bytes()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var customer = await CreateCustomerAsync(seller, "Crème");

        var response = await seller.GetAsync($"{CustomersPath}/{customer.Id}", Cancel);
        var (raw, body) = await ReadRawAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"name\":\"Crème\"", raw);
        Assert.DoesNotContain(Escaped('è'), raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Crème", body.GetProperty("name").GetString());
    }

    /// <summary>
    /// Architecture 14 (effects of A-5): a character beyond the Basic Multilingual Plane, such as the cheese emoji, is
    /// written as its two surrogate escapes, and still parses back as the same single character. This is a
    /// characterisation pin on architecture 14, not evidence for AC-37.9: it passes without T-038 too, because the
    /// framework's earlier relaxed default already escaped characters beyond the plane.
    /// </summary>
    [Fact]
    public async Task Emoji_is_written_as_surrogate_escapes_and_parses_unchanged()
    {
        const string cheese = "\U0001F9C0";
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var name = Seed.UniqueName("Queso " + cheese);
        var customer = await CreateCustomerAsync(seller, name);

        var response = await seller.GetAsync($"{CustomersPath}/{customer.Id}", Cancel);
        var (raw, body) = await ReadRawAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(cheese, raw);
        Assert.Contains(Escaped(cheese[0]) + Escaped(cheese[1]), raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(name, body.GetProperty("name").GetString());
    }

    // ---- AC-37.9: ProblemDetails error bodies --------------------------------------------------------------------------

    /// <summary>
    /// AC-37.9 on an error body, AC-06.2: a second unit named "kg" is <c>409</c> <c>unit-name-taken</c>. The raw
    /// ProblemDetails holds no apostrophe; it holds the escape (backslash, <c>u0027</c>) twice, and its <c>detail</c>
    /// parses back exactly as "A unit named 'kg' already exists.". Error bodies are written with the minimal-API JSON
    /// settings, not the controllers', so this test fails if only one of the two settings has the encoder.
    /// </summary>
    [Fact]
    public async Task Problem_detail_with_quotes_escapes_the_apostrophe_and_parses_back_exactly()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        await Seed.CreateUnitAsync(manager, "kg");

        var response = await manager.PostAsJsonAsync("/api/v1/units", new { name = "kg" }, Cancel);
        var (raw, body) = await ReadRawAsync(response);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain('\'', raw);
        Assert.Contains(Escaped('\'') + "kg" + Escaped('\''), raw, StringComparison.OrdinalIgnoreCase);
        ApiAssert.Problem(response, body, HttpStatusCode.Conflict, "unit-name-taken", "A unit named 'kg' already exists.");
    }

    // ---- AC-37.9: notifications ----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-37.9 on a notification raised by a real event (AC-21.3, AC-23.2): an order whose raw material is the AC-37.1
    /// item <c>&lt;img src=x onerror=alert(1)&gt;</c> is sent and then cancelled with the AC-37.1 reason
    /// <c>&lt;a href="https://example.com"&gt;click&lt;/a&gt;</c>. The raw bytes of <c>GET /notifications</c> and of
    /// <c>GET /notifications/{id}</c> hold no <c>&lt;</c> or <c>&gt;</c> in the <c>message</c> nor in the <c>data</c>
    /// (which the API stores as JSON text and writes out again), and both parse back to the exact texts.
    /// </summary>
    [Fact]
    public async Task Notification_message_and_data_hold_no_raw_angle_brackets()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: ImageItemName);
        var product = await ProductWithRecipeAsync(manager, (material.Id, 1m));
        await ReceiveAsync(manager, material.Id, 10m);
        var number = await CreateOrderAsync(manager, product.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (material.Id, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(manager, number, LinkReason)).StatusCode);

        var listResponse = await manager.GetAsync("/api/v1/notifications", Cancel);
        var (listRaw, list) = await ReadRawAsync(listResponse);
        var id = Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32();
        var response = await manager.GetAsync($"/api/v1/notifications/{id}", Cancel);
        var (raw, notification) = await ReadRawAsync(response);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var text in new[] { listRaw, raw })
        {
            Assert.DoesNotContain('<', text);
            Assert.DoesNotContain('>', text);
            Assert.Contains(Escaped('<') + "img", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Escaped('<') + "a href", text, StringComparison.OrdinalIgnoreCase);
        }

        // Design 7.3, the OrderCancelledWithMaterial message, typed out with the AC-37.1 item name and reason.
        Assert.Equal(
            $"Order #{number} was cancelled with material still in production storage: <img src=x onerror=alert(1)> 10 kg. " +
            "Reason: \"<a href=\"https://example.com\">click</a>\"",
            notification.GetProperty("message").GetString());
        var data = notification.GetProperty("data");
        Assert.Equal(ImageItemName, Assert.Single(data.GetProperty("materials").EnumerateArray()).GetProperty("itemName").GetString());
        Assert.Equal(LinkReason, data.GetProperty("reason").GetString());

        // The data object is in the raw bytes with its own escapes too, not only the message: its reason field
        // begins with the escaped "<a".
        Assert.Contains("\"reason\":\"" + Escaped('<') + "a", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The six-character JSON escape for <paramref name="character"/>: a backslash, the letter <c>u</c> and four hex
    /// digits, in upper case as System.Text.Json writes them (the escape for <c>&lt;</c> ends in <c>003C</c>).
    /// </summary>
    private static string Escaped(char character) => $"{Backslash}u{(int)character:X4}";

    /// <summary>
    /// The response body exactly as it travelled, decoded from UTF-8 but not parsed, and the same body parsed as JSON.
    /// </summary>
    private static async Task<(string Raw, JsonElement Body)> ReadRawAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(Cancel);
        var raw = Encoding.UTF8.GetString(bytes);
        using var document = JsonDocument.Parse(bytes);
        return (raw, document.RootElement.Clone());
    }
}
