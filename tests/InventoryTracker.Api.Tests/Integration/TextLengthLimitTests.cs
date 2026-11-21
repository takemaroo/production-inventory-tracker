using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// BUG-001, decision #116: through the HTTP API, every kind of text limit counts emoji as one character, right at the
/// limit. Text of exactly the limit in emoji is accepted; one emoji more is refused with the unchanged design 7.2 message.
/// </summary>
/// <remarks>
/// Each emoji is two UTF-16 code units, so every "accepted" case here is about twice the limit in code units and was
/// refused before the fix. The "refused" cases prove the limit is still enforced. Tests in this class share one
/// database, so names carry a unique suffix.
/// </remarks>
public class TextLengthLimitTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string Cheese = "\U0001F9C0";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary><paramref name="count"/> cheese emoji: <paramref name="count"/> characters, twice as many code units.</summary>
    private static string Emoji(int count) => string.Concat(Enumerable.Repeat(Cheese, count));

    /// <summary>Four unique letters, so names in the shared database never collide.</summary>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..4];

    /// <summary>AC-06.1: a unit name of 50 characters (46 emoji and 4 letters) is accepted; 51 is refused.</summary>
    [Fact]
    public async Task Unit_name_of_50_characters_with_emoji_is_accepted_and_51_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var accepted = await manager.PostAsJsonAsync("/api/v1/units", new { name = Emoji(46) + Suffix() }, Cancel);
        var refused = await manager.PostAsJsonAsync("/api/v1/units", new { name = Emoji(47) + Suffix() }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        await ApiAssert.ValidationAsync(refused, "name", "Name must be 50 characters or fewer.");
    }

    /// <summary>Design 2.6: an item name of 100 characters and a description of 500, in emoji, are accepted; one more is refused.</summary>
    [Fact]
    public async Task Item_name_and_description_at_the_limit_in_emoji_are_accepted_and_one_more_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);

        var accepted = await manager.PostAsJsonAsync("/api/v1/items", new
        {
            name = Emoji(96) + Suffix(), description = Emoji(500), type = "RawMaterial", unitId = unit.Id,
        }, Cancel);
        var longName = await manager.PostAsJsonAsync("/api/v1/items", new
        {
            name = Emoji(97) + Suffix(), type = "RawMaterial", unitId = unit.Id,
        }, Cancel);
        var longDescription = await manager.PostAsJsonAsync("/api/v1/items", new
        {
            name = Seed.UniqueName("item"), description = Emoji(501), type = "RawMaterial", unitId = unit.Id,
        }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(Emoji(500), (await ApiJson.ReadAsync(accepted)).GetProperty("description").GetString());
        await ApiAssert.ValidationAsync(longName, "name", "Name must be 100 characters or fewer.");
        await ApiAssert.ValidationAsync(longDescription, "description", "Description must be 500 characters or fewer.");
    }

    /// <summary>AC-09.1: a receipt note of 500 emoji is accepted (and saved by the stock ledger); 501 is refused.</summary>
    [Fact]
    public async Task Receipt_note_of_500_emoji_is_accepted_and_501_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var accepted = await manager.PostAsJsonAsync("/api/v1/receipts",
            new { itemId = item.Id, quantity = 1m, receiptDate = "2026-11-01", note = Emoji(500) }, Cancel);
        var refused = await manager.PostAsJsonAsync("/api/v1/receipts",
            new { itemId = item.Id, quantity = 1m, receiptDate = "2026-11-01", note = Emoji(501) }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        await ApiAssert.ValidationAsync(refused, "note", "Note must be 500 characters or fewer.");
    }

    /// <summary>
    /// AC-12.1: an adjustment reason of exactly 500 emoji is accepted. The stock ledger re-checks the length before it
    /// saves the movement, so this also proves the ledger counts the same way as the service. 501 is refused.
    /// </summary>
    [Fact]
    public async Task Adjustment_reason_of_500_emoji_is_accepted_and_501_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await ReceiveAsync(manager, item.Id, 10m);

        var accepted = await manager.PostAsJsonAsync("/api/v1/adjustments",
            new { itemId = item.Id, location = "Warehouse", quantity = -1m, reason = Emoji(500) }, Cancel);
        var refused = await manager.PostAsJsonAsync("/api/v1/adjustments",
            new { itemId = item.Id, location = "Warehouse", quantity = -1m, reason = Emoji(501) }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        await ApiAssert.ValidationAsync(refused, "reason", "Reason must be 500 characters or fewer.");
    }

    /// <summary>
    /// Design 2.6, decision #66: an email of 256 characters with emoji before the <c>@</c> is accepted; 257 is refused.
    /// </summary>
    [Fact]
    public async Task Email_of_256_characters_with_emoji_is_accepted_and_257_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        // Seed.UniqueEmail("e") is 49 characters ("e-", 32 hex digits, "@inventory.test"), so 207 emoji make 256.
        var email256 = Emoji(207) + Seed.UniqueEmail("e");
        var email257 = Emoji(208) + Seed.UniqueEmail("e");

        var accepted = await manager.PostAsJsonAsync("/api/v1/users",
            new { email = email256, password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel);
        var refused = await manager.PostAsJsonAsync("/api/v1/users",
            new { email = email257, password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(email256, (await ApiJson.ReadAsync(accepted)).GetProperty("email").GetString());
        await ApiAssert.ValidationAsync(refused, "email", "Email must be 256 characters or fewer.");
    }

    // ---- Order-flow reasons and notes. Each needs an order InProduction, and only one can be InProduction per
    // database (AC-15.10), so these tests start their own TestApiFactory. -------------------------------------------

    /// <summary>
    /// AC-16.1: a complementary order reason of 500 emoji is accepted and stored as sent; 501 is refused.
    /// </summary>
    [Fact]
    public async Task Complementary_request_reason_of_500_emoji_is_accepted_and_501_is_refused()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);

        var refused = await RequestComplementaryAsync(scene.Manager, scene.Number, Emoji(501), (scene.Cheese.Id, 20m));
        var accepted = await RequestComplementaryAsync(scene.Manager, scene.Number, Emoji(500), (scene.Cheese.Id, 20m));

        await ApiAssert.ValidationAsync(refused, "reason", "Reason must be 500 characters or fewer.");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var number = (await ApiJson.ReadAsync(accepted)).GetProperty("number").GetInt32();
        Assert.Equal(Emoji(500), (await ReadComplementaryAsync(scene.Manager, number)).GetProperty("reason").GetString());
    }

    /// <summary>
    /// AC-17.2: a rejection note of 500 emoji is accepted and stored as the decision note; 501 is refused (and the request
    /// stays pending, which is why it is sent first).
    /// </summary>
    [Fact]
    public async Task Rejection_note_of_500_emoji_is_accepted_and_501_is_refused()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);
        var number = await RequestedComplementaryAsync(scene.Manager, scene.Number, "Hopper spill", (scene.Cheese.Id, 20m));

        var refused = await RejectAsync(scene.Manager, number, Emoji(501));
        var accepted = await RejectAsync(scene.Manager, number, Emoji(500));

        await ApiAssert.ValidationAsync(refused, "note", "Note must be 500 characters or fewer.");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Emoji(500), (await ReadComplementaryAsync(scene.Manager, number)).GetProperty("decisionNote").GetString());
    }

    /// <summary>
    /// AC-19.1: a completion reason of 500 emoji is accepted and stored; 501 is refused. A leftover above 0 makes the
    /// reason required (AC-19.4), so the check cannot be skipped as "no reason given".
    /// </summary>
    [Fact]
    public async Task Completion_reason_of_500_emoji_is_accepted_and_501_is_refused()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);

        var refused = await CompleteAsync(scene.Manager, scene.Number, 1000m, Emoji(501), (scene.Cheese.Id, 5m));
        var accepted = await CompleteAsync(scene.Manager, scene.Number, 1000m, Emoji(500), (scene.Cheese.Id, 5m));

        await ApiAssert.ValidationAsync(refused, "reason", "Reason must be 500 characters or fewer.");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Emoji(500), (await ReadOrderAsync(scene.Manager, scene.Number)).GetProperty("completionReason").GetString());
    }

    /// <summary>AC-21.1: a cancellation reason of 500 emoji is accepted and stored; 501 is refused and the order stays open.</summary>
    [Fact]
    public async Task Cancellation_reason_of_500_emoji_is_accepted_and_501_is_refused()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var number = await CreateOrderAsync(manager, product.Id, 10m);

        var refused = await CancelAsync(manager, number, Emoji(501));
        var accepted = await CancelAsync(manager, number, Emoji(500));

        await ApiAssert.ValidationAsync(refused, "reason", "Reason must be 500 characters or fewer.");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Emoji(500), (await ReadOrderAsync(manager, number)).GetProperty("cancellationReason").GetString());
    }

    /// <summary>
    /// AC-22.1: a return reason of 500 emoji is accepted and stored; 501 is refused. The order is completed with 5 of
    /// leftover first, so there is free leftover to return (AC-22.2).
    /// </summary>
    [Fact]
    public async Task Return_reason_of_500_emoji_is_accepted_and_501_is_refused()
    {
        await using var app = new TestApiFactory();
        var scene = await RaceSceneAsync(app, send: true);
        Assert.Equal(HttpStatusCode.OK,
            (await CompleteAsync(scene.Manager, scene.Number, 1000m, "Line jammed", (scene.Cheese.Id, 5m))).StatusCode);

        var refused = await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 1m, Emoji(501));
        var accepted = await InstructReturnAsync(scene.Manager, scene.Cheese.Id, 1m, Emoji(500));

        await ApiAssert.ValidationAsync(refused, "reason", "Reason must be 500 characters or fewer.");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var number = (await ApiJson.ReadAsync(accepted)).GetProperty("number").GetInt32();
        Assert.Equal(Emoji(500), (await ReadReturnAsync(scene.Manager, number)).GetProperty("reason").GetString());
    }
}
