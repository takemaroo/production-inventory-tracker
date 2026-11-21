using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance tests (phase 5) for the routed item from decision #58: every displayed percentage is
/// round(round(unrounded, 6), 2), both steps half away from zero (PRD 4.1, design 2.4 and 6.1), and the threshold uses
/// the unrounded value (AC-20.4). Earlier tests prove the rule in unit tests and on a percentage written straight to
/// the database; these drive it end to end, from a real send and completion to the order detail and the notification,
/// with figures where the two-step rule and a single rounding give different answers.
/// </summary>
/// <remarks>
/// The figures were found by searching for whole-number orders whose percentage lands just below a rounding edge
/// (a QA script, not product code). Each test starts its own app with an empty database, so the order is #1.
/// </remarks>
public class DisplayedPercentAcceptanceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-20.1, AC-20.2, AC-14.2, decision #58. Ordered 11,579 bags, 1 kg Cheese per bag; 11,590 kg sent (inside
    /// 11,347.42 to 11,810.58) and 11,590 bags produced with no leftover. Output variance is +11 bags, and
    /// 11 ÷ 11,579 × 100 = 0.0949995682...%: stored as 0.095000, so it shows as 0.10 (a single rounding would show 0.09).
    /// The order detail, the notification message and its data all show 0.10. Usage is exactly as required, so 0.
    /// </summary>
    [Fact]
    public async Task Decision_58_output_variance_percent_rounds_in_two_steps_end_to_end()
    {
        await using var app = new TestApiFactory();
        var scene = await SceneAsync(app, receive: 11590m);
        var number = await CreateOrderAsync(scene.Manager, scene.Product.Id, 11579m);
        Assert.Equal(1, number);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(scene.Warehouse, number, (scene.Cheese.Id, 11590m))).StatusCode);

        var response = await CompleteAsync(scene.Production, number, 11590m, null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ApiJson.ReadAsync(response);
        Assert.Equal(11m, order.GetProperty("outputVariance").GetDecimal());
        Assert.Equal(0.1m, order.GetProperty("outputVariancePercent").GetDecimal());
        Assert.Equal(0m, Line(order, scene.Cheese.Id).GetProperty("usageVariancePercent").GetDecimal());
        Assert.Equal(order.GetRawText(), (await ReadOrderAsync(scene.Manager, number)).GetRawText());

        var notification = Assert.Single(await NotificationsAsync(scene.Manager, number));
        Assert.Equal("OutputVariance", notification.GetProperty("type").GetString());
        Assert.Equal("Order #1: produced 11,590 Bag, ordered 11,579 Bag. Difference +11 Bag (+0.10%).",
            notification.GetProperty("message").GetString());
        Assert.Equal(0.1m, notification.GetProperty("data").GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// AC-20.3, AC-20.4, AC-23.2, decision #58. Ordered 19,601 bags at 1 kg per bag; 19,601 kg sent and a complementary
    /// order of 393 kg approved and sent; 19,601 bags produced, no leftover. Usage 19,994 kg against 19,601 kg required:
    /// +393 kg, and 393 ÷ 19,601 × 100 = 2.0049997449...%. Above the 2% tolerance on the unrounded value, so one
    /// UsageVariance notification; stored as 2.005000 and shown as 2.01 (a single rounding would show 2.00) in the line,
    /// the message and the data. Output equals the order, so no OutputVariance notification.
    /// </summary>
    [Fact]
    public async Task Decision_58_usage_variance_percent_rounds_in_two_steps_and_notifies_on_the_unrounded_value()
    {
        await using var app = new TestApiFactory();
        var scene = await SceneAsync(app, receive: 20000m);
        var number = await CreateOrderAsync(scene.Manager, scene.Product.Id, 19601m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(scene.Warehouse, number, (scene.Cheese.Id, 19601m))).StatusCode);
        var complementary = await ApprovedComplementaryAsync(scene.Production, scene.Manager, number, "Hopper spill",
            (scene.Cheese.Id, 393m));
        Assert.Equal(HttpStatusCode.OK, (await SendComplementaryAsync(scene.Warehouse, complementary, (scene.Cheese.Id, 393m))).StatusCode);

        var response = await CompleteAsync(scene.Production, number, 19601m, null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Line(await ApiJson.ReadAsync(response), scene.Cheese.Id);
        Assert.Equal(19994m, line.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(19601m, line.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(393m, line.GetProperty("usageVariance").GetDecimal());
        Assert.Equal(2.01m, line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.True(line.GetProperty("usageVarianceAboveTolerance").GetBoolean());

        var notifications = await NotificationsAsync(scene.Manager, number);
        var usage = Assert.Single(notifications, n => n.GetProperty("type").GetString() == "UsageVariance");
        Assert.DoesNotContain(notifications, n => n.GetProperty("type").GetString() == "OutputVariance");
        Assert.Equal("Order #1, Cheese: used 19,994 kg, required 19,601 kg for actual output. Difference +393 kg (+2.01%).",
            usage.GetProperty("message").GetString());
        Assert.Equal(2.01m, usage.GetProperty("data").GetProperty("percent").GetDecimal());
    }

    /// <summary>The people and master data both tests use: Cheese in kg, a product in Bag at 1 kg per bag.</summary>
    private static async Task<Scene> SceneAsync(TestApiFactory app, decimal receive)
    {
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, receive);
        return new Scene(manager, warehouse, production, cheese, product);
    }

    /// <summary>The notifications of one order, as a Manager reads them through <c>GET /notifications</c>.</summary>
    private static async Task<List<JsonElement>> NotificationsAsync(HttpClient manager, int number)
    {
        var response = await manager.GetAsync("/api/v1/notifications?pageSize=100", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("orderNumber").ValueKind == JsonValueKind.Number
                && n.GetProperty("orderNumber").GetInt32() == number
                && n.GetProperty("type").GetString() != "ComplementaryAwaitingApproval")
            .ToList();
    }

    private sealed record Scene(HttpClient Manager, HttpClient Warehouse, HttpClient Production, CreatedItem Cheese, CreatedItem Product);
}
