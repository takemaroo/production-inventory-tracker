using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance tests (phase 5) for the routed item from the T-008 review: an item with no movements shows stock
/// <c>0</c>, while a location whose movements add up to nothing shows <c>0.0</c>. Both are valid (design 2.4: clients
/// must not rely on trailing zeros, <c>1000</c> and <c>1000.000</c> mean the same). These tests pin what matters to a
/// client: both are JSON numbers equal to zero, neither is a negative zero or a string, and the movements still add up
/// to the stock shown (AC-10.1, AC-10.2, AC-11.4).
/// (v1.1, QA phase 5) The same for <c>openDemand</c> (AC-10.5, AC-31.1, design 5.5: "0, never null"), which the T-029
/// developer noted this class did not check: an item on no sales order, and an item whose sales orders have all been
/// shipped or cancelled, show open demand as a plain zero number, for every role (AC-31.4).
/// </summary>
public class StockZeroFormsAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A plain zero, with or without trailing zeros, and no sign: <c>0</c>, <c>0.0</c>, <c>0.000</c>.</summary>
    private static readonly Regex PlainZero = new(@"^0(\.0+)?$");

    /// <summary>
    /// AC-10.2: an item with no movements shows 0 in both locations, and so do its leftover figures; each is a JSON
    /// number written as a plain zero (exploratory run, 2026-11-21: written <c>0</c>).
    /// </summary>
    [Fact]
    public async Task AC_10_2_item_without_movements_shows_a_plain_zero_number()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);

        var stock = await StockAsync(manager, item.Id);

        foreach (var field in new[] { "warehouse", "productionStorage", "committedToOrder", "pendingReturns", "freeLeftover" })
        {
            AssertPlainZero(stock, field);
        }
    }

    /// <summary>
    /// AC-10.1, AC-11.4 and design 2.4: Production storage gets +10.5 then −10.5 (adjustments), and the Warehouse gets a
    /// receipt of 1.25 then an adjustment of −1.25. Both locations show zero as a plain number (exploratory run: written
    /// <c>0.0</c>, which design 2.4 allows), the single-item and list rows agree, and the movements listed for the item
    /// add up to zero in each location.
    /// </summary>
    [Fact]
    public async Task AC_10_1_location_whose_movements_cancel_out_shows_a_plain_zero_number()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var item = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        await AdjustAsync(manager, item.Id, "ProductionStorage", 10.5m);
        await AdjustAsync(manager, item.Id, "ProductionStorage", -10.5m);
        await ReceiveAsync(manager, item.Id, 1.25m);
        await AdjustAsync(manager, item.Id, "Warehouse", -1.25m);

        var stock = await StockAsync(manager, item.Id);

        AssertPlainZero(stock, "warehouse");
        AssertPlainZero(stock, "productionStorage");
        AssertPlainZero(stock, "freeLeftover");

        var list = await ApiJson.ReadAsync(await manager.GetAsync("/api/v1/stock?pageSize=100", Cancel));
        var row = list.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("itemId").GetInt32() == item.Id);
        Assert.Equal(stock.GetRawText(), row.GetRawText());

        var movements = (await ApiJson.ReadAsync(await manager.GetAsync($"/api/v1/movements?itemId={item.Id}", Cancel)))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, movements.Count);
        Assert.Equal(0m, movements.Sum(m => Change(m, "warehouseChange")));
        Assert.Equal(0m, movements.Sum(m => Change(m, "productionStorageChange")));
    }

    /// <summary>
    /// (v1.1) AC-10.5, AC-31.1, AC-31.4: a raw material and a finished product that are on no sales order show
    /// <c>openDemand</c> as a plain zero JSON number (design 5.5: <c>0</c>, never <c>null</c>, for both item types), the
    /// same to a Manager, a Production-only user and a Seller-only user, in the single-item read and in the list row.
    /// </summary>
    [Fact]
    public async Task AC_31_1_item_on_no_sales_order_shows_open_demand_as_a_plain_zero_number()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var production = await factory.CreateClientAsAsync(Roles.Production);
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var material = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);

        foreach (var client in new[] { manager, production, seller })
        {
            foreach (var itemId in new[] { material.Id, product.Id })
            {
                var stock = await StockAsync(client, itemId);
                AssertPlainZero(stock, "openDemand");
                Assert.Equal(stock.GetRawText(), (await StockRowAsync(client, itemId)).GetRawText());
            }
        }
    }

    /// <summary>
    /// (v1.1) AC-31.1, AC-31.2, AC-27.1: open demand goes back to a plain zero once every sales order for the item has
    /// left the Created status. A finished product with 10.5 in the Warehouse is on sales order A (0.25) and sales order
    /// B (10.25): open demand 10.5. A is shipped (Warehouse 10.25, open demand 10.25), then B is cancelled. Open demand
    /// is then a JSON number equal to zero, written as a plain zero (not <c>null</c>, not <c>-0</c>), for the Manager,
    /// Production and the Seller alike, and the list row equals the single read. The Warehouse keeps 10.25: cancelling
    /// moves no stock.
    /// </summary>
    [Fact]
    public async Task AC_31_2_open_demand_returns_to_a_plain_zero_after_its_sales_orders_ship_and_cancel()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var production = await factory.CreateClientAsAsync(Roles.Production);
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var product = await ProductAsync(manager, warehouse: 10.5m);
        var customer = await CreateCustomerAsync(seller);
        var first = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 0.25m));
        var second = await CreateSalesOrderAsync(seller, customer.Id, (product.Id, 10.25m));
        Assert.Equal(10.5m, await OpenDemandAsync(production, product.Id));

        Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, first)).StatusCode);
        Assert.Equal(10.25m, await OpenDemandAsync(seller, product.Id));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(seller, second)).StatusCode);

        foreach (var client in new[] { manager, production, seller })
        {
            var stock = await StockAsync(client, product.Id);
            AssertPlainZero(stock, "openDemand");
            Assert.Equal(10.25m, stock.GetProperty("warehouse").GetDecimal());
            Assert.Equal(stock.GetRawText(), (await StockRowAsync(client, product.Id)).GetRawText());
        }
    }

    /// <summary>The item's row in <c>GET /stock</c>, searching every page (the shared factory holds many items).</summary>
    private static async Task<JsonElement> StockRowAsync(HttpClient client, int itemId)
    {
        for (var page = 1; ; page++)
        {
            var body = await ApiJson.ReadAsync(await client.GetAsync($"/api/v1/stock?pageSize=100&page={page}", Cancel));
            var rows = body.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            var row = rows.FirstOrDefault(r => r.GetProperty("itemId").GetInt32() == itemId);
            if (row.ValueKind != JsonValueKind.Undefined)
            {
                return row;
            }
        }
    }

    private static void AssertPlainZero(JsonElement body, string field)
    {
        var value = body.GetProperty(field);
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(0m, value.GetDecimal());
        Assert.Matches(PlainZero, value.GetRawText());
    }

    private static decimal Change(JsonElement movement, string field) =>
        movement.GetProperty(field).ValueKind == JsonValueKind.Null ? 0m : movement.GetProperty(field).GetDecimal();

    private static async Task AdjustAsync(HttpClient manager, int itemId, string location, decimal quantity)
    {
        var response = await manager.PostAsJsonAsync("/api/v1/adjustments",
            new { itemId, location, quantity, reason = "Count correction" }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
