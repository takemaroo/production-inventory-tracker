using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance test (phase 5) for the last sentence of AC-03.6: "Users are never deleted, so their name stays on
/// every movement and order they touched." UsersTests proves that <c>DELETE /users/{id}</c> is refused; this proves the
/// outcome a Manager auditing the history cares about, after the user is deactivated.
/// </summary>
public class UserRecordsAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-03.6, AC-05.3: a Warehouse user records a receipt and sends an order; a Manager then deactivates them. Their id
    /// and email still show on the Received and SentToProduction movements (list and single read) and on the order's
    /// InProduction history entry, exactly as before the deactivation.
    /// </summary>
    [Fact]
    public async Task AC_03_6_deactivated_users_still_show_on_the_movements_and_orders_they_touched()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (warehouse, warehouseUser) = await SignedInAsync(factory, Roles.Warehouse);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(warehouse, cheese.Id, 50m);
        var number = await CreateOrderAsync(manager, product.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 10m))).StatusCode);
        var movementsBefore = await MovementsAsync(manager, cheese.Id);
        var orderBefore = await ReadOrderAsync(manager, number);

        var deactivate = await manager.PostAsync($"/api/v1/users/{warehouseUser.Id}/deactivate", content: null, Cancel);

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False((await ApiJson.ReadAsync(deactivate)).GetProperty("isActive").GetBoolean());

        var movements = await MovementsAsync(manager, cheese.Id);
        Assert.Equal(movementsBefore.GetRawText(), movements.GetRawText());
        var rows = movements.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["SentToProduction", "Received"], rows.Select(row => row.GetProperty("type").GetString()));
        foreach (var row in rows)
        {
            AssertPerson(row.GetProperty("user"), warehouseUser);
            var single = await ApiJson.ReadAsync(await manager.GetAsync($"/api/v1/movements/{row.GetProperty("id").GetInt32()}", Cancel));
            AssertPerson(single.GetProperty("user"), warehouseUser);
        }

        var order = await ReadOrderAsync(manager, number);
        Assert.Equal(orderBefore.GetRawText(), order.GetRawText());
        var sent = order.GetProperty("history").EnumerateArray().Single(entry => entry.GetProperty("status").GetString() == "InProduction");
        AssertPerson(sent.GetProperty("by"), warehouseUser);
    }

    private static void AssertPerson(JsonElement person, AppUser user)
    {
        Assert.Equal(user.Id, person.GetProperty("id").GetString());
        Assert.Equal(user.Email, person.GetProperty("email").GetString());
    }

    private static async Task<JsonElement> MovementsAsync(HttpClient client, int itemId)
    {
        var response = await client.GetAsync($"/api/v1/movements?itemId={itemId}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }
}
