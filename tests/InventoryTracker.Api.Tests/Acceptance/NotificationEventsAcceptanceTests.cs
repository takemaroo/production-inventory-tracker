using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance test (phase 5) for AC-23.2: "There are exactly five types, created ONLY by these events." Earlier tests
/// prove each of the five events creates its notification, and a few check that one nearby action creates none. This one
/// walks through every other data-changing endpoint a real day would use and checks that none of them creates a
/// notification, starting from an empty database.
/// </summary>
public class NotificationEventsAcceptanceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-23.2 (and AC-20.4 at exactly t, AC-20.2 at zero, AC-19.7 with no leftover, AC-21.3 not applying to a Created
    /// order). Through the API: unit and item create, rename and delete; a recipe; a receipt; three adjustments; two
    /// orders, one cancelled while Created with free leftover in production storage; a return received and one
    /// cancelled; the send; user create, role change, deactivate, reactivate, password reset and an own password
    /// change. No notification exists. Then two complementary requests give exactly two ComplementaryAwaitingApproval
    /// notifications (one each, AC-16.1), and approving, sending and rejecting add none. Finally the order is completed
    /// with output equal to the order, no leftover, and usage exactly 2.00% over (1,020 kg against 1,000 kg): none of
    /// the three completion notifications is due, so the total stays at two. Marking them read adds none either.
    /// </summary>
    [Fact]
    public async Task AC_23_2_notifications_are_created_only_by_the_five_events()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(app, Roles.Warehouse);
        var (production, _) = await SignedInAsync(app, Roles.Production);

        // Master data: create, change and delete (US-06 to US-08).
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var box = await Seed.CreateUnitAsync(manager, "Box");
        await ExpectAsync(HttpStatusCode.OK, manager.PutAsJsonAsync($"/api/v1/units/{box.Id}", new { name = "Crate" }, Cancel));
        await ExpectAsync(HttpStatusCode.NoContent, manager.DeleteAsync($"/api/v1/units/{box.Id}", Cancel));
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
        await ExpectAsync(HttpStatusCode.OK, manager.PutAsJsonAsync($"/api/v1/items/{cheese.Id}",
            new { name = "Cheese", description = "Mozzarella", type = "RawMaterial", unitId = kg.Id }, Cancel));
        var spare = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
        await ExpectAsync(HttpStatusCode.NoContent, manager.DeleteAsync($"/api/v1/items/{spare.Id}", Cancel));
        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1m));

        // Stock: a receipt and adjustments in both locations (US-09, US-12). Production storage ends at 8 kg.
        await ReceiveAsync(warehouse, cheese.Id, 2000m);
        await AdjustAsync(manager, cheese.Id, "Warehouse", 5m);
        await AdjustAsync(manager, cheese.Id, "ProductionStorage", 10m);
        await AdjustAsync(manager, cheese.Id, "ProductionStorage", -2m);

        // Orders: create two, cancel the newer while Created, with free leftover in production storage (US-13, US-21).
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        var other = await CreateOrderAsync(manager, product.Id, 10m);
        await ExpectAsync(HttpStatusCode.OK, CancelAsync(manager, other, "Not needed"));

        // Returns: one received, one cancelled (US-22). Production storage ends at 5 kg.
        var received = await InstructedReturnAsync(manager, cheese.Id, 3m);
        await ExpectAsync(HttpStatusCode.OK, ReceiveReturnAsync(warehouse, received));
        var cancelled = await InstructedReturnAsync(manager, cheese.Id, 2m);
        await ExpectAsync(HttpStatusCode.OK, CancelReturnAsync(manager, cancelled));

        // The send: leftover applied 5 kg, nominal 995 kg (US-15).
        await ExpectAsync(HttpStatusCode.OK, SendAsync(warehouse, number, (cheese.Id, 995m)));

        // Users (US-03, US-04).
        var created = await ApiJson.ReadAsync(await ExpectAsync(HttpStatusCode.Created, manager.PostAsJsonAsync("/api/v1/users",
            new { email = Seed.UniqueEmail("qa"), password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel)));
        var userId = created.GetProperty("id").GetString();
        await ExpectAsync(HttpStatusCode.OK, manager.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = new[] { "Production" } }, Cancel));
        await ExpectAsync(HttpStatusCode.OK, manager.PostAsync($"/api/v1/users/{userId}/deactivate", content: null, Cancel));
        await ExpectAsync(HttpStatusCode.OK, manager.PostAsync($"/api/v1/users/{userId}/reactivate", content: null, Cancel));
        await ExpectAsync(HttpStatusCode.NoContent, manager.PutAsJsonAsync($"/api/v1/users/{userId}/password", new { newPassword = "Reset-2026a" }, Cancel));
        using var self = app.CreateClientWithToken(await app.SignInAsync(created.GetProperty("email").GetString()!, "Reset-2026a"));
        await ExpectAsync(HttpStatusCode.NoContent, self.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = "Reset-2026a", newPassword = "Changed-2026b" }, Cancel));

        Assert.Empty(await NotificationTypesAsync(manager));

        // Complementary orders (US-16 to US-18): each request is one event; approve, send and reject are not.
        var first = await ApprovedComplementaryAsync(production, manager, number, "Hopper spill", (cheese.Id, 20m));
        await ExpectAsync(HttpStatusCode.OK, SendComplementaryAsync(warehouse, first, (cheese.Id, 20m)));
        var second = await RequestedComplementaryAsync(production, number, "Spare", (cheese.Id, 5m));
        await ExpectAsync(HttpStatusCode.OK, RejectAsync(manager, second, "Not needed"));

        Assert.Equal(["ComplementaryAwaitingApproval", "ComplementaryAwaitingApproval"], await NotificationTypesAsync(manager));

        // Completion with nothing to report: 1,000 bags as ordered, no leftover, usage 5 + 995 + 20 = 1,020 kg against
        // 1,000 kg required, exactly +2.00%, which is not above t (AC-20.4).
        var completed = await ExpectAsync(HttpStatusCode.OK, CompleteAsync(production, number, 1000m, null, (cheese.Id, 0m)));
        var line = Line(await ApiJson.ReadAsync(completed), cheese.Id);
        Assert.Equal(2m, line.GetProperty("usageVariancePercent").GetDecimal());
        Assert.False(line.GetProperty("usageVarianceAboveTolerance").GetBoolean());

        await ExpectAsync(HttpStatusCode.NoContent, manager.PostAsync("/api/v1/notifications/read-all", content: null, Cancel));
        Assert.Equal(["ComplementaryAwaitingApproval", "ComplementaryAwaitingApproval"], await NotificationTypesAsync(manager));
    }

    /// <summary>The type of every notification, newest first, as a Manager lists them.</summary>
    private static async Task<List<string?>> NotificationTypesAsync(HttpClient manager)
    {
        var page = await ApiJson.ReadAsync(await ExpectAsync(HttpStatusCode.OK, manager.GetAsync("/api/v1/notifications?pageSize=100", Cancel)));
        return page.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("type").GetString()).ToList();
    }

    private static Task AdjustAsync(HttpClient manager, int itemId, string location, decimal quantity) =>
        ExpectAsync(HttpStatusCode.Created, manager.PostAsJsonAsync("/api/v1/adjustments",
            new { itemId, location, quantity, reason = "Count correction" }, Cancel));

    /// <summary>Awaits a request and checks its status, so a broken step fails at its own line.</summary>
    private static async Task<HttpResponseMessage> ExpectAsync(HttpStatusCode status, Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.Equal(status, response.StatusCode);
        return response;
    }
}
