using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// (v1.1) QA acceptance tests (phase 5) for a gap in the race tests: a Manager changing or deleting an item at the same
/// moment as a Seller puts it on a new sales order. AC-28.5 makes an item on any sales order line "in use", so its type
/// and unit cannot change and it cannot be deleted (AC-07.3, AC-07.4); PRD section 5 says a request either completes
/// fully or saves nothing. The developer tests race a customer delete against a new sales order (AC-29.8), but not the
/// item. Each ordering is forced with <see cref="ForcedOrder"/>, held at the first read of <c>Items</c> inside the write
/// transaction, so both "Manager first" and "Seller first" are proved on every run. An exploratory run on the real app
/// (2026-11-25, 8 and 20 unforced tries each) gave the same outcomes.
/// </summary>
public class SalesOrderItemRaceAcceptanceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-28.5, AC-07.4, AC-24.3, PRD section 5: an item delete and a new sales order for that item at the same time.
    /// Delete first: <c>204</c>, and the sales order, which waited for the write lock, is refused with <c>400</c>
    /// "Item {id} does not exist." under <c>lines[0].itemId</c>; no sales order is saved. Sales order first: <c>201</c>,
    /// and the delete is refused with <c>409</c> <c>item-in-use</c>; the item is still there. Either way no sales order
    /// line names an item that does not exist.
    /// </summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("create")]
    public async Task AC_28_5_item_delete_racing_a_new_sales_order_for_it_lets_exactly_one_succeed(string first)
    {
        await using var race = new ForcedOrder("Items");
        var manager = await race.App.CreateClientAsAsync(Roles.Manager);
        var seller = await race.App.CreateClientAsAsync(Roles.Seller);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var customer = await CreateCustomerAsync(seller);

        Task<HttpResponseMessage> Delete() => manager.DeleteAsync($"/api/v1/items/{item.Id}", Cancel);
        Task<HttpResponseMessage> Create() => PostSalesOrderAsync(seller, customer.Id, (item.Id, 5m));

        if (first == "delete")
        {
            var (delete, create) = await race.RunAsync(Delete, Create);

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            await ApiAssert.ValidationAsync(create, "lines[0].itemId", $"Item {item.Id} does not exist.");
            Assert.Equal(0, await CountAsync(race.App, db => db.SalesOrders.CountAsync(Cancel)));
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/items/{item.Id}", Cancel)).StatusCode);
        }
        else
        {
            var (create, delete) = await race.RunAsync(Create, Delete);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            await ApiAssert.ProblemAsync(delete, HttpStatusCode.Conflict, "item-in-use",
                $"{item.Name} has movements, recipe lines, order lines or sales order lines and cannot be deleted.");
            Assert.Equal(1, await CountAsync(race.App, db => db.SalesOrders.CountAsync(Cancel)));
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/v1/items/{item.Id}", Cancel)).StatusCode);
        }

        Assert.Equal(0, await CountAsync(race.App,
            db => db.SalesOrderLines.CountAsync(line => !db.Items.Any(i => i.Id == line.ItemId), Cancel)));
    }

    /// <summary>
    /// AC-28.5, AC-07.3, AC-24.1, PRD section 5: a change of an item's type and unit and a new sales order for that item
    /// at the same time. Change first: <c>200</c>, the item is a RawMaterial in its new unit, and the sales order is then
    /// created with <c>201</c> and its line already shows RawMaterial and the new unit (a sales order line may be any item
    /// type, decision #144). Sales order first: <c>201</c> with FinishedProduct and the old unit, and the change is refused
    /// with <c>409</c> "Type and unit cannot change once the item is in use."; the item keeps its type and unit. Either
    /// way the item's type and unit never change after a sales order line names it.
    /// </summary>
    [Theory]
    [InlineData("change")]
    [InlineData("create")]
    public async Task AC_28_5_type_change_racing_a_new_sales_order_never_changes_an_item_in_use(string first)
    {
        await using var race = new ForcedOrder("Items");
        var manager = await race.App.CreateClientAsAsync(Roles.Manager);
        var seller = await race.App.CreateClientAsAsync(Roles.Seller);
        var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
        var newUnit = await Seed.CreateUnitAsync(manager);
        var customer = await CreateCustomerAsync(seller);

        Task<HttpResponseMessage> Change() => manager.PutAsJsonAsync($"/api/v1/items/{item.Id}",
            new { name = item.Name, type = "RawMaterial", unitId = newUnit.Id }, Cancel);
        Task<HttpResponseMessage> Create() => PostSalesOrderAsync(seller, customer.Id, (item.Id, 5m));

        if (first == "change")
        {
            var (change, create) = await race.RunAsync(Change, Create);

            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var line = (await ApiJson.ReadAsync(create)).GetProperty("lines")[0];
            Assert.Equal("RawMaterial", line.GetProperty("itemType").GetString());
            Assert.Equal(newUnit.Name, line.GetProperty("unit").GetString());
        }
        else
        {
            var (create, change) = await race.RunAsync(Create, Change);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var line = (await ApiJson.ReadAsync(create)).GetProperty("lines")[0];
            Assert.Equal("FinishedProduct", line.GetProperty("itemType").GetString());
            Assert.Equal(item.UnitName, line.GetProperty("unit").GetString());
            await ApiAssert.ProblemAsync(change, HttpStatusCode.Conflict, "item-in-use",
                "Type and unit cannot change once the item is in use.");
            var stored = await ApiJson.ReadAsync(await manager.GetAsync($"/api/v1/items/{item.Id}", Cancel));
            Assert.Equal("FinishedProduct", stored.GetProperty("type").GetString());
            Assert.Equal(item.UnitId, stored.GetProperty("unitId").GetInt32());
        }

        // Whatever the order, the item is now in use: a later change of type is refused (AC-28.5).
        var later = await manager.PutAsJsonAsync($"/api/v1/items/{item.Id}",
            new { name = item.Name, type = first == "change" ? "FinishedProduct" : "RawMaterial", unitId = item.UnitId }, Cancel);
        await ApiAssert.ProblemAsync(later, HttpStatusCode.Conflict, "item-in-use",
            "Type and unit cannot change once the item is in use.");
    }

    /// <summary>Runs a count on a fresh <see cref="AppDbContext"/> of <paramref name="app"/>, so it sees only committed rows.</summary>
    private static async Task<int> CountAsync(TestApiFactory app, Func<AppDbContext, Task<int>> count)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await count(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
