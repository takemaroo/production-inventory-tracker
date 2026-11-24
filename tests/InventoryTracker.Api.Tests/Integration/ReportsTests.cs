using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using InventoryTracker.Api.Tests.Integration.Scenarios;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-030: the shipped-totals reports, <c>GET /api/v1/reports/shipped-by-item</c> and
/// <c>GET /api/v1/reports/shipped-by-customer</c> (US-32 AC-32.1 to AC-32.6, US-33 AC-33.1 to AC-33.4, D-13, AC-29.3;
/// design 2.5, 3.16, 5.13 and 7.2 "Reports"; architecture 3.1 name order, 3.5 and 4.1 item 19; decisions #160, #161).
/// </summary>
/// <remarks>
/// <para>
/// Three kinds of database. The PRD's December example (AC-32.3, AC-33.3) is built once in <see cref="DecemberExample"/>,
/// with every shipment made through the real ship endpoint at <c>FakeTime</c> dates, and its tests only read it. Tests
/// that need exact names ("apple", "Émile") or exact times (23:59:59 on 31 December) build their own
/// <see cref="TestApiFactory"/>. The rest share one database (<see cref="IClassFixture{TFixture}"/>) and stay apart by
/// filtering each report on a customer or item they made themselves.
/// </para>
/// <para>
/// Quantities are compared as decimals, never as JSON text, because design 2.4 says clients must not rely on trailing
/// zeros (<c>1201</c> and <c>1201.000</c> mean the same).
/// </para>
/// </remarks>
public class ReportsTests(TestApiFactory factory, ReportsTests.DecemberExample december)
    : IClassFixture<TestApiFactory>, IClassFixture<ReportsTests.DecemberExample>
{
    private const string ShreddedName = "Shredded cheese 1 kg bag";
    private const string GratedName = "Grated cheese 200 g bag";
    private const string ByItem = "shipped-by-item";
    private const string ByCustomer = "shipped-by-customer";

    /// <summary>The AC-32.3 and AC-33.3 period: December 2026.</summary>
    private const string December = "from=2026-12-01&to=2026-12-31";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-32.3, AC-33.3: the PRD's December example -----------------------------------------------------------------

    /// <summary>
    /// AC-32.3 first bullet, AC-32.1, design 5.13: for December, "Grated cheese 200 g bag" 50 Bag on 1 sales order, then
    /// "Shredded cheese 1 kg bag" 1,201 Bag on 3 sales orders, in that order, in the standard page envelope with the design
    /// 5.13 row shape field for field. Shredded cheese was created first, so it has the lower id: the order is by name,
    /// not by id or creation time (PRD row 51).
    /// </summary>
    [Fact]
    public async Task December_by_item_gives_the_PRD_rows_A_to_Z()
    {
        var body = await ShippedByItemAsync(december.Seller, December);

        Assert.Equal(["items", "page", "pageSize", "totalCount"], body.EnumerateObject().Select(field => field.Name));
        Assert.Equal((1, 50, 2), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(),
            body.GetProperty("totalCount").GetInt32()));
        Assert.Equal([(GratedName, 50m, 1), (ShreddedName, 1201m, 3)], ItemRows(body));

        var grated = body.GetProperty("items")[0];
        Assert.Equal(
            ["itemId", "itemName", "itemType", "unit", "shippedQuantity", "salesOrderCount"],
            grated.EnumerateObject().Select(field => field.Name));
        Assert.Equal(december.Grated.Id, grated.GetProperty("itemId").GetInt32());
        Assert.Equal("FinishedProduct", grated.GetProperty("itemType").GetString());
        Assert.Equal("Bag", grated.GetProperty("unit").GetString());
        Assert.Equal(december.Shredded.Id, body.GetProperty("items")[1].GetProperty("itemId").GetInt32());
        Assert.True(december.Shredded.Id < december.Grated.Id);
    }

    /// <summary>
    /// AC-32.3 second and third bullets: with customer Deli Norte, only "Shredded cheese 1 kg bag" 701 Bag on 2 sales
    /// orders (#1 and #3; no Grated cheese row). From 2026-12-03 to 2026-12-31, Grated cheese 50 Bag on 1, then Shredded
    /// cheese 601 Bag on 2 (#2 and #3; #1 shipped on the 2nd).
    /// </summary>
    [Fact]
    public async Task December_by_item_for_Deli_Norte_and_from_the_3rd()
    {
        var deliNorte = await ShippedByItemAsync(december.Seller, $"{December}&customerId={december.DeliNorte.Id}");
        var fromThe3rd = await ShippedByItemAsync(december.Seller, "from=2026-12-03&to=2026-12-31");

        Assert.Equal([(ShreddedName, 701m, 2)], ItemRows(deliNorte));
        Assert.Equal(1, deliNorte.GetProperty("totalCount").GetInt32());
        Assert.Equal([(GratedName, 50m, 1), (ShreddedName, 601m, 2)], ItemRows(fromThe3rd));
    }

    /// <summary>
    /// AC-33.3, AC-33.1, design 5.13: for December, Cafe Sur with Grated cheese 50 Bag on 1, Cafe Sur with Shredded
    /// cheese 500 Bag on 1, then Deli Norte with Shredded cheese 701 Bag on 2, in that order (customer name, then item
    /// name). Each row has the design 5.13 shape, with the customer as <c>{ id, name, isActive }</c>. Deli Norte was
    /// created first, so again the order is by name, not id. With <c>itemId</c> of Grated cheese, only the Cafe Sur row.
    /// </summary>
    [Fact]
    public async Task December_by_customer_gives_the_PRD_rows_A_to_Z()
    {
        var body = await ShippedByCustomerAsync(december.Seller, December);
        var grated = await ShippedByCustomerAsync(december.Seller, $"{December}&itemId={december.Grated.Id}");

        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            [("Cafe Sur", GratedName, 50m, 1), ("Cafe Sur", ShreddedName, 500m, 1), ("Deli Norte", ShreddedName, 701m, 2)],
            CustomerRows(body));

        var first = body.GetProperty("items")[0];
        Assert.Equal(
            ["customer", "itemId", "itemName", "itemType", "unit", "shippedQuantity", "salesOrderCount"],
            first.EnumerateObject().Select(field => field.Name));
        var customer = first.GetProperty("customer");
        Assert.Equal(["id", "name", "isActive"], customer.EnumerateObject().Select(field => field.Name));
        Assert.Equal(december.CafeSur.Id, customer.GetProperty("id").GetInt32());
        Assert.True(customer.GetProperty("isActive").GetBoolean());
        Assert.Equal(december.Grated.Id, first.GetProperty("itemId").GetInt32());
        Assert.Equal("FinishedProduct", first.GetProperty("itemType").GetString());
        Assert.Equal("Bag", first.GetProperty("unit").GetString());
        Assert.Equal(december.DeliNorte.Id, body.GetProperty("items")[2].GetProperty("customer").GetProperty("id").GetInt32());
        Assert.True(december.DeliNorte.Id < december.CafeSur.Id);

        Assert.Equal([("Cafe Sur", GratedName, 50m, 1)], CustomerRows(grated));
    }

    /// <summary>
    /// AC-32.2, D-13: in the example, #4 (500 Bag for Deli Norte) is still Created and #7 (300 Bag for Deli Norte) is
    /// Cancelled, and neither counts in any period, all time included. All time is therefore December's figures: 1,201
    /// Bag on 3 sales orders, not 1,701 or 2,001, and Deli Norte's 701 on 2.
    /// </summary>
    [Fact]
    public async Task Created_and_cancelled_sales_orders_never_count()
    {
        var byItem = await ShippedByItemAsync(december.Seller);
        var byCustomer = await ShippedByCustomerAsync(december.Seller, $"customerId={december.DeliNorte.Id}");

        Assert.Equal([(GratedName, 50m, 1), (ShreddedName, 1201m, 3)], ItemRows(byItem));
        Assert.Equal([("Deli Norte", ShreddedName, 701m, 2)], CustomerRows(byCustomer));
    }

    /// <summary>
    /// AC-32.4, AC-33.2: for each item of the example and each period the ACs use (December, from the 3rd, the 2nd alone,
    /// the 5th alone, only <c>from</c>, only <c>to</c>, all time), the report's <c>shippedQuantity</c> (0 when the item
    /// has no row) equals minus the sum of <c>warehouseChange</c> over
    /// <c>GET /movements?type=ShippedToCustomer&amp;itemId=...&amp;from=...&amp;to=...</c>. The customer report's rows for
    /// the item add up to the same figure.
    /// </summary>
    [Theory]
    [InlineData("from=2026-12-01&to=2026-12-31")]
    [InlineData("from=2026-12-03&to=2026-12-31")]
    [InlineData("from=2026-12-02&to=2026-12-02")]
    [InlineData("from=2026-12-05&to=2026-12-05")]
    [InlineData("from=2026-12-04")]
    [InlineData("to=2026-12-04")]
    [InlineData("")]
    public async Task Shipped_quantity_equals_minus_the_shipment_movements(string period)
    {
        var byItem = await ShippedByItemAsync(december.Seller, period);
        var byCustomer = await ShippedByCustomerAsync(december.Seller, period);

        foreach (var item in new[] { december.Shredded, december.Grated })
        {
            var separator = period.Length == 0 ? "" : "&";
            var movements = await ScenarioApi.AllRowsAsync(december.Warehouse,
                $"{ScenarioApi.ApiPath}/movements?type=ShippedToCustomer&itemId={item.Id}{separator}{period}");
            var fromMovements = -movements.Sum(movement => movement.GetProperty("warehouseChange").GetDecimal());

            Assert.Equal(fromMovements, ShippedOf(byItem, item.Id));
            Assert.Equal(fromMovements, byCustomer.GetProperty("items").EnumerateArray()
                .Where(row => row.GetProperty("itemId").GetInt32() == item.Id)
                .Sum(row => row.GetProperty("shippedQuantity").GetDecimal()));
        }
    }

    /// <summary>
    /// AC-32.1, AC-33.1, design 2.5: paging. <c>pageSize=1&amp;page=2</c> of the item report is its second row (Shredded
    /// cheese), and <c>totalCount</c> is still the number of rows (2). <c>pageSize=2&amp;page=2</c> of the customer
    /// report is its third row (Deli Norte), with <c>totalCount</c> 3; a page past the end is <c>200</c> with no rows.
    /// </summary>
    [Fact]
    public async Task Reports_are_paged_with_the_total_row_count()
    {
        var secondItem = await ShippedByItemAsync(december.Seller, $"{December}&pageSize=1&page=2");
        var secondCustomerPage = await ShippedByCustomerAsync(december.Seller, $"{December}&pageSize=2&page=2");
        var pastTheEnd = await ShippedByCustomerAsync(december.Seller, $"{December}&pageSize=2&page=3");

        Assert.Equal([(ShreddedName, 1201m, 3)], ItemRows(secondItem));
        Assert.Equal((2, 1, 2), (secondItem.GetProperty("page").GetInt32(), secondItem.GetProperty("pageSize").GetInt32(),
            secondItem.GetProperty("totalCount").GetInt32()));
        Assert.Equal([("Deli Norte", ShreddedName, 701m, 2)], CustomerRows(secondCustomerPage));
        Assert.Equal(3, secondCustomerPage.GetProperty("totalCount").GetInt32());
        Assert.Empty(pastTheEnd.GetProperty("items").EnumerateArray());
        Assert.Equal(3, pastTheEnd.GetProperty("totalCount").GetInt32());
    }

    // ---- Name order (architecture 3.1, risk 16; design 2.5) ------------------------------------------------------------

    /// <summary>
    /// Design 2.5, architecture 3.1 and risk 16: "A to Z" is ordinal on the upper-cased name. Items and customers named
    /// "apple", "Banana", "Zeta" and "Émile", created in a scrambled order, come out as apple, Banana, Zeta, Émile in both
    /// reports, and in the customer report each customer's items follow the same order.
    /// </summary>
    /// <remarks>
    /// The four names catch both wrong orders. Ordering by the name as typed (case-sensitive) would put "apple" after
    /// "Zeta", because lower-case letters come after upper-case ones; so letter case would change the order. A
    /// language-aware order (PostgreSQL's default collation, or .NET's culture comparison) would put "Émile" next to E,
    /// before "Zeta". Only the ordinal comparison of "APPLE", "BANANA", "ZETA", "ÉMILE" gives this order, because É
    /// (U+00C9) is after Z (U+005A).
    /// </remarks>
    [Fact]
    public async Task Names_are_ordered_ordinally_ignoring_case()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        string[] created = ["Zeta", "Émile", "apple", "Banana"];
        string[] expected = ["apple", "Banana", "Zeta", "Émile"];
        var unit = await Seed.CreateUnitAsync(manager, "Box");
        var items = new List<CreatedItem>();
        foreach (var name in created)
        {
            var item = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit, name: name);
            await ScenarioApi.AdjustAsync(manager, item.Id, "Warehouse", 10m, OpeningCount);
            items.Add(item);
        }

        foreach (var name in new[] { "Émile", "Zeta", "Banana", "apple" })
        {
            var customer = await CreateCustomerAsync(seller, name);
            await CreateAndShipAsync(seller, manager, customer.Id, items.Select(item => (item.Id, 1m)).ToArray());
        }

        var byItem = await ShippedByItemAsync(seller);
        var byCustomer = await ShippedByCustomerAsync(seller);

        Assert.Equal(expected.Select(name => (name, 4m, 4)), ItemRows(byItem));
        Assert.Equal(
            expected.SelectMany(customer => expected.Select(item => (customer, item, 1m, 1))),
            CustomerRows(byCustomer));
    }

    // ---- AC-32.2: what never counts ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-32.2, D-13, AC-28.6, PRD row 50: only shipments count. After a 10 Bag shipment, a positive Warehouse adjustment
    /// of 10 that records the goods coming back, and a negative adjustment of 5, the report still says 10 shipped on 1
    /// sales order. A Created and a Cancelled sales order of the same item for the same customer add nothing, and an item
    /// that is only on those two has no row at all. The ShippedToCustomer movements still add up to minus 10 (AC-32.4).
    /// </summary>
    [Fact]
    public async Task Adjustments_and_unshipped_sales_orders_do_not_count()
    {
        var scene = await SceneAsync();
        var bags = await ProductAsync(scene.Manager, 100m);
        var unshipped = await ProductAsync(scene.Manager, 100m);
        var shipped = await CreateAndShipAsync(scene.Seller, scene.Warehouse, scene.Customer.Id, (bags.Id, 10m));
        await ScenarioApi.AdjustAsync(scene.Manager, bags.Id, "Warehouse", 10m, $"Returned by the customer, sales order #{shipped}");
        await ScenarioApi.AdjustAsync(scene.Manager, bags.Id, "Warehouse", -5m, "Damaged in storage");
        await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (bags.Id, 20m), (unshipped.Id, 20m));
        var cancelled = await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (bags.Id, 30m), (unshipped.Id, 30m));
        Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(scene.Seller, cancelled)).StatusCode);

        var byItem = await ShippedByItemAsync(scene.Seller, $"customerId={scene.Customer.Id}");
        var byCustomer = await ShippedByCustomerAsync(scene.Seller, $"customerId={scene.Customer.Id}");

        Assert.Equal([(bags.Name, 10m, 1)], ItemRows(byItem));
        Assert.Equal([(scene.Customer.Name, bags.Name, 10m, 1)], CustomerRows(byCustomer));
        var movements = await ScenarioApi.AllRowsAsync(scene.Warehouse,
            $"{ScenarioApi.ApiPath}/movements?type=ShippedToCustomer&itemId={bags.Id}");
        Assert.Equal(-10m, movements.Sum(movement => movement.GetProperty("warehouseChange").GetDecimal()));
        Assert.Equal(95m, await WarehouseAsync(scene.Manager, bags.Id));
    }

    // ---- Filters ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-32.1, design 5.13: the item type filter, any letter case. A sales order shipped a raw material (decision #144)
    /// and a finished product. <c>itemType=RawMaterial</c> gives only the raw material, with its type and its own unit;
    /// <c>itemType=finishedproduct</c> only the product; no filter both, A to Z.
    /// </summary>
    [Fact]
    public async Task Item_type_filter_keeps_one_type()
    {
        var scene = await SceneAsync();
        var cheese = await RawMaterialAsync(scene.Manager, 50m);
        var bags = await ProductAsync(scene.Manager, 20m);
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, scene.Customer.Id, (cheese.Id, 12.5m), (bags.Id, 20m));
        var customer = $"customerId={scene.Customer.Id}";

        var raw = await ShippedByItemAsync(scene.Seller, $"{customer}&itemType=RawMaterial");
        var finished = await ShippedByItemAsync(scene.Seller, $"{customer}&itemType=finishedproduct");
        var both = await ShippedByItemAsync(scene.Seller, customer);

        Assert.Equal([(cheese.Name, 12.5m, 1)], ItemRows(raw));
        Assert.Equal("RawMaterial", raw.GetProperty("items")[0].GetProperty("itemType").GetString());
        Assert.Equal(cheese.UnitName, raw.GetProperty("items")[0].GetProperty("unit").GetString());
        Assert.Equal([(bags.Name, 20m, 1)], ItemRows(finished));

        // Seed names start with the item type: "FinishedProduct-..." sorts before "RawMaterial-...".
        Assert.Equal([(bags.Name, 20m, 1), (cheese.Name, 12.5m, 1)], ItemRows(both));
    }

    /// <summary>
    /// AC-33.1: <c>customerId</c> and <c>itemId</c> together narrow the customer report to one row; each alone keeps
    /// that customer's items, or that item's customers.
    /// </summary>
    [Fact]
    public async Task Customer_and_item_filters_combine()
    {
        var scene = await SceneAsync();
        var other = await CreateCustomerAsync(scene.Seller);
        var bags = await ProductAsync(scene.Manager, 100m);
        var boxes = await ProductAsync(scene.Manager, 100m);
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, scene.Customer.Id, (bags.Id, 1m), (boxes.Id, 2m));
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, other.Id, (bags.Id, 3m));

        var both = await ShippedByCustomerAsync(scene.Seller, $"customerId={scene.Customer.Id}&itemId={bags.Id}");
        var oneCustomer = await ShippedByCustomerAsync(scene.Seller, $"customerId={scene.Customer.Id}");
        var oneItem = await ShippedByCustomerAsync(scene.Seller, $"itemId={bags.Id}");

        Assert.Equal([(scene.Customer.Name, bags.Name, 1m, 1)], CustomerRows(both));
        Assert.Equal(
            new[] { bags, boxes }.OrderBy(item => item.Name.ToUpperInvariant(), StringComparer.Ordinal)
                .Select(item => item.Name),
            CustomerRows(oneCustomer).Select(row => row.Item));
        Assert.Equal(
            new[] { scene.Customer, other }.OrderBy(customer => customer.Name.ToUpperInvariant(), StringComparer.Ordinal)
                .Select(customer => customer.Name),
            CustomerRows(oneItem).Select(row => row.Customer));
    }

    // ---- Day boundaries (D-13, design 2.5) -----------------------------------------------------------------------------

    /// <summary>
    /// D-13, design 2.5, decision #89: whole UTC days on the shipment time, both ends included, either end optional.
    /// Shipments of one item at 23:59:59 on 2026-12-30 (4), 23:59:59 on 2026-12-31 (1) and 00:00:00 on 2027-01-01 (2).
    /// <c>to=2026-12-31</c> counts the 23:59:59 one but not midnight; <c>from=2026-12-31</c> leaves out the 30th; a
    /// one-day range is that day only; <c>from</c> alone, <c>to</c> alone and neither (all time) work, and
    /// <c>to=9999-12-31</c> has no upper bound.
    /// </summary>
    [Fact]
    public async Task Periods_are_whole_UTC_days_with_optional_ends()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 12, 30, 20, 0, 0, TimeSpan.Zero));
        await using var app = new TestApiFactory { FakeTime = time };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var seller = await app.CreateClientAsAsync(Roles.Seller);
        var bags = await ProductAsync(manager, 10m);
        var customer = await CreateCustomerAsync(seller);
        var orders = new[]
        {
            await CreateSalesOrderAsync(seller, customer.Id, (bags.Id, 4m)),
            await CreateSalesOrderAsync(seller, customer.Id, (bags.Id, 1m)),
            await CreateSalesOrderAsync(seller, customer.Id, (bags.Id, 2m)),
        };
        var shipmentTimes = new[]
        {
            new DateTimeOffset(2026, 12, 30, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        for (var i = 0; i < orders.Length; i++)
        {
            // A token lasts 8 hours (AC-01.2), so each shipper signs in at the new time.
            time.SetUtcNow(shipmentTimes[i]);
            var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);
            Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, orders[i])).StatusCode);
        }

        var reader = await app.CreateClientAsAsync(Roles.Seller);

        Assert.Equal((5m, 2), await TotalAsync(reader, "to=2026-12-31"));
        Assert.Equal((2m, 1), await TotalAsync(reader, "from=2027-01-01"));
        Assert.Equal((1m, 1), await TotalAsync(reader, "from=2026-12-31&to=2026-12-31"));
        Assert.Equal((3m, 2), await TotalAsync(reader, "from=2026-12-31"));
        Assert.Equal((4m, 1), await TotalAsync(reader, "to=2026-12-30"));
        Assert.Equal((7m, 3), await TotalAsync(reader, ""));
        Assert.Equal((7m, 3), await TotalAsync(reader, "to=9999-12-31"));
        Assert.Empty((await ShippedByItemAsync(reader, "from=2027-01-02")).GetProperty("items").EnumerateArray());

        // The customer report uses the same day rule (one shared base query, architecture 3.5).
        Assert.Equal([(customer.Name, bags.Name, 5m, 2)], CustomerRows(await ShippedByCustomerAsync(reader, "to=2026-12-31")));
        Assert.Equal([(customer.Name, bags.Name, 2m, 1)], CustomerRows(await ShippedByCustomerAsync(reader, "from=2027-01-01")));
    }

    // ---- AC-33.4, AC-29.3: the customer as it is now -------------------------------------------------------------------

    /// <summary>
    /// AC-33.4, AC-29.4: a customer deactivated after its shipment still has its row in the customer report, with
    /// <c>isActive: false</c>, and its shipment still counts in the item report.
    /// </summary>
    [Fact]
    public async Task Deactivated_customer_rows_still_appear_marked_inactive()
    {
        var scene = await SceneAsync();
        var bags = await ProductAsync(scene.Manager, 10m);
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, scene.Customer.Id, (bags.Id, 6m));
        var deactivate = await scene.Seller.PostAsync($"{CustomersPath}/{scene.Customer.Id}/deactivate", content: null, Cancel);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var byCustomer = await ShippedByCustomerAsync(scene.Seller, $"customerId={scene.Customer.Id}");
        var byItem = await ShippedByItemAsync(scene.Seller, $"customerId={scene.Customer.Id}");

        Assert.Equal([(scene.Customer.Name, bags.Name, 6m, 1)], CustomerRows(byCustomer));
        Assert.False(byCustomer.GetProperty("items")[0].GetProperty("customer").GetProperty("isActive").GetBoolean());
        Assert.Equal([(bags.Name, 6m, 1)], ItemRows(byItem));
    }

    /// <summary>
    /// AC-29.3: reports show the customer's current name. Two customers, "Bravo-..." and "Charlie-...", were shipped one
    /// item, so Bravo comes first. After Bravo is renamed "Delta-...", the report shows the new name and sorts by it:
    /// Charlie first, then Delta.
    /// </summary>
    [Fact]
    public async Task Renamed_customer_shows_and_sorts_by_its_new_name()
    {
        var scene = await SceneAsync();
        var bravo = await CreateCustomerAsync(scene.Seller, Seed.UniqueName("Bravo"));
        var charlie = await CreateCustomerAsync(scene.Seller, Seed.UniqueName("Charlie"));
        var bags = await ProductAsync(scene.Manager, 10m);
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, bravo.Id, (bags.Id, 1m));
        await CreateAndShipAsync(scene.Seller, scene.Warehouse, charlie.Id, (bags.Id, 2m));
        var before = await ShippedByCustomerAsync(scene.Seller, $"itemId={bags.Id}");

        var newName = Seed.UniqueName("Delta");
        var rename = await scene.Seller.PutAsJsonAsync($"{CustomersPath}/{bravo.Id}", new { name = newName }, Cancel);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var after = await ShippedByCustomerAsync(scene.Seller, $"itemId={bags.Id}");

        Assert.Equal([(bravo.Name, bags.Name, 1m, 1), (charlie.Name, bags.Name, 2m, 1)], CustomerRows(before));
        Assert.Equal([(charlie.Name, bags.Name, 2m, 1), (newName, bags.Name, 1m, 1)], CustomerRows(after));
        Assert.Equal(bravo.Id, after.GetProperty("items")[1].GetProperty("customer").GetProperty("id").GetInt32());
    }

    // ---- AC-32.5: empty answers and bad query values ---------------------------------------------------------------------

    /// <summary>
    /// AC-32.5, AC-33.2, design 2.5: no shipment in the period, a customer that never had one, and a <c>customerId</c> or
    /// <c>itemId</c> that names nothing are each <c>200</c> with an empty list and <c>totalCount</c> 0, never an error.
    /// </summary>
    [Theory]
    [InlineData(ByItem, "from=9999-12-01&to=9999-12-31")]
    [InlineData(ByItem, "to=2000-01-01")]
    [InlineData(ByItem, "customerId=999999")]
    [InlineData(ByCustomer, "from=9999-12-01&to=9999-12-31")]
    [InlineData(ByCustomer, "to=2000-01-01")]
    [InlineData(ByCustomer, "customerId=999999")]
    [InlineData(ByCustomer, "itemId=999999")]
    public async Task No_matching_shipment_is_an_empty_200(string report, string query)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var body = await ScenarioApi.GetAsync(seller, ReportPath(report, query));

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// AC-32.5: a customer that exists but has nothing shipped (only a Created sales order) gives an empty list in both
    /// reports.
    /// </summary>
    [Fact]
    public async Task Customer_without_shipments_gives_empty_reports()
    {
        var scene = await SceneAsync();
        var bags = await ProductAsync(scene.Manager, 0m);
        await CreateSalesOrderAsync(scene.Seller, scene.Customer.Id, (bags.Id, 5m));

        Assert.Empty((await ShippedByItemAsync(scene.Seller, $"customerId={scene.Customer.Id}")).GetProperty("items").EnumerateArray());
        Assert.Empty((await ShippedByCustomerAsync(scene.Seller, $"customerId={scene.Customer.Id}")).GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// AC-32.5, AC-33.2, design 7.2 "Reports" (the general list messages) and architecture 4.1 item 19: <c>from</c> after
    /// <c>to</c>, a date not in <c>yyyy-MM-dd</c>, an unknown <c>itemType</c>, paging outside its limits, and an id filter
    /// that is not a whole number (the framework's sentence, decision #113) are each <c>400</c> <c>validation</c> with the
    /// one message under the query name.
    /// </summary>
    [Theory]
    [InlineData(ByItem, "from=2026-12-31&to=2026-12-01", "from", "'from' must be on or before 'to'.")]
    [InlineData(ByItem, "from=yesterday", "from", "'from' must be a date in the format yyyy-MM-dd.")]
    [InlineData(ByItem, "to=2026-02-30", "to", "'to' must be a date in the format yyyy-MM-dd.")]
    [InlineData(ByItem, "itemType=Box", "itemType", "itemType must be RawMaterial or FinishedProduct.")]
    [InlineData(ByItem, "pageSize=101", "pageSize", "pageSize must be between 1 and 100.")]
    [InlineData(ByItem, "page=0", "page", "page must be 1 or greater.")]
    [InlineData(ByItem, "customerId=abc", "customerId", "The value 'abc' is not valid.")]
    [InlineData(ByCustomer, "from=2026-12-31&to=2026-12-01", "from", "'from' must be on or before 'to'.")]
    [InlineData(ByCustomer, "from=2026-1-5", "from", "'from' must be a date in the format yyyy-MM-dd.")]
    [InlineData(ByCustomer, "to=31-12-2026", "to", "'to' must be a date in the format yyyy-MM-dd.")]
    [InlineData(ByCustomer, "pageSize=0", "pageSize", "pageSize must be between 1 and 100.")]
    [InlineData(ByCustomer, "customerId=abc", "customerId", "The value 'abc' is not valid.")]
    [InlineData(ByCustomer, "itemId=abc", "itemId", "The value 'abc' is not valid.")]
    public async Task Bad_query_value_is_400_with_the_list_message(string report, string query, string path, string message)
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var response = await seller.GetAsync(ReportPath(report, query), Cancel);

        await ApiAssert.ValidationAsync(response, path, message);
    }

    /// <summary>
    /// Architecture 4.1 item 19, design 6: every problem with the query is reported in one <c>400</c>, each under its own
    /// name.
    /// </summary>
    [Fact]
    public async Task Every_bad_query_value_is_reported_in_one_400()
    {
        var seller = await factory.CreateClientAsAsync(Roles.Seller);

        var byItem = await seller.GetAsync(ReportPath(ByItem, "page=0&from=yesterday&to=2026-13-01&itemType=Box"), Cancel);
        var byCustomer = await seller.GetAsync(ReportPath(ByCustomer, "pageSize=500&from=2026-12-31&to=2026-12-01"), Cancel);

        var itemBody = await ApiJson.ReadAsync(byItem);
        Assert.Equal(HttpStatusCode.BadRequest, byItem.StatusCode);
        Assert.Equal(["page", "from", "to", "itemType"], itemBody.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        var customerBody = await ApiJson.ReadAsync(byCustomer);
        Assert.Equal(HttpStatusCode.BadRequest, byCustomer.StatusCode);
        Assert.Equal(["pageSize", "from"], customerBody.GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    // ---- AC-32.6, AC-33.2: roles -----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-32.6, AC-33.2, design 7.1, decision #147: a Production-only user gets <c>403</c> "Your roles do not allow this
    /// action. It needs the Warehouse, Manager or Seller role." for both reports, even with a bad date, because the role
    /// check comes before the query is read (design 2.2).
    /// </summary>
    [Theory]
    [InlineData(ByItem, "")]
    [InlineData(ByItem, "from=yesterday&itemType=Box")]
    [InlineData(ByCustomer, "")]
    [InlineData(ByCustomer, "from=yesterday&itemId=abc")]
    public async Task Production_only_user_gets_403(string report, string query)
    {
        var production = await factory.CreateClientAsAsync(Roles.Production);

        var response = await production.GetAsync(ReportPath(report, query), Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseManagerOrSeller);
    }

    /// <summary>AC-32.6, AC-33.2, AC-30.4: Warehouse, Manager and Seller users get <c>200</c> for both reports.</summary>
    [Theory]
    [InlineData(ByItem, Roles.Warehouse)]
    [InlineData(ByItem, Roles.Manager)]
    [InlineData(ByItem, Roles.Seller)]
    [InlineData(ByCustomer, Roles.Warehouse)]
    [InlineData(ByCustomer, Roles.Manager)]
    [InlineData(ByCustomer, Roles.Seller)]
    public async Task Warehouse_Manager_and_Seller_get_200(string report, string role)
    {
        var client = await factory.CreateClientAsAsync(role);

        var response = await client.GetAsync(ReportPath(report), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>AC-01.5: without a token, both reports answer <c>401</c> <c>not-authenticated</c> with <c>WWW-Authenticate: Bearer</c>.</summary>
    [Theory]
    [InlineData(ByItem)]
    [InlineData(ByCustomer)]
    public async Task Without_a_token_the_reports_are_401(string report)
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(ReportPath(report), Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The records most shared-database tests need, new for each test: a Manager, a Warehouse user, a Seller and an active
    /// customer. Each test filters the reports on this customer (or on its own item), so other tests' shipments never
    /// reach its rows.
    /// </summary>
    private async Task<Scene> SceneAsync()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var seller = await factory.CreateClientAsAsync(Roles.Seller);
        return new Scene(manager, warehouse, seller, await CreateCustomerAsync(seller));
    }

    /// <summary>The item report's rows as (item name, shipped quantity, sales order count), in order.</summary>
    private static List<(string Item, decimal Quantity, int Count)> ItemRows(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(row => (row.GetProperty("itemName").GetString()!, row.GetProperty("shippedQuantity").GetDecimal(),
                row.GetProperty("salesOrderCount").GetInt32()))
            .ToList();

    /// <summary>The customer report's rows as (customer name, item name, shipped quantity, sales order count), in order.</summary>
    private static List<(string Customer, string Item, decimal Quantity, int Count)> CustomerRows(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(row => (row.GetProperty("customer").GetProperty("name").GetString()!, row.GetProperty("itemName").GetString()!,
                row.GetProperty("shippedQuantity").GetDecimal(), row.GetProperty("salesOrderCount").GetInt32()))
            .ToList();

    /// <summary>The item's <c>shippedQuantity</c> in an item report page, or 0 when the item has no row (nothing shipped).</summary>
    private static decimal ShippedOf(JsonElement page, int itemId) =>
        page.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("itemId").GetInt32() == itemId)
            .Select(row => row.GetProperty("shippedQuantity").GetDecimal())
            .SingleOrDefault();

    /// <summary>The single row of an item report for a database with one shipped item, as (quantity, sales order count).</summary>
    private static async Task<(decimal Quantity, int Count)> TotalAsync(HttpClient client, string query)
    {
        var row = Assert.Single((await ShippedByItemAsync(client, query)).GetProperty("items").EnumerateArray());
        return (row.GetProperty("shippedQuantity").GetDecimal(), row.GetProperty("salesOrderCount").GetInt32());
    }

    /// <summary>The per-test records of <see cref="SceneAsync"/>.</summary>
    private sealed record Scene(HttpClient Manager, HttpClient Warehouse, HttpClient Seller, CreatedCustomer Customer);

    /// <summary>
    /// The PRD's December example (AC-32.3, AC-33.3), built once in its own database and only read by the tests that use
    /// it. Every shipment goes through the real ship endpoint, with the app's clock set to the PRD's dates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set-up, in order, on 30 November 2026: the unit "Bag"; "Shredded cheese 1 kg bag", then "Grated cheese 200 g
    /// bag" (so Shredded cheese has the lower id); 1,300 and 80 Bag of them in the Warehouse; customers "Deli Norte", then
    /// "Cafe Sur". Sales orders #1 (Deli Norte, 600 Shredded), #2 (Cafe Sur, 500 Shredded and 50 Grated), #3 (Deli Norte,
    /// 101 Shredded), #4 (Deli Norte, 500 Shredded, left Created), #5 and #6 (Cafe Sur, 1 Bag each, left Created, so the
    /// cancelled one is #7 as in the PRD), and #7 (Deli Norte, 300 Shredded), cancelled. Then #1 ships at 10:00 on 2
    /// December, and #2 and #3 at 14:30 on 5 December.
    /// </para>
    /// <para>
    /// <see cref="IAsyncLifetime"/> lets xUnit run the asynchronous set-up once, before the first test of the class, and
    /// dispose of the app after the last.
    /// </para>
    /// </remarks>
    public sealed class DecemberExample : IAsyncLifetime
    {
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 30, 9, 0, 0, TimeSpan.Zero));

        /// <summary>Creates the example's own app, on the fake clock.</summary>
        public DecemberExample() => App = new TestApiFactory { FakeTime = _time };

        /// <summary>The example's app and database.</summary>
        public TestApiFactory App { get; }

        /// <summary>A Seller signed in after the last shipment: reads both reports.</summary>
        public HttpClient Seller { get; private set; } = null!;

        /// <summary>A Warehouse user signed in after the last shipment: reads movements (a Seller may not, AC-30.6).</summary>
        public HttpClient Warehouse { get; private set; } = null!;

        /// <summary>"Shredded cheese 1 kg bag".</summary>
        public CreatedItem Shredded { get; private set; } = null!;

        /// <summary>"Grated cheese 200 g bag".</summary>
        public CreatedItem Grated { get; private set; } = null!;

        /// <summary>"Deli Norte".</summary>
        public CreatedCustomer DeliNorte { get; private set; } = null!;

        /// <summary>"Cafe Sur".</summary>
        public CreatedCustomer CafeSur { get; private set; } = null!;

        /// <summary>Builds the example (see the class remarks).</summary>
        public async ValueTask InitializeAsync()
        {
            var manager = await App.CreateClientAsAsync(Roles.Manager);
            var seller = await App.CreateClientAsAsync(Roles.Seller);
            var bag = await Seed.CreateUnitAsync(manager, "Bag");
            Shredded = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: ShreddedName);
            Grated = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: GratedName);
            await ScenarioApi.AdjustAsync(manager, Shredded.Id, "Warehouse", 1300m, OpeningCount);
            await ScenarioApi.AdjustAsync(manager, Grated.Id, "Warehouse", 80m, OpeningCount);
            DeliNorte = await CreateCustomerAsync(seller, "Deli Norte");
            CafeSur = await CreateCustomerAsync(seller, "Cafe Sur");

            int[] numbers =
            [
                await CreateSalesOrderAsync(seller, DeliNorte.Id, (Shredded.Id, 600m)),
                await CreateSalesOrderAsync(seller, CafeSur.Id, (Shredded.Id, 500m), (Grated.Id, 50m)),
                await CreateSalesOrderAsync(seller, DeliNorte.Id, (Shredded.Id, 101m)),
                await CreateSalesOrderAsync(seller, DeliNorte.Id, (Shredded.Id, 500m)),
                await CreateSalesOrderAsync(seller, CafeSur.Id, (Shredded.Id, 1m)),
                await CreateSalesOrderAsync(seller, CafeSur.Id, (Grated.Id, 1m)),
                await CreateSalesOrderAsync(seller, DeliNorte.Id, (Shredded.Id, 300m)),
            ];
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], numbers);
            Assert.Equal(HttpStatusCode.OK, (await CancelSalesOrderAsync(seller, 7)).StatusCode);

            // A token lasts 8 hours (AC-01.2), so each shipper signs in at the new time.
            _time.SetUtcNow(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));
            await ShipOrFailAsync(await App.CreateClientAsAsync(Roles.Warehouse), 1);

            _time.SetUtcNow(new DateTimeOffset(2026, 12, 5, 14, 30, 0, TimeSpan.Zero));
            var warehouse = await App.CreateClientAsAsync(Roles.Warehouse);
            await ShipOrFailAsync(warehouse, 2);
            await ShipOrFailAsync(warehouse, 3);

            Seller = await App.CreateClientAsAsync(Roles.Seller);
            Warehouse = warehouse;
        }

        /// <summary>Stops the example's app and deletes its database.</summary>
        public ValueTask DisposeAsync() => App.DisposeAsync();

        private static async Task ShipOrFailAsync(HttpClient warehouse, int number) =>
            Assert.Equal(HttpStatusCode.OK, (await ShipAsync(warehouse, number)).StatusCode);
    }
}
