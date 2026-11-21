using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance tests (phase 5) for text length limits counted in characters (AC-06.1 "1 to 50 characters", AC-12.1
/// and every reason or note "1 to 500 characters", design 2.6 item names "1–100"). Found in the exploratory pass: the
/// API counted UTF-16 code units, so a character outside the Basic Multilingual Plane, such as an emoji, counted as two.
/// </summary>
/// <remarks>
/// BUG-001 (docs/bugs/BUG-001-text-limits-count-code-units.md) is fixed: limits now count Unicode code points (decision
/// #116), and these tests guard the fix. They fail again if any of these limits goes back to counting UTF-16 code
/// units. The cheese emoji U+1F9C0 is one Unicode code point and one character as a person sees it, so the expected
/// result does not depend on which of those two definitions is used.
/// </remarks>
public class TextLengthAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string Cheese = "\U0001F9C0";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>AC-06.1: a unit name of 26 characters (26 emoji) is within 1 to 50 characters and is accepted.</summary>
    [Fact]
    public async Task AC_06_1_BUG_001_unit_name_of_26_emoji_is_accepted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = string.Concat(Enumerable.Repeat(Cheese, 26)) + Guid.NewGuid().ToString("N")[..4];

        var response = await manager.PostAsJsonAsync("/api/v1/units", new { name }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>AC-12.1: an adjustment reason of 260 characters (260 emoji) is within 1 to 500 characters and is accepted.</summary>
    [Fact]
    public async Task AC_12_1_BUG_001_reason_of_260_emoji_is_accepted()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await ReceiveAsync(manager, item.Id, 10m);

        var response = await manager.PostAsJsonAsync("/api/v1/adjustments", new
        {
            itemId = item.Id, location = "Warehouse", quantity = -1m, reason = string.Concat(Enumerable.Repeat(Cheese, 260)),
        }, Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
