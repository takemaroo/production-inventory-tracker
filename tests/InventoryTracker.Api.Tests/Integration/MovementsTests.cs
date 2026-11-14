using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-013: the movement history, <c>GET /api/v1/movements</c> and <c>GET /api/v1/movements/{id}</c> (US-11, design 5.6,
/// 2.5 lists).
/// </summary>
/// <remarks>
/// <para>
/// Most tests read one fixed history of eleven movements, <see cref="HistoryFactory"/>, written once for the whole class
/// through <see cref="StockLedger"/> (so every movement passes the ledger's D-2 shape, link (#87) and below-zero checks,
/// and the #86 guard). Because the history is fixed and nothing in these tests adds to it, a test can expect an exact
/// list, in an exact order, even without an item filter. Tests that add movements (a receipt, the volume test) create
/// their own app.
/// </para>
/// <para>
/// The history, newest first (the order <c>GET /movements</c> must return), with the stock it leaves:
/// </para>
/// <code>
/// name                     time (UTC)            type                  item    Warehouse  Production  links
/// CheeseCounted            2026-11-04 00:00:00   Adjustment            cheese  -0.5                   reason
/// StarchWrittenOff         2026-11-03 23:59:59   Adjustment            starch             -1          reason
/// CheeseSentOrderB         2026-11-03 12:00:00   SentToProduction      cheese  -10        +10         order B
/// CheeseReturned           2026-11-03 11:00:00   ReturnedToWarehouse   cheese  +5         -5          return R, order A
/// BagsProduced             2026-11-03 09:00:00   ProducedOutput        bags    +950                   order A   (same second as
/// CheeseConsumed           2026-11-03 09:00:00   ConsumedInProduction  cheese             -1000       order A    the next row)
/// CheeseComplementarySent  2026-11-02 12:00:00   SentToProduction      cheese  -20        +20         order A, complementary C
/// StarchSent               2026-11-02 10:00:00   SentToProduction      starch  -15        +15         order A   (same second as
/// CheeseSent               2026-11-02 10:00:00   SentToProduction      cheese  -995       +995        order A    the next row)
/// CheeseReceived           2026-11-01 08:00:00   Received              cheese  +1100                  receipt date 2026-11-01, note
/// StarchReceived           2026-11-01 00:00:00   Received              starch  +50                    receipt date 2026-10-31
/// </code>
/// <para>
/// They are written in the order CheeseReceived, StarchReceived, CheeseSent, StarchSent, CheeseComplementarySent,
/// CheeseConsumed, BagsProduced, CheeseReturned, StarchWrittenOff, CheeseSentOrderB, CheeseCounted, so ids rise in that
/// order. Two pairs share a second (the id must break the tie), and two movements were written later than a movement
/// they precede in time (StarchReceived, CheeseSentOrderB), so ordering by id alone would be wrong. Stock afterwards:
/// cheese 79.5 Warehouse, 20 Production storage; starch 35 and 14; bags 950 and 0.
/// </para>
/// </remarks>
public class MovementsTests(MovementsTests.HistoryFactory history) : IClassFixture<MovementsTests.HistoryFactory>
{
    private const string MovementsPath = "/api/v1/movements";

    /// <summary>Every movement of the history, newest first.</summary>
    private const string All = "CheeseCounted,StarchWrittenOff,CheeseSentOrderB,CheeseReturned,BagsProduced,CheeseConsumed,"
        + "CheeseComplementarySent,StarchSent,CheeseSent,CheeseReceived,StarchReceived";

    private const string TypeMessage =
        "type must be Received, SentToProduction, ConsumedInProduction, ProducedOutput, ReturnedToWarehouse or Adjustment.";

    private const string LocationMessage = "location must be Warehouse or ProductionStorage.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-11.1: who may read, and what each movement shows -----------------------------------------------------

    /// <summary>
    /// AC-11.1, PRD 4.2: any signed-in user can list movements and read one, whatever their role. (There is no
    /// <c>403</c> case: no role lacks access, and every user has at least one role, AC-03.1.)
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_list_and_read_movements(string role)
    {
        var reader = await history.CreateClientAsAsync(role);

        var list = await reader.GetAsync(MovementsPath, Cancel);
        var one = await reader.GetAsync($"{MovementsPath}/{history.Ids["CheeseSent"]}", Cancel);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(11, (await ApiJson.ReadAsync(list)).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
    }

    /// <summary>
    /// AC-11.1, design 5.6: a movement shows its item, unit, signed change per location (both for a transfer, null for a
    /// location it does not touch), type, UTC time with <c>Z</c>, user, linked order (the parent too, for a complementary
    /// send), complementary order or return, and its receipt date and note or reason, in exactly the fifteen design
    /// fields. Checked on one movement of every kind of link.
    /// </summary>
    [Fact]
    public async Task A_movement_shows_every_design_field()
    {
        var cheese = history.Cheese;
        var starch = history.Starch;

        var receipt = await GetMovementAsync("CheeseReceived");
        AssertMovement(receipt, "CheeseReceived", "Received", cheese, warehouse: 1100m, production: null,
            "2026-11-01T08:00:00Z", history.WarehouseUser, order: null, complementary: null, returnNumber: null,
            receiptDate: "2026-11-01", note: "Delivery note 4471", reason: null);

        var complementarySend = await GetMovementAsync("CheeseComplementarySent");
        AssertMovement(complementarySend, "CheeseComplementarySent", "SentToProduction", cheese, warehouse: -20m, production: 20m,
            "2026-11-02T12:00:00Z", history.WarehouseUser, order: history.OrderA, complementary: history.ComplementaryC,
            returnNumber: null, receiptDate: null, note: null, reason: null);

        var consumed = await GetMovementAsync("CheeseConsumed");
        AssertMovement(consumed, "CheeseConsumed", "ConsumedInProduction", cheese, warehouse: null, production: -1000m,
            "2026-11-03T09:00:00Z", history.ManagerUser, order: history.OrderA, complementary: null, returnNumber: null,
            receiptDate: null, note: null, reason: null);

        var returned = await GetMovementAsync("CheeseReturned");
        AssertMovement(returned, "CheeseReturned", "ReturnedToWarehouse", cheese, warehouse: 5m, production: -5m,
            "2026-11-03T11:00:00Z", history.WarehouseUser, order: history.OrderA, complementary: null,
            returnNumber: history.ReturnR, receiptDate: null, note: null, reason: null);

        var writtenOff = await GetMovementAsync("StarchWrittenOff");
        AssertMovement(writtenOff, "StarchWrittenOff", "Adjustment", starch, warehouse: null, production: -1m,
            "2026-11-03T23:59:59Z", history.ManagerUser, order: null, complementary: null, returnNumber: null,
            receiptDate: null, note: null, reason: "Spoiled");

        var produced = await GetMovementAsync("BagsProduced");
        AssertMovement(produced, "BagsProduced", "ProducedOutput", history.Bags, warehouse: 950m, production: null,
            "2026-11-03T09:00:00Z", history.ManagerUser, order: history.OrderA, complementary: null, returnNumber: null,
            receiptDate: null, note: null, reason: null);
    }

    /// <summary>
    /// AC-11.1, design 5.6: each row of the list is exactly the movement <c>GET /movements/{id}</c> returns, for every
    /// movement in the history (the shared <c>MovementMapping</c> gives both).
    /// </summary>
    [Fact]
    public async Task List_rows_equal_the_single_movement()
    {
        var page = await ListAsync("");

        foreach (var row in page.GetProperty("items").EnumerateArray())
        {
            var single = await ApiJson.ReadAsync(
                await history.Reader.GetAsync($"{MovementsPath}/{row.GetProperty("id").GetInt32()}", Cancel));
            Assert.Equal(single.GetRawText(), row.GetRawText());
        }
    }

    /// <summary>
    /// Design 2.2 step 3 and 7.2 pattern ("{Record} {id} not found."): an unknown movement id is <c>404</c>
    /// <c>not-found</c> "Movement {id} not found.". An id that is not a number matches no route, so it is the general
    /// <c>404</c> of architecture 4.1 item 8.
    /// </summary>
    [Theory]
    [InlineData("999999", "Movement 999999 not found.")]
    [InlineData("0", "Movement 0 not found.")]
    [InlineData("-1", "Movement -1 not found.")]
    [InlineData("abc", "No endpoint matches this path.")]
    public async Task Unknown_movement_is_404(string id, string message)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}/{id}", Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", message);
    }

    // ---- AC-11.2: filters, newest first ----------------------------------------------------------------------------

    /// <summary>
    /// AC-11.2, design 5.6 and 2.5: each filter on its own returns exactly the matching movements, newest first by time,
    /// then by id (PRD 4.1), with the total count. Covered: no filter; empty values (no filter); <c>itemId</c>;
    /// <c>location</c> (a movement matches when it changes that location, so transfers match both); <c>type</c> in any
    /// letter case; <c>orderNumber</c> (the order's own movements, its complementary send and its linked return);
    /// <c>complementaryOrderNumber</c>; and <c>from</c>/<c>to</c> as whole UTC days, both included, on
    /// <c>occurredAt</c>: 00:00:00 and 23:59:59 are inside their day, the next 00:00:00 is not, and the extreme dates
    /// 0001-01-01 and 9999-12-31 work.
    /// </summary>
    [Theory]
    [InlineData("", All)]
    [InlineData("itemId=&location=&type=&orderNumber=&complementaryOrderNumber=&from=&to=", All)]
    [InlineData("itemId={cheese}", "CheeseCounted,CheeseSentOrderB,CheeseReturned,CheeseConsumed,CheeseComplementarySent,CheeseSent,CheeseReceived")]
    [InlineData("itemId={starch}", "StarchWrittenOff,StarchSent,StarchReceived")]
    [InlineData("itemId={bags}", "BagsProduced")]
    [InlineData("location=Warehouse", "CheeseCounted,CheeseSentOrderB,CheeseReturned,BagsProduced,CheeseComplementarySent,StarchSent,CheeseSent,CheeseReceived,StarchReceived")]
    [InlineData("location=productionstorage", "StarchWrittenOff,CheeseSentOrderB,CheeseReturned,CheeseConsumed,CheeseComplementarySent,StarchSent,CheeseSent")]
    [InlineData("type=Received", "CheeseReceived,StarchReceived")]
    [InlineData("type=sentToProduction", "CheeseSentOrderB,CheeseComplementarySent,StarchSent,CheeseSent")]
    [InlineData("type=ConsumedInProduction", "CheeseConsumed")]
    [InlineData("type=ProducedOutput", "BagsProduced")]
    [InlineData("type=ReturnedToWarehouse", "CheeseReturned")]
    [InlineData("type=ADJUSTMENT", "CheeseCounted,StarchWrittenOff")]
    [InlineData("orderNumber={orderA}", "CheeseReturned,BagsProduced,CheeseConsumed,CheeseComplementarySent,StarchSent,CheeseSent")]
    [InlineData("orderNumber={orderB}", "CheeseSentOrderB")]
    [InlineData("complementaryOrderNumber={complementaryC}", "CheeseComplementarySent")]
    [InlineData("from=2026-11-03", "CheeseCounted,StarchWrittenOff,CheeseSentOrderB,CheeseReturned,BagsProduced,CheeseConsumed")]
    [InlineData("from=2026-11-04", "CheeseCounted")]
    [InlineData("to=2026-11-01", "CheeseReceived,StarchReceived")]
    [InlineData("from=2026-11-01&to=2026-11-01", "CheeseReceived,StarchReceived")]
    [InlineData("from=2026-11-02&to=2026-11-02", "CheeseComplementarySent,StarchSent,CheeseSent")]
    [InlineData("from=2026-11-03&to=2026-11-03", "StarchWrittenOff,CheeseSentOrderB,CheeseReturned,BagsProduced,CheeseConsumed")]
    [InlineData("from=2026-11-01&to=2026-11-04", All)]
    [InlineData("from=0001-01-01", All)]
    [InlineData("to=9999-12-31", All)]
    [InlineData("from=9999-12-31&to=9999-12-31", "")]
    public async Task Each_filter_returns_the_matching_movements_newest_first(string query, string expected)
    {
        await AssertListAsync(query, expected);
    }

    /// <summary>
    /// AC-11.2 "filters combine", design 2.5: several filters together match only movements that pass all of them (AND),
    /// still newest first.
    /// </summary>
    [Theory]
    [InlineData("itemId={cheese}&location=ProductionStorage&from=2026-11-03", "CheeseSentOrderB,CheeseReturned,CheeseConsumed")]
    [InlineData("itemId={cheese}&type=SentToProduction&orderNumber={orderA}", "CheeseComplementarySent,CheeseSent")]
    [InlineData("type=SentToProduction&location=Warehouse&complementaryOrderNumber={complementaryC}", "CheeseComplementarySent")]
    [InlineData("itemId={starch}&location=Warehouse&to=2026-11-02", "StarchSent,StarchReceived")]
    [InlineData("orderNumber={orderA}&location=Warehouse&from=2026-11-03&to=2026-11-03", "CheeseReturned,BagsProduced")]
    [InlineData("itemId={cheese}&type=Adjustment&location=Warehouse&from=2026-11-01&to=2026-11-04", "CheeseCounted")]
    public async Task Filters_combine_with_and(string query, string expected)
    {
        await AssertListAsync(query, expected);
    }

    /// <summary>
    /// AC-11.2 "no matches returns 200 with an empty list", design 2.5: including an item, order or complementary order
    /// that does not exist, filters that each match something but not together, and dates with no movements. The date
    /// filter is on <c>occurredAt</c>, not the receipt date: StarchReceived has receipt date 2026-10-31 but happened on
    /// 2026-11-01.
    /// </summary>
    [Theory]
    [InlineData("itemId=999999")]
    [InlineData("itemId=-1")]
    [InlineData("orderNumber=999999")]
    [InlineData("complementaryOrderNumber=999999")]
    [InlineData("itemId={bags}&type=Received")]
    [InlineData("itemId={bags}&location=ProductionStorage")]
    [InlineData("orderNumber={orderB}&complementaryOrderNumber={complementaryC}")]
    [InlineData("from=2026-11-05")]
    [InlineData("to=2026-10-31")]
    [InlineData("type=Received&from=2026-11-02")]
    public async Task No_match_is_200_with_an_empty_list(string query)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}?{history.Resolve(query)}", Cancel);
        var body = await ApiJson.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(ApiJson.Ids(body));
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    // ---- Lists: paging (design 2.5, PRD 4.1) ----------------------------------------------------------------------

    /// <summary>
    /// PRD 4.1, design 2.5: the list is paged with the total count on every page; defaults are page 1 and 50 per page;
    /// pages follow the newest-first order without gaps or repeats (the id tiebreak keeps rows with the same second on
    /// one fixed side of a page boundary); a page past the end is <c>200</c> with no items. Paging works together with a
    /// filter.
    /// </summary>
    [Fact]
    public async Task Movements_are_paged_newest_first_with_the_total_count()
    {
        var defaults = await ListAsync("");
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(50, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(11, defaults.GetProperty("totalCount").GetInt32());

        var pages = new List<JsonElement>();
        for (var number = 1; number <= 4; number++)
        {
            pages.Add(await ListAsync($"pageSize=4&page={number}"));
        }

        Assert.Equal(Names("CheeseCounted,StarchWrittenOff,CheeseSentOrderB,CheeseReturned"), ApiJson.Ids(pages[0]));
        Assert.Equal(Names("BagsProduced,CheeseConsumed,CheeseComplementarySent,StarchSent"), ApiJson.Ids(pages[1]));
        Assert.Equal(Names("CheeseSent,CheeseReceived,StarchReceived"), ApiJson.Ids(pages[2]));
        Assert.Empty(ApiJson.Ids(pages[3]));
        Assert.All(pages, page =>
        {
            Assert.Equal(11, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(4, page.GetProperty("pageSize").GetInt32());
        });
        Assert.Equal(4, pages[3].GetProperty("page").GetInt32());

        var filtered = await ListAsync($"itemId={history.Cheese.Id}&pageSize=2&page=2");
        Assert.Equal(Names("CheeseReturned,CheeseConsumed"), ApiJson.Ids(filtered));
        Assert.Equal(7, filtered.GetProperty("totalCount").GetInt32());

        var farPastTheEnd = await ListAsync($"page={int.MaxValue}&pageSize=100");
        Assert.Empty(ApiJson.Ids(farPastTheEnd));
    }

    // ---- 400: values in the wrong format ---------------------------------------------------------------------------

    /// <summary>
    /// Design 2.5 and 7.2: a filter or paging value in the wrong format is <c>400</c> <c>validation</c> with one message
    /// under its query name: <c>from</c> after <c>to</c> ("'from' must be on or before 'to'.", design 7.2), a date not
    /// written as a real <c>yyyy-MM-dd</c> date, an unknown location or type (numbers are not names, architecture 4.1
    /// item 3), and paging outside its limits.
    /// </summary>
    [Theory]
    [InlineData("from=2026-11-05&to=2026-11-04", "from", "'from' must be on or before 'to'.")]
    [InlineData("from=yesterday", "from", "'from' must be a date in the format yyyy-MM-dd.")]
    [InlineData("from=2026-11-1", "from", "'from' must be a date in the format yyyy-MM-dd.")]
    [InlineData("from=2026-11-01T00:00:00Z", "from", "'from' must be a date in the format yyyy-MM-dd.")]
    [InlineData("to=2026-02-30", "to", "'to' must be a date in the format yyyy-MM-dd.")]
    [InlineData("location=Shelf", "location", LocationMessage)]
    [InlineData("location=1", "location", LocationMessage)]
    [InlineData("type=Sent", "type", TypeMessage)]
    [InlineData("type=0", "type", TypeMessage)]
    [InlineData("page=0", "page", "page must be 1 or greater.")]
    [InlineData("pageSize=0", "pageSize", "pageSize must be between 1 and 100.")]
    [InlineData("pageSize=101", "pageSize", "pageSize must be between 1 and 100.")]
    public async Task Value_in_the_wrong_format_is_400(string query, string path, string message)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}?{query}", Cancel);

        await ApiAssert.ValidationAsync(response, path, message);
    }

    /// <summary>
    /// Design 6: several problems in one query are reported together in one <c>400</c>, each under its own name, with
    /// <c>detail</c> joining them in the order the parameters are checked (paging, then the filters as design 5.6 lists
    /// them).
    /// </summary>
    [Fact]
    public async Task Several_wrong_values_are_reported_together()
    {
        var response = await history.Reader.GetAsync(
            $"{MovementsPath}?page=0&pageSize=101&location=Shelf&type=Sent&from=2026-11-05&to=2026-11-04", Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"page must be 1 or greater. pageSize must be between 1 and 100. {LocationMessage} {TypeMessage} 'from' must be on or before 'to'.");
        Assert.Equal([LocationMessage], ApiJson.Messages(body, "location"));
        Assert.Equal([TypeMessage], ApiJson.Messages(body, "type"));
        Assert.Equal(["'from' must be on or before 'to'."], ApiJson.Messages(body, "from"));
    }

    /// <summary>
    /// Design 2.5: an id or number filter that is not a whole number is refused by model binding with <c>400</c>
    /// <c>validation</c>, keyed by its query name, before the action runs, the same as <c>?page=abc</c>. The message is MVC's own and names the C# property (<c>ItemId</c>), as it does for
    /// <c>Page</c>; the <c>errors</c> key is the query name.
    /// </summary>
    [Theory]
    [InlineData("itemId=abc", "itemId", "The value 'abc' is not valid for ItemId.")]
    [InlineData("itemId=1.5", "itemId", "The value '1.5' is not valid for ItemId.")]
    [InlineData("orderNumber=twelve", "orderNumber", "The value 'twelve' is not valid for OrderNumber.")]
    [InlineData("complementaryOrderNumber=99999999999", "complementaryOrderNumber",
        "The value '99999999999' is not valid for ComplementaryOrderNumber.")]
    public async Task Number_filter_that_is_not_a_whole_number_is_400(string query, string path, string message)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}?{query}", Cancel);

        await ApiAssert.ValidationAsync(response, path, message);
    }

    // ---- AC-11.3: no edits or deletes ------------------------------------------------------------------------------

    /// <summary>
    /// AC-11.3, design 4: no endpoint edits, deletes or adds a movement. <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> on
    /// <c>/movements/{id}</c> and <c>POST</c> on <c>/movements</c> get the framework's <c>405</c>
    /// <c>method-not-allowed</c>, even for a Manager, and the movement and the list are unchanged afterwards.
    /// </summary>
    [Theory]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("POST", false)]
    public async Task Movements_cannot_be_edited_deleted_or_added(string method, bool onOneMovement)
    {
        var manager = await history.CreateClientAsAsync(Roles.Manager);
        var path = $"{MovementsPath}/{history.Ids["CheeseReceived"]}";
        var before = await ApiJson.ReadAsync(await manager.GetAsync(path, Cancel));
        using var request = new HttpRequestMessage(new HttpMethod(method), onOneMovement ? path : MovementsPath)
        {
            Content = method == "DELETE" ? null : ApiJson.Content("""{ "warehouseChange": 5000, "itemId": 1 }"""),
        };

        var response = await manager.SendAsync(request, Cancel);

        var body = await ApiJson.ReadAsync(response);
        ApiAssert.Problem(response, body, HttpStatusCode.MethodNotAllowed, ApiAssert.NoTitleCheck,
            "method-not-allowed", "This method is not allowed on this path.");
        var after = await ApiJson.ReadAsync(await manager.GetAsync(path, Cancel));
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(11, (await ListAsync("")).GetProperty("totalCount").GetInt32());
    }

    // ---- AC-11.4: history adds up to the stock -----------------------------------------------------------------------

    /// <summary>
    /// AC-11.4: for each item with mixed movements (receipts, sends, a complementary send, consumption, output, a return,
    /// adjustments in both locations), adding up the listed <c>warehouseChange</c> and <c>productionStorageChange</c>
    /// values, read page by page, equals <c>GET /stock/{itemId}</c> for each location. The same holds with the list
    /// filtered to that location.
    /// </summary>
    [Fact]
    public async Task Listed_changes_add_up_to_the_stock_of_each_location()
    {
        var expected = new[]
        {
            (Item: history.Cheese, Warehouse: 79.5m, Production: 20m),
            (Item: history.Starch, Warehouse: 35m, Production: 14m),
            (Item: history.Bags, Warehouse: 950m, Production: 0m),
        };

        foreach (var (item, warehouse, production) in expected)
        {
            var rows = await AllRowsAsync($"itemId={item.Id}");
            var stock = await ApiJson.ReadAsync(await history.Reader.GetAsync($"/api/v1/stock/{item.Id}", Cancel));

            Assert.Equal(warehouse, stock.GetProperty("warehouse").GetDecimal());
            Assert.Equal(production, stock.GetProperty("productionStorage").GetDecimal());
            Assert.Equal(warehouse, Sum(rows, "warehouseChange"));
            Assert.Equal(production, Sum(rows, "productionStorageChange"));
            Assert.Equal(warehouse, Sum(await AllRowsAsync($"itemId={item.Id}&location=Warehouse"), "warehouseChange"));
            Assert.Equal(production,
                Sum(await AllRowsAsync($"itemId={item.Id}&location=ProductionStorage"), "productionStorageChange"));
        }
    }

    // ---- Receipts point here --------------------------------------------------------------------------------------

    /// <summary>
    /// Design 4 and 5.6, T-008: the <c>Location</c> header of a receipt's <c>201</c> is <c>/api/v1/movements/{id}</c>;
    /// following it now gives <c>200</c> with exactly the body the receipt returned, and the receipt appears in
    /// <c>GET /movements?itemId=3&amp;type=Received</c> (design 3.5 step 3) with the same row.
    /// </summary>
    [Fact]
    public async Task A_receipts_location_header_leads_to_its_movement()
    {
        await using var app = new TestApiFactory
        {
            FakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeSpan.Zero)),
        };
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var warehouse = await app.CreateClientAsAsync(Roles.Warehouse);

        var created = await warehouse.PostAsJsonAsync("/api/v1/receipts",
            new { itemId = cheese.Id, quantity = 1000, receiptDate = "2026-11-11", note = "Delivery note 4471" }, Cancel);
        var receipt = await ApiJson.ReadAsync(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = receipt.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/movements/{id}", created.Headers.Location?.OriginalString);

        var production = await app.CreateClientAsAsync(Roles.Production);
        var followed = await production.GetAsync(created.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await ApiJson.ReadAsync(followed)).GetRawText());

        var listed = await ApiJson.ReadAsync(
            await production.GetAsync($"{MovementsPath}?itemId={cheese.Id}&type=Received", Cancel));
        Assert.Equal(receipt.GetRawText(), Assert.Single(listed.GetProperty("items").EnumerateArray()).GetRawText());
    }

    // ---- 401 ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-01.5, design 2.2 step 1: both endpoints answer <c>401</c> <c>not-authenticated</c> without a token, before any
    /// other check (an unknown id or an invalid filter does not change that).
    /// </summary>
    [Theory]
    [InlineData(MovementsPath)]
    [InlineData(MovementsPath + "?type=Nonsense&from=bad")]
    [InlineData(MovementsPath + "/1")]
    [InlineData(MovementsPath + "/999999")]
    public async Task Movements_without_a_token_are_401(string path)
    {
        using var anonymous = history.CreateClient();

        var response = await anonymous.GetAsync(path, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    // ---- PRD section 5: performance -------------------------------------------------------------------------------

    /// <summary>
    /// PRD section 5 performance target: with 100,000 movements over 500 items (four of the six types: Received,
    /// SentToProduction, ConsumedInProduction and Adjustment, spread over about seven months) and 1,000 orders in the
    /// database, each kind of history request answers within 1 second for 95% of requests
    /// (asserted on the 19th of 20 sorted timings): the newest page, the last page, and pages filtered by item, type,
    /// location, a month, an order and a combination, plus one movement by id. The counts and the stock sum are checked
    /// too, so a fast but wrong answer fails.
    /// </summary>
    [Fact]
    public async Task History_reads_meet_the_performance_target_with_100000_movements()
    {
        await using var app = new TestApiFactory();
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var unit = await Seed.CreateUnitAsync(manager);
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, unit);
        var user = await app.CreateUserAsync(Seed.UniqueEmail("perf"), Roles.Manager);
        await SeedVolumeAsync(app, unit.Id, product.Id, user.Id);
        var reader = await app.CreateClientAsAsync(Roles.Production);

        var (firstItem, firstOrder, someMovement) = await VolumeKeysAsync(app);

        // Counts from the seed's arithmetic (see SeedVolumeAsync).
        Assert.Equal(100_000, await CountAsync(reader, ""));
        Assert.Equal(200, await CountAsync(reader, $"itemId={firstItem}"));
        Assert.Equal(25_000, await CountAsync(reader, "type=Received"));
        Assert.Equal(50_000, await CountAsync(reader, "location=ProductionStorage"));
        Assert.Equal(14_880, await CountAsync(reader, "from=2026-03-01&to=2026-03-31"));

        // AC-11.4 at volume: the first item's history adds up to its stock (W 37.5, P 25).
        var stock = await ApiJson.ReadAsync(await reader.GetAsync($"/api/v1/stock/{firstItem}", Cancel));
        var rows = await AllRowsAsync(reader, $"itemId={firstItem}");
        Assert.Equal(37.5m, stock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(25m, stock.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(37.5m, Sum(rows, "warehouseChange"));
        Assert.Equal(25m, Sum(rows, "productionStorageChange"));

        var paths = new[]
        {
            $"{MovementsPath}?pageSize=100",
            $"{MovementsPath}?pageSize=100&page=1000",
            $"{MovementsPath}?pageSize=100&itemId={firstItem}",
            $"{MovementsPath}?pageSize=100&type=Received",
            $"{MovementsPath}?pageSize=100&location=ProductionStorage",
            $"{MovementsPath}?pageSize=100&from=2026-03-01&to=2026-03-31",
            $"{MovementsPath}?pageSize=100&orderNumber={firstOrder}",
            $"{MovementsPath}?pageSize=100&type=Adjustment&location=Warehouse&from=2026-02-01&to=2026-05-31",
            $"{MovementsPath}/{someMovement}",
        };

        foreach (var path in paths)
        {
            var times = await TimeAsync(reader, path);

            // Written to the test's output (visible in a TRX report), so a slow machine can be spotted before it fails.
            TestContext.Current.TestOutputHelper?.WriteLine($"GET {path}: p95 {times[18]} ms, max {times[19]} ms.");
            Assert.True(times[18] < 1000, $"GET {path} p95 was {times[18]} ms.");
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shared app for this class, holding the fixed history in the class remarks. xUnit creates it once for the
    /// class and calls <see cref="InitializeAsync"/> before the first test (<see cref="IAsyncLifetime"/>), then disposes
    /// it after the last.
    /// </summary>
    public sealed class HistoryFactory : TestApiFactory, IAsyncLifetime
    {
        private readonly Dictionary<string, int> _ids = [];

        /// <summary>The id of each movement in the history, by its name in the class remarks.</summary>
        public IReadOnlyDictionary<string, int> Ids => _ids;

        /// <summary>Raw material "cheese", in kg.</summary>
        public CreatedItem Cheese { get; private set; } = null!;

        /// <summary>Raw material "starch", in kg.</summary>
        public CreatedItem Starch { get; private set; } = null!;

        /// <summary>The finished product, in its own unit.</summary>
        public CreatedItem Bags { get; private set; } = null!;

        /// <summary>Order A: most of the history belongs to it.</summary>
        public int OrderA { get; private set; }

        /// <summary>Order B: one send.</summary>
        public int OrderB { get; private set; }

        /// <summary>Complementary order C, under order A.</summary>
        public int ComplementaryC { get; private set; }

        /// <summary>Return R of cheese, linked to order A.</summary>
        public int ReturnR { get; private set; }

        /// <summary>The Warehouse user who made the receipts, the complementary send, the return and order B's send.</summary>
        public AppUser WarehouseUser { get; private set; } = null!;

        /// <summary>The Manager who made the other movements.</summary>
        public AppUser ManagerUser { get; private set; } = null!;

        /// <summary>A signed-in Warehouse user's client, for tests that only read.</summary>
        public HttpClient Reader { get; private set; } = null!;

        /// <summary>
        /// Replaces the placeholders <c>{cheese}</c>, <c>{starch}</c>, <c>{bags}</c>, <c>{orderA}</c>, <c>{orderB}</c>
        /// and <c>{complementaryC}</c> in a query with the real ids, which are only known once the history exists.
        /// </summary>
        public string Resolve(string query) => query
            .Replace("{cheese}", Cheese.Id.ToString())
            .Replace("{starch}", Starch.Id.ToString())
            .Replace("{bags}", Bags.Id.ToString())
            .Replace("{orderA}", OrderA.ToString())
            .Replace("{orderB}", OrderB.ToString())
            .Replace("{complementaryC}", ComplementaryC.ToString());

        /// <summary>
        /// Creates the items, orders, complementary order and return the movements link to, then writes the eleven
        /// movements through <see cref="StockLedger"/> in one write transaction, as a service would.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            var manager = await CreateClientAsAsync(Roles.Manager);
            var kg = await Seed.CreateUnitAsync(manager);
            Cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
            Starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg);
            Bags = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct);
            WarehouseUser = await CreateUserAsync(Seed.UniqueEmail("warehouse"), Roles.Warehouse);
            ManagerUser = await CreateUserAsync(Seed.UniqueEmail("manager"), Roles.Manager);
            Reader = await CreateClientAsAsync(Roles.Warehouse);

            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();

            // The records the movements link to (foreign keys). They are not movements, so the ledger rules do not
            // apply to them; the endpoints that create them arrive in later tasks.
            var at = At(1, 0, 0, 0);
            var orderA = new ProductionOrder
            {
                FinishedProductId = Bags.Id, OrderedQuantity = 1000m, Status = OrderStatus.Completed,
                CreatedAt = at, CreatedById = ManagerUser.Id, StatusChangedAt = at,
            };
            var orderB = new ProductionOrder
            {
                // InProduction, because it has had its send (CheeseSentOrderB), as real data would be.
                FinishedProductId = Bags.Id, OrderedQuantity = 10m, Status = OrderStatus.InProduction,
                CreatedAt = at, CreatedById = ManagerUser.Id, StatusChangedAt = At(3, 12, 0, 0),
                SentAt = At(3, 12, 0, 0), SentById = WarehouseUser.Id,
            };
            var complementary = new ComplementaryOrder
            {
                ParentOrder = orderA, Status = ComplementaryOrderStatus.Sent, Reason = "Hopper spill",
                RequestedAt = at, RequestedById = ManagerUser.Id, SentAt = At(2, 12, 0, 0), SentById = WarehouseUser.Id,
            };
            var leftoverReturn = new LeftoverReturn
            {
                ItemId = Cheese.Id, Quantity = 5m, Reason = "Not needed", ProductionOrder = orderA,
                Status = ReturnStatus.Received, CreatedAt = at, CreatedById = ManagerUser.Id,
                ReceivedAt = At(3, 11, 0, 0), ReceivedById = WarehouseUser.Id,
            };
            db.AddRange(orderA, orderB, complementary, leftoverReturn);
            await db.SaveChangesAsync(Cancel);
            OrderA = orderA.Number;
            OrderB = orderB.Number;
            ComplementaryC = complementary.Number;
            ReturnR = leftoverReturn.Number;

            var warehouse = WarehouseUser.Id;
            var boss = ManagerUser.Id;
            var movements = new (string Name, Movement Movement)[]
            {
                ("CheeseReceived", new Movement
                {
                    Type = MovementType.Received, ItemId = Cheese.Id, WarehouseChange = 1100m, OccurredAt = At(1, 8, 0, 0),
                    UserId = warehouse, ReceiptDate = new DateOnly(2026, 11, 1), Note = "Delivery note 4471",
                }),
                ("StarchReceived", new Movement
                {
                    Type = MovementType.Received, ItemId = Starch.Id, WarehouseChange = 50m, OccurredAt = At(1, 0, 0, 0),
                    UserId = warehouse, ReceiptDate = new DateOnly(2026, 10, 31),
                }),
                ("CheeseSent", Send(Cheese.Id, 995m, At(2, 10, 0, 0), boss, OrderA)),
                ("StarchSent", Send(Starch.Id, 15m, At(2, 10, 0, 0), boss, OrderA)),
                ("CheeseComplementarySent", new Movement
                {
                    Type = MovementType.SentToProduction, ItemId = Cheese.Id, WarehouseChange = -20m,
                    ProductionStorageChange = 20m, OccurredAt = At(2, 12, 0, 0), UserId = warehouse,
                    ProductionOrderNumber = OrderA, ComplementaryOrderNumber = ComplementaryC,
                }),
                ("CheeseConsumed", new Movement
                {
                    Type = MovementType.ConsumedInProduction, ItemId = Cheese.Id, ProductionStorageChange = -1000m,
                    OccurredAt = At(3, 9, 0, 0), UserId = boss, ProductionOrderNumber = OrderA,
                }),
                ("BagsProduced", new Movement
                {
                    Type = MovementType.ProducedOutput, ItemId = Bags.Id, WarehouseChange = 950m,
                    OccurredAt = At(3, 9, 0, 0), UserId = boss, ProductionOrderNumber = OrderA,
                }),
                ("CheeseReturned", new Movement
                {
                    Type = MovementType.ReturnedToWarehouse, ItemId = Cheese.Id, WarehouseChange = 5m,
                    ProductionStorageChange = -5m, OccurredAt = At(3, 11, 0, 0), UserId = warehouse,
                    ReturnNumber = ReturnR, ProductionOrderNumber = OrderA,
                }),
                ("StarchWrittenOff", new Movement
                {
                    Type = MovementType.Adjustment, ItemId = Starch.Id, ProductionStorageChange = -1m,
                    OccurredAt = At(3, 23, 59, 59), UserId = boss, Reason = "Spoiled",
                }),
                ("CheeseSentOrderB", Send(Cheese.Id, 10m, At(3, 12, 0, 0), warehouse, OrderB)),
                ("CheeseCounted", new Movement
                {
                    Type = MovementType.Adjustment, ItemId = Cheese.Id, WarehouseChange = -0.5m,
                    OccurredAt = At(4, 0, 0, 0), UserId = boss, Reason = "Count correction",
                }),
            };

            await db.InWriteTransactionAsync(async () =>
            {
                foreach (var (name, movement) in movements)
                {
                    await ledger.AddAsync(movement, Cancel);
                    _ids[name] = movement.Id;
                }
            }, Cancel);
        }

        /// <summary>A UTC time on day <paramref name="day"/> of November 2026.</summary>
        private static DateTime At(int day, int hour, int minute, int second) =>
            new(2026, 11, day, hour, minute, second, DateTimeKind.Utc);

        private static Movement Send(int itemId, decimal quantity, DateTime at, string userId, int order) => new()
        {
            Type = MovementType.SentToProduction, ItemId = itemId, WarehouseChange = -quantity,
            ProductionStorageChange = quantity, OccurredAt = at, UserId = userId, ProductionOrderNumber = order,
        };
    }

    /// <summary>The ids of the named movements (comma-separated names from the history), in that order.</summary>
    private IEnumerable<int> Names(string names) =>
        names.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => history.Ids[name]).ToList();

    private async Task<JsonElement> ListAsync(string query)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}?{history.Resolve(query)}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>Checks that the query returns exactly the named movements, in that order, with that total count.</summary>
    private async Task AssertListAsync(string query, string expected)
    {
        var body = await ListAsync(query);
        var ids = Names(expected).ToList();

        Assert.Equal(ids, ApiJson.Ids(body));
        Assert.Equal(ids.Count, body.GetProperty("totalCount").GetInt32());
    }

    private async Task<JsonElement> GetMovementAsync(string name)
    {
        var response = await history.Reader.GetAsync($"{MovementsPath}/{history.Ids[name]}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>Every row of the query across all pages, reading two rows per page so the sum crosses page boundaries.</summary>
    private Task<List<JsonElement>> AllRowsAsync(string query) => AllRowsAsync(history.Reader, query, pageSize: 2);

    private static async Task<List<JsonElement>> AllRowsAsync(HttpClient reader, string query, int pageSize = 100)
    {
        var rows = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var body = await ApiJson.ReadAsync(
                await reader.GetAsync($"{MovementsPath}?{query}&pageSize={pageSize}&page={page}", Cancel));
            var items = body.GetProperty("items").EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return rows;
            }

            rows.AddRange(items);
        }
    }

    /// <summary>The sum of one change field over the rows; a null change (location not touched) counts as 0.</summary>
    private static decimal Sum(IEnumerable<JsonElement> rows, string field) =>
        rows.Sum(row => row.GetProperty(field).ValueKind == JsonValueKind.Null ? 0m : row.GetProperty(field).GetDecimal());

    private static async Task<int> CountAsync(HttpClient reader, string query)
    {
        var response = await reader.GetAsync($"{MovementsPath}?{query}&pageSize=1", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("totalCount").GetInt32();
    }

    /// <summary>Checks every one of the fifteen design 5.6 fields of a movement.</summary>
    private void AssertMovement(JsonElement row, string name, string type, CreatedItem item, decimal? warehouse,
        decimal? production, string occurredAt, AppUser user, int? order, int? complementary, int? returnNumber,
        string? receiptDate, string? note, string? reason)
    {
        Assert.Equal(15, row.EnumerateObject().Count());
        Assert.Equal(history.Ids[name], row.GetProperty("id").GetInt32());
        Assert.Equal(type, row.GetProperty("type").GetString());
        Assert.Equal(item.Id, row.GetProperty("itemId").GetInt32());
        Assert.Equal(item.Name, row.GetProperty("itemName").GetString());
        Assert.Equal(item.UnitName, row.GetProperty("unit").GetString());
        AssertNumber(warehouse, row.GetProperty("warehouseChange"));
        AssertNumber(production, row.GetProperty("productionStorageChange"));
        Assert.Equal(occurredAt, row.GetProperty("occurredAt").GetString());
        Assert.Equal(user.Id, row.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(user.Email, row.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(2, row.GetProperty("user").EnumerateObject().Count());
        AssertNumber(order, row.GetProperty("orderNumber"));
        AssertNumber(complementary, row.GetProperty("complementaryOrderNumber"));
        AssertNumber(returnNumber, row.GetProperty("returnNumber"));
        AssertText(receiptDate, row.GetProperty("receiptDate"));
        AssertText(note, row.GetProperty("note"));
        AssertText(reason, row.GetProperty("reason"));
    }

    private static void AssertNumber(decimal? expected, JsonElement value)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        else
        {
            Assert.Equal(expected.Value, value.GetDecimal());
        }
    }

    private static void AssertNumber(int? expected, JsonElement value) => AssertNumber((decimal?)expected, value);

    private static void AssertText(string? expected, JsonElement value)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        else
        {
            Assert.Equal(expected, value.GetString());
        }
    }

    /// <summary>
    /// Fills a fresh database to the PRD's performance-target size with set-based, parameterised SQL (100,000 ledger
    /// calls would take minutes; T-008 seeds its volume test the same way). The rows still follow the ledger's D-2 shape
    /// and #87 link rules. 500 raw materials; 1,000 Completed orders; 100,000 movements, movement <c>i</c> for item
    /// <c>i % 500</c> at 2026-01-01 plus <c>3 × i</c> minutes (so 480 a day, to late July), with the type cycling every
    /// 500 rows: Received W +2 (with a receipt date), SentToProduction W −1 P +1 (with an order), ConsumedInProduction
    /// P −0.5 (with an order), Adjustment W −0.25 (with a reason). So each item has 50 of each type: Warehouse
    /// 100 − 50 − 12.5 = 37.5 and Production storage 50 − 25 = 25; 25,000 of each type; 50,000 touch Production
    /// storage; March 2026 (days 59 to 89 after 1 January) holds movements 28,320 to 43,199, which is 14,880.
    /// </summary>
    private static async Task SeedVolumeAsync(TestApiFactory app, int unitId, int productId, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.InWriteTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
                INSERT INTO Items (Name, NameNormalized, Description, Type, UnitId, CreatedAt)
                SELECT 'perf-' || i, 'PERF-' || i, NULL, 'RawMaterial', {unitId}, '2026-01-01 00:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
                INSERT INTO ProductionOrders (FinishedProductId, OrderedQuantity, Status, CreatedAt, CreatedById, StatusChangedAt)
                SELECT {productId}, '10.0', 'Completed', '2026-01-01 00:00:00', {userId}, '2026-01-01 00:00:00' FROM n
                """, Cancel);
            await db.Database.ExecuteSqlAsync($"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99999),
                     typed(i, kind) AS (SELECT i, (i / 500) % 4 FROM n)
                INSERT INTO Movements (Type, ItemId, WarehouseChange, ProductionStorageChange, OccurredAt, UserId,
                                       ProductionOrderNumber, ReceiptDate, Reason)
                SELECT CASE kind WHEN 0 THEN 'Received' WHEN 1 THEN 'SentToProduction'
                                 WHEN 2 THEN 'ConsumedInProduction' ELSE 'Adjustment' END,
                       (SELECT MIN(Id) FROM Items WHERE Name LIKE 'perf-%') + (i % 500),
                       CASE kind WHEN 0 THEN '2.0' WHEN 1 THEN '-1.0' WHEN 2 THEN NULL ELSE '-0.25' END,
                       CASE kind WHEN 1 THEN '1.0' WHEN 2 THEN '-0.5' ELSE NULL END,
                       datetime('2026-01-01 00:00:00', '+' || (i * 3) || ' minutes'),
                       {userId},
                       CASE WHEN kind IN (1, 2) THEN (SELECT MIN(Number) FROM ProductionOrders) + (i % 1000) END,
                       CASE kind WHEN 0 THEN '2026-01-01' END,
                       CASE kind WHEN 3 THEN 'Count correction' END
                FROM typed
                """, Cancel);
        }, Cancel);
    }

    /// <summary>The first volume item's id, an order number that has movements, and the id of a movement mid-table.</summary>
    private static async Task<(int Item, int Order, int Movement)> VolumeKeysAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.Items.Where(candidate => candidate.Name.StartsWith("perf-")).MinAsync(candidate => candidate.Id, Cancel);
        var order = await db.Movements.Where(movement => movement.ProductionOrderNumber != null)
            .MinAsync(movement => movement.ProductionOrderNumber!.Value, Cancel);
        var movement = await db.Movements.OrderBy(candidate => candidate.Id).Skip(50_000).Select(candidate => candidate.Id).FirstAsync(Cancel);
        return (item, order, movement);
    }

    /// <summary>Times 20 calls of <paramref name="path"/> after one warm-up call, and returns the times sorted, in ms.</summary>
    private static async Task<long[]> TimeAsync(HttpClient client, string path)
    {
        (await client.GetAsync(path, Cancel)).EnsureSuccessStatusCode();
        var times = new long[20];
        for (var i = 0; i < times.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync(path, Cancel);
            await response.Content.ReadAsStringAsync(Cancel);
            times[i] = watch.ElapsedMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Array.Sort(times);
        return times;
    }
}
