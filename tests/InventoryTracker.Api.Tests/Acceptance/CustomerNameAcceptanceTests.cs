using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// (v1.1) QA acceptance test (phase 5) for BUG-002: a customer name is unique "without regard to case" (AC-29.2) so that
/// "every sales order names its customer the same way and sales can be totalled per customer" (US-29). The same
/// accented name can arrive in two Unicode forms that look identical: an accented "e" as one character (U+00E9, what a Windows
/// keyboard types) or as "e" followed by a combining accent (U+0301, common in text pasted from macOS or a PDF). Before
/// the fix both were accepted as two customers, and the per-customer report showed two "Cafe Sur" rows.
/// </summary>
/// <remarks>
/// BUG-002 was fixed by converting names to Unicode NFC before they are checked and stored (decision #170) and closed
/// by QA on 2026-11-25. Before the fix this test failed with <c>201</c> instead of <c>409</c>. The accented letters are
/// written as <c>\u</c> escapes so an editor cannot silently turn one form into the other. The wider rule (units and
/// items too, invisible-only names, U+FFFE, rename) is covered by <c>Integration/NameUnicodeTests</c>.
/// </remarks>
public class CustomerNameAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-29.2, BUG-002: "Cafe Sur" typed with a precomposed accented "e" (U+00E9) is created; the same name typed with "e" plus a
    /// combining acute accent is refused with <c>409</c> <c>customer-name-taken</c>, as an upper-case copy already is.
    /// </summary>
    [Fact]
    public async Task AC_29_2_BUG_002_same_name_in_another_unicode_form_is_a_duplicate()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var precomposed = $"Caf\u00E9 Sur {suffix}";  // U+00E9 as one code point (NFC)
        var decomposed = $"Cafe\u0301 Sur {suffix}"; // "e" + COMBINING ACUTE ACCENT (NFD)

        var first = await seller.PostAsJsonAsync("/api/v1/customers", new { name = precomposed }, Cancel);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await seller.PostAsJsonAsync("/api/v1/customers", new { name = decomposed }, Cancel);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("customer-name-taken", (await ApiJson.ReadAsync(second)).GetProperty("code").GetString());
    }
}
