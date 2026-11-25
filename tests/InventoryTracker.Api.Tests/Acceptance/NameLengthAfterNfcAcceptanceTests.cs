using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// (v1.1) QA acceptance test (phase 5), BUG-002 re-test: design 2.6 says a unit, item or customer name's length "is
/// counted after conversion" to NFC (decisions #170, #173). The developer's tests cover the direction where NFC makes a
/// name shorter ("e" + accent becomes one letter). This covers the other direction: a few characters are split in two
/// or three by NFC, because Unicode excludes them from composition. U+0958 DEVANAGARI LETTER QA becomes U+0915 U+093C
/// (two code points). So a name that is within the limit as sent can be over it once stored, and must be refused.
/// </summary>
/// <remarks>
/// Limits from design 2.6 and decision #116 (code points): units 50 (AC-06.1), items 100 (decision #69), customers 100
/// (AC-29.2). Characters are written as <c>\u</c> escapes so an editor cannot change them.
/// </remarks>
public class NameLengthAfterNfcAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>One code point as sent, two after NFC: U+0958 becomes U+0915 U+093C.</summary>
    private const string GrowsInNfc = "\u0958";

    /// <summary>
    /// AC-06.1 / AC-29.2, design 2.6 (BUG-002 fix): a name exactly at the limit once in NFC is accepted and stored in
    /// NFC; a name of exactly the limit as sent that NFC makes longer is refused with the length message, on create and
    /// on rename, and nothing is saved under it.
    /// </summary>
    [Theory]
    [InlineData("units", 50)]
    [InlineData("items", 100)]
    [InlineData("customers", 100)]
    public async Task Design_2_6_name_that_NFC_makes_longer_than_the_limit_is_refused(string list, int limit)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        // 8 letters + (limit - 8) / 2 copies: limit code points once each copy is two (limit is even).
        var suffix = Suffix();
        var atLimitAfterNfc = suffix + string.Concat(Enumerable.Repeat(GrowsInNfc, (limit - suffix.Length) / 2));
        var accepted = await CreateAsync(manager, list, atLimitAfterNfc);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var stored = (await ApiJson.ReadAsync(accepted)).GetProperty("name").GetString()!;
        Assert.Equal(limit, stored.Length); // every code point here is one UTF-16 unit
        Assert.Equal(atLimitAfterNfc.Normalize(), stored);
        // Ordinal: a culture-aware search treats U+0958 and U+0915 U+093C as the same text.
        Assert.False(stored.Contains(GrowsInNfc, StringComparison.Ordinal));

        // 8 letters + (limit - 8) copies: exactly the limit as sent, 2 * limit - 8 after NFC.
        var overOnlyAfterNfc = Suffix() + string.Concat(Enumerable.Repeat(GrowsInNfc, limit - 8));
        Assert.Equal(limit, overOnlyAfterNfc.Length);
        var refused = await CreateAsync(manager, list, overOnlyAfterNfc);
        await ApiAssert.ValidationAsync(refused, "name", $"Name must be {limit} characters or fewer.");

        var id = (await ApiJson.ReadAsync(accepted)).GetProperty("id").GetInt32();
        var renamed = await manager.PutAsJsonAsync($"/api/v1/{list}/{id}", await BodyAsync(manager, list, overOnlyAfterNfc), Cancel);
        await ApiAssert.ValidationAsync(renamed, "name", $"Name must be {limit} characters or fewer.");

        // The rename changed nothing: the record keeps its accepted name.
        var reread = await manager.GetAsync($"/api/v1/{list}/{id}", Cancel);
        Assert.Equal(stored, (await ApiJson.ReadAsync(reread)).GetProperty("name").GetString());
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpResponseMessage> CreateAsync(HttpClient manager, string list, string name) =>
        await manager.PostAsJsonAsync($"/api/v1/{list}", await BodyAsync(manager, list, name), Cancel);

    /// <summary>The full body for the list; an item gets a fresh unit and the RawMaterial type (AC-07.1).</summary>
    private static async Task<object> BodyAsync(HttpClient manager, string list, string name) => list switch
    {
        "items" => new { name, type = "RawMaterial", unitId = (await Seed.CreateUnitAsync(manager)).Id },
        _ => new { name },
    };
}
