using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-017: complementary orders (US-16, US-17, the ComplementaryAwaitingApproval notification of AC-23.2; design 3.7, 5.8,
/// 5.10, 7.2 and 7.3): <c>POST /api/v1/production-orders/{number}/complementary-orders</c>, <c>GET
/// /api/v1/complementary-orders</c>, <c>GET /api/v1/complementary-orders/{number}</c>, and <c>POST
/// /api/v1/complementary-orders/{number}/approve</c> and <c>/reject</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything under test goes through the API. Complementary orders in a status no T-017 endpoint can produce in one step
/// (Sent, which is T-018, or Cancelled without ending a parent) are inserted with
/// <see cref="OrderFlow.InsertComplementaryOrderAsync"/>, for the AC-17.3 checks only.
/// </para>
/// <para>
/// Only one order can be InProduction per database (AC-15.10). The shared <see cref="RunningOrderApp"/> holds one
/// InProduction order for the whole class, and no test on it sends, completes or cancels an order in production; tests
/// that need their own exact numbers, names or times, or that end the parent, create their own factory.
/// </para>
/// </remarks>
public class ComplementaryOrdersTests(ComplementaryOrdersTests.RunningOrderApp factory)
    : IClassFixture<ComplementaryOrdersTests.RunningOrderApp>
{
    /// <summary>The reason of design 5.8's example.</summary>
    private const string SpillReason = "Hopper spill at start of run";

    /// <summary>AC-16.2, design 7.2: the end of the <c>parent-not-in-production</c> message.</summary>
    private const string OnlyInProduction = "Complementary orders can be raised only while the order is in production.";

    /// <summary>AC-17.3, design 7.2: the end of the <c>not-pending-approval</c> message.</summary>
    private const string OnlyPending = "Only a PendingApproval complementary order can be approved or rejected.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-16.1: requesting a complementary order -------------------------------------------------------------------

    /// <summary>
    /// AC-16.1, AC-16.5, AC-05.3, AC-23.2, design 2.1, 5.8, 5.10 and 7.3. Order #1 is cancelled; order #2 (Cheese 1 kg and
    /// Potato starch 0.015 kg per bag, 1,000 bags) is sent. At 11:20 the Production user lucia.mendez@example.com requests
    /// Potato starch 0.5 kg and Cheese 20 kg, in that order, with the reason "  Hopper spill at start of run  ".
    /// <c>201</c>, <c>Location: /api/v1/complementary-orders/1</c>: complementary order #1 (its own sequence, not the
    /// orders'), for order #2, PendingApproval, the reason trimmed, requested at 11:20 by Lucía, no notes, the lines in the
    /// order requested with the send ranges at 2% (0.49 to 0.51, 19.6 to 20.4) and no sent quantity, and one history entry.
    /// The shape has exactly the design's fields and no ordered or produced quantity (AC-16.5). <c>GET</c> returns the
    /// same body. No stock moves and no movement is written. One ComplementaryAwaitingApproval notification exists, with
    /// the design 7.3 message listing the materials in recipe line order, and the design 5.10 data. The parent's detail
    /// lists complementary order #1 as PendingApproval (T-009). The request is logged.
    /// </summary>
    [Fact]
    public async Task Production_user_requests_extra_material_for_the_running_order()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var lucia = await app.CreateUserAsync("lucia.mendez@example.com", Roles.Production);
        using var production = app.CreateClientWithToken(await app.SignInAsync(lucia.Email!));
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        await ReceiveAsync(manager, starch.Id, 100m);
        await SetStatusAsync(app, await CreateOrderAsync(manager, product.Id, 10m), OrderStatus.Cancelled, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(2, number);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m), (starch.Id, 15m))).StatusCode);
        var movementsBefore = await MovementCountAsync(app);
        time.SetUtcNow(EightAm.AddHours(3).AddMinutes(20));

        var response = await RequestComplementaryAsync(production, number, "  Hopper spill at start of run  ",
            (starch.Id, 0.5m), (cheese.Id, 20m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/complementary-orders/1", response.Headers.Location?.AbsolutePath);
        var body = await ApiJson.ReadAsync(response);
        AssertShape(body);
        Assert.Equal(1, body.GetProperty("number").GetInt32());
        Assert.Equal(2, body.GetProperty("orderNumber").GetInt32());
        Assert.Equal("PendingApproval", body.GetProperty("status").GetString());
        Assert.Equal(SpillReason, body.GetProperty("reason").GetString());
        Assert.Equal("2026-11-11T11:20:00Z", body.GetProperty("requestedAt").GetString());
        AssertPerson(body.GetProperty("requestedBy"), lucia);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("decisionNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancellationNote").ValueKind);

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([starch.Id, cheese.Id], lines.Select(line => line.GetProperty("itemId").GetInt32()));
        AssertLine(lines[0], starch.Id, "Potato starch", "kg", requested: 0.5m, min: 0.49m, max: 0.51m);
        AssertLine(lines[1], cheese.Id, "Cheese", "kg", requested: 20m, min: 19.6m, max: 20.4m);

        var history = Assert.Single(body.GetProperty("history").EnumerateArray());
        Assert.Equal("PendingApproval", history.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T11:20:00Z", history.GetProperty("at").GetString());
        AssertPerson(history.GetProperty("by"), lucia);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("note").ValueKind);

        // Design 5.8: GET returns exactly what the request returned.
        Assert.Equal(body.GetRawText(), (await ReadComplementaryAsync(manager, 1)).GetRawText());

        // T-017: no stock moves until an approved request is sent (T-018).
        Assert.Equal(movementsBefore, await MovementCountAsync(app));
        var cheeseStock = await StockAsync(manager, cheese.Id);
        Assert.Equal(1000m, cheeseStock.GetProperty("warehouse").GetDecimal());
        Assert.Equal(1000m, cheeseStock.GetProperty("productionStorage").GetDecimal());

        // AC-16.1, AC-23.2, design 5.10 and 7.3: one notification, materials in recipe line order (Cheese first).
        var page = await NotificationsPageAsync(manager);
        var notification = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("ComplementaryAwaitingApproval", notification.GetProperty("type").GetString());
        Assert.Equal(2, notification.GetProperty("orderNumber").GetInt32());
        Assert.Equal(1, notification.GetProperty("complementaryOrderNumber").GetInt32());
        Assert.Equal("2026-11-11T11:20:00Z", notification.GetProperty("createdAt").GetString());
        Assert.Equal(
            "Complementary order #1 for order #2 is waiting for approval. lucia.mendez@example.com requested Cheese 20 kg, Potato starch 0.5 kg. Reason: \"Hopper spill at start of run\"",
            notification.GetProperty("message").GetString());
        var data = notification.GetProperty("data");
        Assert.Equal(["materials", "reason", "requestedBy"], data.EnumerateObject().Select(field => field.Name));
        var materials = data.GetProperty("materials").EnumerateArray().ToList();
        Assert.Equal(2, materials.Count);
        AssertRequestedMaterial(materials[0], cheese.Id, "Cheese", "kg", 20m);
        AssertRequestedMaterial(materials[1], starch.Id, "Potato starch", "kg", 0.5m);
        Assert.Equal(SpillReason, data.GetProperty("reason").GetString());
        AssertPerson(data.GetProperty("requestedBy"), lucia);

        // T-009, AC-14.2: the parent's detail lists it with its status; its lines' complementary sends are still 0.
        var parent = await ReadOrderAsync(manager, number);
        var linked = Assert.Single(parent.GetProperty("complementaryOrders").EnumerateArray());
        Assert.Equal(["number", "status", "requestedAt"], linked.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, linked.GetProperty("number").GetInt32());
        Assert.Equal("PendingApproval", linked.GetProperty("status").GetString());
        Assert.Equal("2026-11-11T11:20:00Z", linked.GetProperty("requestedAt").GetString());
        Assert.Equal(0m, Line(parent, cheese.Id).GetProperty("complementarySends").GetDecimal());

        // Architecture 6, Logging: the state change is logged with the numbers and the user.
        Assert.Contains(app.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Complementary order 1 requested for order 2 by user {lucia.Id}.");
    }

    /// <summary>
    /// PRD section 6 row 6, decision #14, AC-16.1, AC-17.1: a Manager can raise a request (PRD 4.2, "Production or
    /// Manager") and approve it themselves. Both get the expected status, and the history names the same Manager twice.
    /// </summary>
    [Fact]
    public async Task Manager_can_raise_a_request_and_approve_it_themselves()
    {
        var running = await factory.RunningAsync();

        var number = await RequestedComplementaryAsync(running.Manager, running.Number, SpillReason, (running.Cheese.Id, 5m));
        var response = await ApproveAsync(running.Manager, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        AssertPerson(body.GetProperty("requestedBy"), running.ManagerUser);
        AssertHistory(body, "PendingApproval", "Approved");
        foreach (var entry in body.GetProperty("history").EnumerateArray())
        {
            AssertPerson(entry.GetProperty("by"), running.ManagerUser);
        }
    }

    /// <summary>
    /// Design 2.4, 2.6, decision #80, AC-16.1 limits included: a reason of exactly 500 characters with spaces around it is
    /// accepted and stored as the 500 characters; a requested amount of 20.5000 (trailing zeros do not count) is accepted
    /// as 20.5; the largest amount, 999,999,999.999, is accepted and its send range is shown rounded to 3 places
    /// (979,999,999.999 to 1,019,999,999.999).
    /// </summary>
    [Fact]
    public async Task Limits_are_accepted()
    {
        var running = await factory.RunningAsync();
        var reason = new string('r', 500);

        var response = await RequestComplementaryAsync(running.Production, running.Number, $"  {reason}  ",
            (running.Cheese.Id, 20.5000m), (running.Starch.Id, 999_999_999.999m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(20.5m, lines[0].GetProperty("requestedAmount").GetDecimal());
        Assert.Equal(999_999_999.999m, lines[1].GetProperty("requestedAmount").GetDecimal());
        Assert.Equal(979_999_999.999m, lines[1].GetProperty("sendRangeMin").GetDecimal());
        Assert.Equal(1_019_999_999.999m, lines[1].GetProperty("sendRangeMax").GetDecimal());
    }

    // ---- AC-16.2: only while the parent is InProduction ----------------------------------------------------------------

    /// <summary>
    /// AC-16.2, design 7.2: a request for a Created, Completed or Cancelled order, with a valid body, is <c>409</c>
    /// <c>parent-not-in-production</c> "Order #N is {status}. Complementary orders can be raised only while the order is
    /// in production.". Nothing is saved: no complementary order and no notification.
    /// </summary>
    [Theory]
    [InlineData(OrderStatus.Created)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task Request_for_an_order_not_in_production_is_refused(OrderStatus status)
    {
        var running = await factory.RunningAsync();
        var (number, cheese, _) = await TwoLineOrderAsync(running.Manager, receive: false);
        if (status != OrderStatus.Created)
        {
            await SetStatusAsync(factory, number, status, running.ManagerUser.Id);
        }

        var awaitingBefore = await AwaitingApprovalCountAsync(factory);

        var response = await RequestComplementaryAsync(running.Production, number, SpillReason, (cheese.Id, 20m));

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "parent-not-in-production",
            $"Order #{number} is {status}. {OnlyInProduction}");
        Assert.Equal(0, await ComplementaryCountAsync(factory, number));
        Assert.Equal(awaitingBefore, await AwaitingApprovalCountAsync(factory));
    }

    // ---- AC-16.3: invalid requests ---------------------------------------------------------------------------------

    /// <summary>
    /// AC-16.3, design 2.6, 7.2, decisions #80, #95, #96: each invalid body gives <c>400</c> <c>validation</c> with exactly
    /// one message, under the field's JSON path. Reason missing, <c>null</c>, empty or only spaces: "Reason is required.";
    /// 501 characters: "Reason must be 500 characters or fewer.". Lines missing, <c>null</c> or empty: "At least one line is
    /// required.". An item missing: "Item is required."; a raw material not on the parent: "{item} is not on order #N.";
    /// an id that names no item: "Item 999999 is not on order #N."; the same item twice: "{item} appears on more than one
    /// line." under the second line. An amount missing or <c>null</c>: "Requested amount is required."; zero or less:
    /// "Requested amount must be greater than 0."; more than 3 decimal places: "Requested amount can have at most 3 decimal
    /// places."; above 999,999,999.999: "Requested amount must be 999,999,999.999 or less.". Nothing is saved.
    /// </summary>
    /// <remarks>
    /// In the templates, <c>{cheese}</c> and <c>{other}</c> are replaced by item ids, <c>{cheeseName}</c> and
    /// <c>{otherName}</c> by their names, <c>{n}</c> by the order number and <c>{long}</c> by 501 characters.
    /// </remarks>
    [Theory]
    [InlineData("""{ "lines": [ { "itemId": {cheese}, "requestedAmount": 20 } ] }""", "reason", "Reason is required.")]
    [InlineData("""{ "reason": null, "lines": [ { "itemId": {cheese}, "requestedAmount": 20 } ] }""", "reason", "Reason is required.")]
    [InlineData("""{ "reason": "", "lines": [ { "itemId": {cheese}, "requestedAmount": 20 } ] }""", "reason", "Reason is required.")]
    [InlineData("""{ "reason": "   ", "lines": [ { "itemId": {cheese}, "requestedAmount": 20 } ] }""", "reason", "Reason is required.")]
    [InlineData("""{ "reason": "{long}", "lines": [ { "itemId": {cheese}, "requestedAmount": 20 } ] }""", "reason", "Reason must be 500 characters or fewer.")]
    [InlineData("""{ "reason": "Spill" }""", "lines", "At least one line is required.")]
    [InlineData("""{ "reason": "Spill", "lines": null }""", "lines", "At least one line is required.")]
    [InlineData("""{ "reason": "Spill", "lines": [] }""", "lines", "At least one line is required.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "requestedAmount": 20 } ] }""", "lines[0].itemId", "Item is required.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {other}, "requestedAmount": 20 } ] }""", "lines[0].itemId", "{otherName} is not on order #{n}.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": 999999, "requestedAmount": 20 } ] }""", "lines[0].itemId", "Item 999999 is not on order #{n}.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": 20 }, { "itemId": {cheese}, "requestedAmount": 5 } ] }""", "lines[1].itemId", "{cheeseName} appears on more than one line.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese} } ] }""", "lines[0].requestedAmount", "Requested amount is required.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": null } ] }""", "lines[0].requestedAmount", "Requested amount is required.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": 0 } ] }""", "lines[0].requestedAmount", "Requested amount must be greater than 0.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": -1 } ] }""", "lines[0].requestedAmount", "Requested amount must be greater than 0.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": 20.0001 } ] }""", "lines[0].requestedAmount", "Requested amount can have at most 3 decimal places.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": 1000000000 } ] }""", "lines[0].requestedAmount", "Requested amount must be 999,999,999.999 or less.")]
    [InlineData("""{ "reason": "Spill", "lines": [ { "itemId": {cheese}, "requestedAmount": 999999999999999999999 } ] }""", "lines[0].requestedAmount", "Requested amount must be 999,999,999.999 or less.")]
    public async Task Invalid_request_is_rejected(string template, string path, string message)
    {
        var running = await factory.RunningAsync();
        string Fill(string text) => text
            .Replace("{long}", new string('r', 501), StringComparison.Ordinal)
            .Replace("{cheeseName}", running.Cheese.Name, StringComparison.Ordinal)
            .Replace("{otherName}", running.Other.Name, StringComparison.Ordinal)
            .Replace("{cheese}", running.Cheese.Id.ToString(), StringComparison.Ordinal)
            .Replace("{other}", running.Other.Id.ToString(), StringComparison.Ordinal)
            .Replace("{n}", running.Number.ToString(), StringComparison.Ordinal);
        var before = await ComplementaryCountAsync(factory, running.Number);
        var awaitingBefore = await AwaitingApprovalCountAsync(factory);

        var response = await RequestRawAsync(running.Production, running.Number, Fill(template));

        await ApiAssert.ValidationAsync(response, path, Fill(message));
        Assert.Equal(before, await ComplementaryCountAsync(factory, running.Number));
        Assert.Equal(awaitingBefore, await AwaitingApprovalCountAsync(factory));
    }

    /// <summary>
    /// AC-16.3, design 6, decisions #74, #75: every problem is reported in one <c>400</c>, one message per field, keyed by
    /// its JSON path, and <c>detail</c> joins them in body order: a blank reason, an amount of 0, a repeated item with too
    /// many decimals, and a line sent as <c>null</c> (which gets both "required" messages).
    /// </summary>
    [Fact]
    public async Task Every_problem_is_reported_in_one_response()
    {
        var running = await factory.RunningAsync();
        var cheese = running.Cheese;
        var json = $$"""
            { "reason": " ", "lines": [
                { "itemId": {{cheese.Id}}, "requestedAmount": 0 },
                { "itemId": {{cheese.Id}}, "requestedAmount": 1.0001 },
                null ] }
            """;

        var response = await RequestRawAsync(running.Production, running.Number, json);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"Reason is required. Requested amount must be greater than 0. {cheese.Name} appears on more than one line. " +
            "Requested amount can have at most 3 decimal places. Item is required. Requested amount is required.");
        Assert.Equal(["Reason is required."], ApiJson.Messages(body, "reason"));
        Assert.Equal(["Requested amount must be greater than 0."], ApiJson.Messages(body, "lines[0].requestedAmount"));
        Assert.Equal([$"{cheese.Name} appears on more than one line."], ApiJson.Messages(body, "lines[1].itemId"));
        Assert.Equal(["Requested amount can have at most 3 decimal places."], ApiJson.Messages(body, "lines[1].requestedAmount"));
        Assert.Equal(["Item is required."], ApiJson.Messages(body, "lines[2].itemId"));
        Assert.Equal(["Requested amount is required."], ApiJson.Messages(body, "lines[2].requestedAmount"));
        Assert.Equal(6, body.GetProperty("errors").EnumerateObject().Count());
    }

    /// <summary>
    /// Architecture 4.1 item 2: a missing body is <c>400</c> "A request body is required."; an amount sent as a string is
    /// <c>400</c> "lines[0].requestedAmount has the wrong type." (design 2.4). Nothing is saved.
    /// </summary>
    [Fact]
    public async Task Unreadable_bodies_are_rejected()
    {
        var running = await factory.RunningAsync();
        var before = await ComplementaryCountAsync(factory, running.Number);

        await ApiAssert.ProblemAsync(await RequestRawAsync(running.Production, running.Number, ""),
            HttpStatusCode.BadRequest, "validation", "A request body is required.");
        await ApiAssert.ValidationAsync(
            await RequestRawAsync(running.Production, running.Number,
                $$"""{ "reason": "Spill", "lines": [ { "itemId": {{running.Cheese.Id}}, "requestedAmount": "20" } ] }"""),
            "lines[0].requestedAmount", "lines[0].requestedAmount has the wrong type.");

        Assert.Equal(before, await ComplementaryCountAsync(factory, running.Number));
    }

    /// <summary>
    /// Design 2.2 check order: <c>403</c> before <c>404</c> (a Warehouse user asking about an unknown order); <c>404</c>
    /// "Order #999999 not found." before the body; the body (<c>400</c>) before the parent's state (<c>409</c>), so a
    /// Created order with a blank reason is <c>400</c> "Reason is required.", not <c>409</c>.
    /// </summary>
    [Fact]
    public async Task Checks_run_in_the_design_order()
    {
        var running = await factory.RunningAsync();
        var (created, cheese, _) = await TwoLineOrderAsync(running.Manager, receive: false);

        await ApiAssert.ProblemAsync(await RequestComplementaryAsync(running.Warehouse, 999999, null),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ProductionOrManager);
        await ApiAssert.ProblemAsync(await RequestComplementaryAsync(running.Production, 999999, null),
            HttpStatusCode.NotFound, "not-found", "Order #999999 not found.");
        await ApiAssert.ValidationAsync(await RequestComplementaryAsync(running.Production, created, "  ", (cheese.Id, 20m)),
            "reason", "Reason is required.");
    }

    // ---- AC-16.4: several complementary orders per parent ------------------------------------------------------------

    /// <summary>
    /// AC-16.4: one parent gets two complementary orders, with consecutive numbers of their own. Each has its own
    /// decision: the first is approved and the second rejected, and neither decision touches the other. The parent's
    /// detail lists both, oldest first, with their statuses (T-009), and <c>?orderNumber=</c> lists both, newest first.
    /// </summary>
    [Fact]
    public async Task A_parent_can_have_several_complementary_orders_each_decided_on_its_own()
    {
        var running = await factory.RunningAsync();

        var first = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        var second = await RequestedComplementaryAsync(running.Production, running.Number, "Bag line jam", (running.Cheese.Id, 10m));
        Assert.Equal(first + 1, second);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(running.Manager, first)).StatusCode);
        Assert.Equal("PendingApproval", (await ReadComplementaryAsync(running.Manager, second)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(running.Manager, second, null)).StatusCode);
        Assert.Equal("Approved", (await ReadComplementaryAsync(running.Manager, first)).GetProperty("status").GetString());

        var linked = (await ReadOrderAsync(running.Manager, running.Number)).GetProperty("complementaryOrders")
            .EnumerateArray().ToDictionary(entry => entry.GetProperty("number").GetInt32(), entry => entry.GetProperty("status").GetString());
        Assert.Equal("Approved", linked[first]);
        Assert.Equal("Rejected", linked[second]);
        var numbers = linked.Keys.ToList();
        Assert.Equal(numbers.Order(), numbers);

        var page = await ListAsync(running.Manager, $"orderNumber={running.Number}&pageSize=100");
        var listed = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("number").GetInt32()).ToList();
        Assert.Equal(listed.OrderDescending(), listed);
        Assert.Contains(first, listed);
        Assert.Contains(second, listed);
    }

    // ---- Lists and reads -------------------------------------------------------------------------------------------

    /// <summary>
    /// Design 2.5, 5.8, PRD 4.1, AC-21.4. On parent A: #1 at 09:00 (rejected), #2 at 10:00 (approved), #3 at 11:00
    /// (pending). A is cancelled at 12:00, so #2 and #3 become Cancelled with "Parent order cancelled", recorded against
    /// the cancelling Manager, with no send range; #1 stays Rejected. Parent B is sent and #4 requested at 13:00. The list
    /// is newest first (#4, #3, #2, #1, each in the full design 5.8 shape); <c>?status=</c> (any letter case) and
    /// <c>?orderNumber=</c> filter and combine with AND; an order with none gives an empty page; paging works; an unknown
    /// status, an order number that is not a number, and a bad page size are <c>400</c>.
    /// </summary>
    [Fact]
    public async Task List_is_newest_first_and_filters_by_status_and_order_number()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var (manager, managerUser) = await SignedInAsync(app, Roles.Manager);
        var (production, _) = await SignedInAsync(app, Roles.Production);
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        await ReceiveAsync(manager, cheese.Id, 2000m);
        var orderA = await CreateOrderAsync(manager, product.Id, 100m);
        var orderB = await CreateOrderAsync(manager, product.Id, 100m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, orderA, (cheese.Id, 100m))).StatusCode);

        async Task<int> RequestAtAsync(int order, int hour)
        {
            time.SetUtcNow(EightAm.AddHours(hour - 8));
            return await RequestedComplementaryAsync(production, order, SpillReason, (cheese.Id, 20m));
        }

        var c1 = await RequestAtAsync(orderA, 9);
        var c2 = await RequestAtAsync(orderA, 10);
        var c3 = await RequestAtAsync(orderA, 11);
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(manager, c1, "Use the spare from order #11")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(manager, c2)).StatusCode);
        time.SetUtcNow(EightAm.AddHours(4));
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(manager, orderA, "Line breakdown")).StatusCode);

        // A's 100 sent is free leftover once A is cancelled (AC-21.3), and covers all of B's plan, so B's send is 0 (AC-15.4).
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, orderB, (cheese.Id, 0m))).StatusCode);
        var c4 = await RequestAtAsync(orderB, 13);

        // AC-21.4 through the real endpoints: the unsent ones are cancelled, recorded against the cancelling Manager.
        foreach (var cancelled in new[] { c2, c3 })
        {
            var body = await ReadComplementaryAsync(production, cancelled);
            Assert.Equal("Cancelled", body.GetProperty("status").GetString());
            Assert.Equal("Parent order cancelled", body.GetProperty("cancellationNote").GetString());
            var last = body.GetProperty("history").EnumerateArray().Last();
            Assert.Equal("Cancelled", last.GetProperty("status").GetString());
            Assert.Equal("2026-11-11T12:00:00Z", last.GetProperty("at").GetString());
            AssertPerson(last.GetProperty("by"), managerUser);
            Assert.Equal("Parent order cancelled", last.GetProperty("note").GetString());
            Assert.All(body.GetProperty("lines").EnumerateArray(), line =>
            {
                Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMin").ValueKind);
                Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMax").ValueKind);
            });
        }

        AssertHistory(await ReadComplementaryAsync(production, c2), "PendingApproval", "Approved", "Cancelled");
        AssertHistory(await ReadComplementaryAsync(production, c3), "PendingApproval", "Cancelled");
        Assert.Equal("Rejected", (await ReadComplementaryAsync(production, c1)).GetProperty("status").GetString());

        var all = await ListAsync(production, "");
        Assert.Equal([c4, c3, c2, c1], Numbers(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        Assert.Equal((await ReadComplementaryAsync(production, c4)).GetRawText(), all.GetProperty("items")[0].GetRawText());

        Assert.Equal([c3, c2], Numbers(await ListAsync(production, "status=Cancelled")));
        Assert.Equal([c4], Numbers(await ListAsync(production, "status=pendingapproval")));
        Assert.Equal([c3, c2, c1], Numbers(await ListAsync(production, $"orderNumber={orderA}")));
        Assert.Equal([c1], Numbers(await ListAsync(production, $"orderNumber={orderA}&status=Rejected")));
        Assert.Empty(Numbers(await ListAsync(production, $"orderNumber={orderB}&status=Rejected")));
        var none = await ListAsync(production, "orderNumber=999999");
        Assert.Empty(Numbers(none));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
        var second = await ListAsync(production, "page=2&pageSize=1");
        Assert.Equal([c3], Numbers(second));
        Assert.Equal(4, second.GetProperty("totalCount").GetInt32());

        await ApiAssert.ValidationAsync(await production.GetAsync($"{ComplementaryPath}?status=Done", Cancel),
            "status", "status must be PendingApproval, Approved, Rejected, Sent or Cancelled.");
        var badNumber = await production.GetAsync($"{ComplementaryPath}?orderNumber=abc", Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, badNumber.StatusCode);
        Assert.Equal("validation", (await ApiJson.ReadAsync(badNumber)).GetProperty("code").GetString());
        await ApiAssert.ValidationAsync(await production.GetAsync($"{ComplementaryPath}?pageSize=0", Cancel),
            "pageSize", "pageSize must be between 1 and 100.");
    }

    /// <summary>Design 7.2: <c>GET</c>, approve and reject of an unknown number are <c>404</c> "Complementary order #999999 not found.".</summary>
    [Fact]
    public async Task Unknown_complementary_order_is_404()
    {
        var running = await factory.RunningAsync();
        const string message = "Complementary order #999999 not found.";

        await ApiAssert.ProblemAsync(await running.Production.GetAsync($"{ComplementaryPath}/999999", Cancel),
            HttpStatusCode.NotFound, "not-found", message);
        await ApiAssert.ProblemAsync(await ApproveAsync(running.Manager, 999999), HttpStatusCode.NotFound, "not-found", message);
        await ApiAssert.ProblemAsync(await RejectAsync(running.Manager, 999999, null), HttpStatusCode.NotFound, "not-found", message);
    }

    // ---- AC-17.1, AC-17.2: approve and reject -----------------------------------------------------------------------

    /// <summary>
    /// AC-17.1, AC-05.3, design 5.8: a Manager approves a PendingApproval request raised by a Production user. <c>200</c>:
    /// Approved, history PendingApproval (the requester) then Approved (the Manager, at the approval time), the send
    /// ranges still shown, no notes. The stored approver and time match, nothing is recorded as rejected, <c>GET</c>
    /// returns the same body, no movement is written and no notification is added. The approval is logged.
    /// </summary>
    [Fact]
    public async Task Manager_approves_a_pending_request()
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        running.Time.Advance(TimeSpan.FromMinutes(5));
        var approvedAt = running.Time.GetUtcNow().UtcDateTime;
        var movementsBefore = await MovementCountAsync(factory);
        var notificationsBefore = await NotificationCountAsync(factory);

        var response = await ApproveAsync(running.Manager, number);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        AssertShape(body);
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        AssertPerson(body.GetProperty("requestedBy"), running.ProductionUser);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("decisionNote").ValueKind);
        var history = body.GetProperty("history").EnumerateArray().ToList();
        AssertHistory(body, "PendingApproval", "Approved");
        AssertPerson(history[0].GetProperty("by"), running.ProductionUser);
        AssertPerson(history[1].GetProperty("by"), running.ManagerUser);
        Assert.Equal(approvedAt, history[1].GetProperty("at").GetDateTime().ToUniversalTime());
        AssertLine(Assert.Single(body.GetProperty("lines").EnumerateArray()),
            running.Cheese.Id, running.Cheese.Name, running.Cheese.UnitName, requested: 20m, min: 19.6m, max: 20.4m);

        var stored = await StoredComplementaryOrderAsync(factory, number);
        Assert.Equal(ComplementaryOrderStatus.Approved, stored.Status);
        Assert.Equal(approvedAt, stored.ApprovedAt);
        Assert.Equal(running.ManagerUser.Id, stored.ApprovedById);
        Assert.Null(stored.RejectedAt);
        Assert.Null(stored.RejectedById);

        Assert.Equal(body.GetRawText(), (await ReadComplementaryAsync(running.Warehouse, number)).GetRawText());
        Assert.Equal(movementsBefore, await MovementCountAsync(factory));
        Assert.Equal(notificationsBefore, await NotificationCountAsync(factory));
        Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Complementary order {number} approved by user {running.ManagerUser.Id}.");
    }

    /// <summary>
    /// AC-17.2, AC-05.3, design 5.8: a Manager rejects with the note "  Use the spare from order #11  ". <c>200</c>:
    /// Rejected, <c>decisionNote</c> trimmed, the history's Rejected entry by the Manager with the note, and no send range
    /// (it can never be sent). The stored decision matches and nothing is recorded as approved. It can never be sent: it
    /// can never be approved afterwards (<c>409</c> "Complementary order #N is Rejected. …"), and only an Approved one can
    /// be sent (AC-18.3, T-018). The rejection is logged.
    /// </summary>
    [Fact]
    public async Task Manager_rejects_a_pending_request_with_a_note()
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        running.Time.Advance(TimeSpan.FromMinutes(5));
        var rejectedAt = running.Time.GetUtcNow().UtcDateTime;

        var response = await RejectAsync(running.Manager, number, "  Use the spare from order #11  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        AssertShape(body);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal("Use the spare from order #11", body.GetProperty("decisionNote").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancellationNote").ValueKind);
        AssertHistory(body, "PendingApproval", "Rejected");
        var rejected = body.GetProperty("history")[1];
        AssertPerson(rejected.GetProperty("by"), running.ManagerUser);
        Assert.Equal(rejectedAt, rejected.GetProperty("at").GetDateTime().ToUniversalTime());
        Assert.Equal("Use the spare from order #11", rejected.GetProperty("note").GetString());
        var line = Assert.Single(body.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sendRangeMax").ValueKind);

        var stored = await StoredComplementaryOrderAsync(factory, number);
        Assert.Equal(ComplementaryOrderStatus.Rejected, stored.Status);
        Assert.Equal(rejectedAt, stored.RejectedAt);
        Assert.Equal(running.ManagerUser.Id, stored.RejectedById);
        Assert.Null(stored.ApprovedAt);
        Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message == $"Complementary order {number} rejected by user {running.ManagerUser.Id}.");

        await ApiAssert.ProblemAsync(await ApproveAsync(running.Manager, number), HttpStatusCode.Conflict,
            "not-pending-approval", $"Complementary order #{number} is Rejected. {OnlyPending}");
        Assert.Equal(ComplementaryOrderStatus.Rejected, (await StoredComplementaryOrderAsync(factory, number)).Status);
    }

    /// <summary>
    /// AC-17.2 ("an optional note"), design 2.6, 5.8: no body at all, an empty object, a <c>null</c> note and an all-spaces
    /// note each reject the request with no note (<c>decisionNote</c> null, history note null).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ }")]
    [InlineData("""{ "note": null }""")]
    [InlineData("""{ "note": "   " }""")]
    public async Task Reject_without_a_note(string json)
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));

        var response = await RejectRawAsync(running.Manager, number, json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("decisionNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("history")[1].GetProperty("note").ValueKind);
        Assert.Null((await StoredComplementaryOrderAsync(factory, number)).DecisionNote);
    }

    /// <summary>
    /// Design 2.6, 7.2 ("Note or reason over 500"): a note of 501 characters is <c>400</c> <c>validation</c> "Note must be
    /// 500 characters or fewer." under <c>note</c>, and a note sent as a number is "note has the wrong type."; the request
    /// stays PendingApproval. Exactly 500 characters, with spaces around them, is accepted and stored as the 500.
    /// </summary>
    [Fact]
    public async Task Rejection_note_is_at_most_500_characters()
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));

        await ApiAssert.ValidationAsync(await RejectAsync(running.Manager, number, new string('n', 501)),
            "note", "Note must be 500 characters or fewer.");
        await ApiAssert.ValidationAsync(await RejectRawAsync(running.Manager, number, """{ "note": 5 }"""),
            "note", "note has the wrong type.");
        Assert.Equal(ComplementaryOrderStatus.PendingApproval, (await StoredComplementaryOrderAsync(factory, number)).Status);

        var response = await RejectAsync(running.Manager, number, $"  {new string('n', 500)}  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new string('n', 500), (await StoredComplementaryOrderAsync(factory, number)).DecisionNote);
    }

    /// <summary>
    /// Design 2.2 for reject: the unknown number (<c>404</c>) comes before a too-long note, and the note (<c>400</c>)
    /// comes before the state, so an Approved request with a 501-character note is <c>400</c>, not <c>409</c>.
    /// </summary>
    [Fact]
    public async Task Reject_checks_run_in_the_design_order()
    {
        var running = await factory.RunningAsync();
        var approved = await InsertComplementaryOrderAsync(
            factory, running.Number, ComplementaryOrderStatus.Approved, running.Cheese.Id, running.ManagerUser.Id);
        var tooLong = new string('n', 501);

        await ApiAssert.ProblemAsync(await RejectAsync(running.Manager, 999999, tooLong),
            HttpStatusCode.NotFound, "not-found", "Complementary order #999999 not found.");
        await ApiAssert.ValidationAsync(await RejectAsync(running.Manager, approved, tooLong),
            "note", "Note must be 500 characters or fewer.");
    }

    // ---- AC-17.3: only PendingApproval can be decided ----------------------------------------------------------------

    /// <summary>
    /// AC-17.3, design 7.2: approving or rejecting a complementary order that is Approved, Rejected, Sent or Cancelled is
    /// <c>409</c> <c>not-pending-approval</c> "Complementary order #N is {status}. Only a PendingApproval complementary
    /// order can be approved or rejected.", and it is left exactly as it was.
    /// </summary>
    [Theory]
    [InlineData(ComplementaryOrderStatus.Approved)]
    [InlineData(ComplementaryOrderStatus.Rejected)]
    [InlineData(ComplementaryOrderStatus.Sent)]
    [InlineData(ComplementaryOrderStatus.Cancelled)]
    public async Task Only_a_pending_request_can_be_approved_or_rejected(ComplementaryOrderStatus status)
    {
        var running = await factory.RunningAsync();
        var number = await InsertComplementaryOrderAsync(
            factory, running.Number, status, running.Cheese.Id, running.ProductionUser.Id);
        var before = await StoredComplementaryOrderAsync(factory, number);
        var expected = $"Complementary order #{number} is {status}. {OnlyPending}";

        await ApiAssert.ProblemAsync(await ApproveAsync(running.Manager, number),
            HttpStatusCode.Conflict, "not-pending-approval", expected);
        await ApiAssert.ProblemAsync(await RejectAsync(running.Manager, number, "Too late"),
            HttpStatusCode.Conflict, "not-pending-approval", expected);

        var after = await StoredComplementaryOrderAsync(factory, number);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.ApprovedById, after.ApprovedById);
        Assert.Equal(before.RejectedById, after.RejectedById);
        Assert.Null(after.DecisionNote);
    }

    // ---- Keeping AC-19.6 and AC-21.4 working with real requests -----------------------------------------------------

    /// <summary>
    /// AC-19.6, PRD section 6 row 6, through the real endpoints: three requests on one running order, one approved, one
    /// rejected, one left pending. The Production user completes the order. The pending and the approved ones become
    /// Cancelled with "Parent order completed", recorded against the completing user and time, with no send range; the
    /// rejected one is unchanged. The completion's response lists the new statuses. A cancelled one can no longer be
    /// approved (<c>409</c> "… is Cancelled. …"), and the completed order takes no new request (<c>409</c> "Order #N is
    /// Completed. …").
    /// </summary>
    [Fact]
    public async Task Completing_the_parent_cancels_its_pending_and_approved_requests()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var app = new TestApiFactory { FakeTime = time };
        var scene = await RaceSceneAsync(app, send: true);
        var pending = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));
        var approved = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));
        var rejected = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(scene.Manager, approved)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(scene.Manager, rejected, null)).StatusCode);
        time.SetUtcNow(EightAm.AddHours(5));

        var response = await CompleteAsync(scene.Production, scene.Number, 1000m, null, (scene.Cheese.Id, 0m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Cancelled", "Cancelled", "Rejected"],
            (await ApiJson.ReadAsync(response)).GetProperty("complementaryOrders").EnumerateArray()
                .Select(entry => entry.GetProperty("status").GetString()));
        foreach (var number in new[] { pending, approved })
        {
            var body = await ReadComplementaryAsync(scene.Manager, number);
            Assert.Equal("Cancelled", body.GetProperty("status").GetString());
            Assert.Equal("Parent order completed", body.GetProperty("cancellationNote").GetString());
            var last = body.GetProperty("history").EnumerateArray().Last();
            Assert.Equal("2026-11-11T13:00:00Z", last.GetProperty("at").GetString());
            AssertPerson(last.GetProperty("by"), scene.ProductionUser);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("lines")[0].GetProperty("sendRangeMin").ValueKind);
        }

        AssertHistory(await ReadComplementaryAsync(scene.Manager, pending), "PendingApproval", "Cancelled");
        AssertHistory(await ReadComplementaryAsync(scene.Manager, approved), "PendingApproval", "Approved", "Cancelled");
        AssertHistory(await ReadComplementaryAsync(scene.Manager, rejected), "PendingApproval", "Rejected");

        await ApiAssert.ProblemAsync(await ApproveAsync(scene.Manager, pending), HttpStatusCode.Conflict,
            "not-pending-approval", $"Complementary order #{pending} is Cancelled. {OnlyPending}");
        await ApiAssert.ProblemAsync(
            await RequestComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m)),
            HttpStatusCode.Conflict, "parent-not-in-production", $"Order #{scene.Number} is Completed. {OnlyInProduction}");
    }

    // ---- Concurrency (PRD section 5, architecture 5.2) -------------------------------------------------------------

    /// <summary>
    /// Architecture 5.2, AC-17.3: two Managers approve the same request at the same moment. Exactly one gets <c>200</c>;
    /// the other gets <c>409</c> "Complementary order #N is Approved. …". The stored approver is the winner. Three rounds.
    /// </summary>
    [Fact]
    public async Task Simultaneous_approvals_succeed_once()
    {
        var running = await factory.RunningAsync();
        var (first, firstUser) = await SignedInAsync(factory, Roles.Manager);
        var (second, secondUser) = await SignedInAsync(factory, Roles.Manager);

        for (var round = 0; round < 3; round++)
        {
            var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));

            var responses = await Task.WhenAll(ApproveAsync(first, number), ApproveAsync(second, number));

            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await ApiAssert.ProblemAsync(responses[1 - winner], HttpStatusCode.Conflict, "not-pending-approval",
                $"Complementary order #{number} is Approved. {OnlyPending}");
            Assert.Equal(winner == 0 ? firstUser.Id : secondUser.Id, (await StoredComplementaryOrderAsync(factory, number)).ApprovedById);
        }
    }

    /// <summary>
    /// Architecture 5.2, AC-17.3: one Manager approves and another rejects the same request at the same moment. Exactly
    /// one gets <c>200</c>; the other gets <c>409</c> naming the winner's decision. The stored status is the winner's, and
    /// only the winner's who-and-when pair is filled. Four rounds.
    /// </summary>
    [Fact]
    public async Task Simultaneous_approve_and_reject_decide_once()
    {
        var running = await factory.RunningAsync();
        var (approver, _) = await SignedInAsync(factory, Roles.Manager);
        var (rejecter, _) = await SignedInAsync(factory, Roles.Manager);

        for (var round = 0; round < 4; round++)
        {
            var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));

            var responses = await Task.WhenAll(ApproveAsync(approver, number), RejectAsync(rejecter, number, "No"));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var stored = await StoredComplementaryOrderAsync(factory, number);
            if (responses[0].StatusCode == HttpStatusCode.OK)
            {
                await ApiAssert.ProblemAsync(responses[1], HttpStatusCode.Conflict, "not-pending-approval",
                    $"Complementary order #{number} is Approved. {OnlyPending}");
                Assert.Equal(ComplementaryOrderStatus.Approved, stored.Status);
                Assert.Null(stored.RejectedAt);
                Assert.Null(stored.DecisionNote);
            }
            else
            {
                await ApiAssert.ProblemAsync(responses[0], HttpStatusCode.Conflict, "not-pending-approval",
                    $"Complementary order #{number} is Rejected. {OnlyPending}");
                Assert.Equal(ComplementaryOrderStatus.Rejected, stored.Status);
                Assert.Null(stored.ApprovedAt);
            }
        }
    }

    /// <summary>
    /// Architecture 5.2, forced orders "approve, then reject" and "reject, then approve": the decision arriving while the
    /// other holds the write lock waits, then gets <c>409</c> naming the first one's decision. Both branches are shown
    /// deterministically (the simultaneous test above cannot choose which one it exercises).
    /// </summary>
    [Fact]
    public async Task Decision_waiting_behind_another_decision_is_refused()
    {
        await using var race = new ForcedOrder("ComplementaryOrders");
        var scene = await RaceSceneAsync(race.App, send: true);
        var first = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));
        var second = await RequestedComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m));

        var (approve, reject) = await race.RunAsync(
            () => ApproveAsync(scene.Manager, first), () => RejectAsync(scene.Manager, first, "No"));

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        await ApiAssert.ProblemAsync(reject, HttpStatusCode.Conflict, "not-pending-approval",
            $"Complementary order #{first} is Approved. {OnlyPending}");
        Assert.Equal(ComplementaryOrderStatus.Approved, (await StoredComplementaryOrderAsync(race.App, first)).Status);

        await using var race2 = new ForcedOrder("ComplementaryOrders");
        var scene2 = await RaceSceneAsync(race2.App, send: true);
        var number = await RequestedComplementaryAsync(scene2.Production, scene2.Number, SpillReason, (scene2.Cheese.Id, 20m));

        var (rejectFirst, approveSecond) = await race2.RunAsync(
            () => RejectAsync(scene2.Manager, number, "No"), () => ApproveAsync(scene2.Manager, number));

        Assert.Equal(HttpStatusCode.OK, rejectFirst.StatusCode);
        await ApiAssert.ProblemAsync(approveSecond, HttpStatusCode.Conflict, "not-pending-approval",
            $"Complementary order #{number} is Rejected. {OnlyPending}");
        Assert.Equal(ComplementaryOrderStatus.Rejected, (await StoredComplementaryOrderAsync(race2.App, number)).Status);
        Assert.Equal(ComplementaryOrderStatus.PendingApproval, (await StoredComplementaryOrderAsync(race.App, second)).Status);
    }

    /// <summary>
    /// Design 2.1, architecture 5.2: eight requests for the same running order, from eight Production users at the same
    /// moment, all get <c>201</c> with different, consecutive numbers. Each has exactly one ComplementaryAwaitingApproval
    /// notification naming its own number, and the stored requester of each is the user who sent it.
    /// </summary>
    [Fact]
    public async Task Simultaneous_requests_get_unique_numbers()
    {
        var running = await factory.RunningAsync();
        var users = new List<(HttpClient Client, AppUser User)>();
        for (var i = 0; i < 8; i++)
        {
            users.Add(await SignedInAsync(factory, Roles.Production));
        }

        var responses = await Task.WhenAll(users.Select(user =>
            RequestComplementaryAsync(user.Client, running.Number, SpillReason, (running.Cheese.Id, 20m))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(ApiJson.ReadAsync));
        var numbers = bodies.Select(body => body.GetProperty("number").GetInt32()).ToList();
        Assert.Equal(8, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(numbers.Min(), 8), numbers.Order());
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(users[i].User.Id, (await StoredComplementaryOrderAsync(factory, numbers[i])).RequestedById);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var notified = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking()
            .Where(notification => notification.Type == NotificationType.ComplementaryAwaitingApproval
                && notification.ComplementaryOrderNumber != null && numbers.Contains(notification.ComplementaryOrderNumber.Value))
            .Select(notification => notification.ComplementaryOrderNumber!.Value)
            .ToListAsync(Cancel);
        Assert.Equal(numbers.Order(), notified.Order());
    }

    /// <summary>
    /// PRD section 5, architecture 5.2, AC-16.2, AC-19.6: a request and the completion of its parent at the same moment
    /// end in one consistent outcome. The completion always succeeds. If the request ran first (<c>201</c>), the
    /// completion cancelled it: it is stored Cancelled with "Parent order completed", and its notification exists (the
    /// request did happen). Otherwise the request got <c>409</c> "Order #N is Completed. …" and left nothing: no
    /// complementary order and no notification for the order. Either way no open request is left on a completed order.
    /// Four rounds with new materials.
    /// </summary>
    [Fact]
    public async Task Request_racing_the_completion_of_its_parent_ends_consistently()
    {
        await using var app = new TestApiFactory();
        var (manager, _) = await SignedInAsync(app, Roles.Manager);
        var (requester, _) = await SignedInAsync(app, Roles.Production);
        var (completer, _) = await SignedInAsync(app, Roles.Production);

        for (var round = 0; round < 4; round++)
        {
            var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
            await ReceiveAsync(manager, cheese.Id, 1000m);
            var number = await CreateOrderAsync(manager, product.Id, 1000m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, number, (cheese.Id, 1000m))).StatusCode);

            var responses = await Task.WhenAll(
                RequestComplementaryAsync(requester, number, SpillReason, (cheese.Id, 20m)),
                CompleteAsync(completer, number, 1000m, null, (cheese.Id, 0m)));

            var (request, complete) = (responses[0], responses[1]);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            var notifications = await NotificationsOfOrderAsync(app, number);
            if (request.StatusCode == HttpStatusCode.Created)
            {
                var complementary = (await ApiJson.ReadAsync(request)).GetProperty("number").GetInt32();
                var stored = await StoredComplementaryOrderAsync(app, complementary);
                Assert.Equal(ComplementaryOrderStatus.Cancelled, stored.Status);
                Assert.Equal("Parent order completed", stored.CancellationNote);
                Assert.Equal(NotificationType.ComplementaryAwaitingApproval, Assert.Single(notifications).Type);
            }
            else
            {
                await ApiAssert.ProblemAsync(request, HttpStatusCode.Conflict, "parent-not-in-production",
                    $"Order #{number} is Completed. {OnlyInProduction}");
                Assert.Equal(0, await ComplementaryCountAsync(app, number));
                Assert.Empty(notifications);
            }

            Assert.Equal(0, await OpenComplementaryCountAsync(app, number));
        }
    }

    /// <summary>
    /// Architecture 5.2, AC-19.6, forced order "request, then complete": a completion arriving while the request holds the
    /// write lock waits, then cancels the new request. The request got <c>201</c> PendingApproval; the completion's
    /// response lists it as Cancelled; it is stored Cancelled with "Parent order completed".
    /// </summary>
    [Fact]
    public async Task Completion_waiting_behind_a_request_cancels_it()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (request, complete) = await race.RunAsync(
            () => RequestComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m)),
            () => CompleteAsync(scene.Manager, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)));

        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        var requested = await ApiJson.ReadAsync(request);
        var number = requested.GetProperty("number").GetInt32();
        Assert.Equal("PendingApproval", requested.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var linked = Assert.Single((await ApiJson.ReadAsync(complete)).GetProperty("complementaryOrders").EnumerateArray());
        Assert.Equal(number, linked.GetProperty("number").GetInt32());
        Assert.Equal("Cancelled", linked.GetProperty("status").GetString());
        Assert.Equal("Parent order completed", (await StoredComplementaryOrderAsync(race.App, number)).CancellationNote);
    }

    /// <summary>
    /// Architecture 5.2, AC-16.2, forced order "complete, then request": a request arriving while the completion holds the
    /// write lock waits, then gets <c>409</c> "Order #N is Completed. …", and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Request_waiting_behind_a_completion_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (complete, request) = await race.RunAsync(
            () => CompleteAsync(scene.Manager, scene.Number, 1000m, null, (scene.Cheese.Id, 0m)),
            () => RequestComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await ApiAssert.ProblemAsync(request, HttpStatusCode.Conflict, "parent-not-in-production",
            $"Order #{scene.Number} is Completed. {OnlyInProduction}");
        Assert.Equal(0, await ComplementaryCountAsync(race.App, scene.Number));
        Assert.Equal(0, await AwaitingApprovalCountAsync(race.App));
    }

    /// <summary>
    /// Architecture 5.2, AC-21.4, forced order "request, then cancel": a cancellation arriving while the request holds the
    /// write lock waits, then cancels the new request with "Parent order cancelled"; its response lists it as Cancelled.
    /// </summary>
    [Fact]
    public async Task Cancellation_waiting_behind_a_request_cancels_it()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (request, cancel) = await race.RunAsync(
            () => RequestComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m)),
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"));

        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        var number = (await ApiJson.ReadAsync(request)).GetProperty("number").GetInt32();
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var linked = Assert.Single((await ApiJson.ReadAsync(cancel)).GetProperty("complementaryOrders").EnumerateArray());
        Assert.Equal("Cancelled", linked.GetProperty("status").GetString());
        Assert.Equal("Parent order cancelled", (await StoredComplementaryOrderAsync(race.App, number)).CancellationNote);
    }

    /// <summary>
    /// Architecture 5.2, AC-16.2, forced order "cancel, then request": a request arriving while the cancellation holds the
    /// write lock waits, then gets <c>409</c> "Order #N is Cancelled. …", and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Request_waiting_behind_a_cancellation_is_refused()
    {
        await using var race = new ForcedOrder();
        var scene = await RaceSceneAsync(race.App, send: true);

        var (cancel, request) = await race.RunAsync(
            () => CancelAsync(scene.Manager, scene.Number, "Line breakdown"),
            () => RequestComplementaryAsync(scene.Production, scene.Number, SpillReason, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await ApiAssert.ProblemAsync(request, HttpStatusCode.Conflict, "parent-not-in-production",
            $"Order #{scene.Number} is Cancelled. {OnlyInProduction}");
        Assert.Equal(0, await ComplementaryCountAsync(race.App, scene.Number));
        Assert.Equal(0, await AwaitingApprovalCountAsync(race.App));
    }

    // ---- Access (PRD 4.2) -----------------------------------------------------------------------------------------

    /// <summary>AC-01.5: every complementary order endpoint without a token is <c>401</c> <c>not-authenticated</c>.</summary>
    [Fact]
    public async Task Every_endpoint_without_a_token_is_401()
    {
        var running = await factory.RunningAsync();
        using var anonymous = factory.CreateClient();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));

        await ApiAssert.NotAuthenticatedAsync(
            await RequestComplementaryAsync(anonymous, running.Number, SpillReason, (running.Cheese.Id, 20m)));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync(ComplementaryPath, Cancel));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync($"{ComplementaryPath}/{number}", Cancel));
        await ApiAssert.NotAuthenticatedAsync(await ApproveAsync(anonymous, number));
        await ApiAssert.NotAuthenticatedAsync(await RejectAsync(anonymous, number, null));
        Assert.Equal(ComplementaryOrderStatus.PendingApproval, (await StoredComplementaryOrderAsync(factory, number)).Status);
    }

    /// <summary>
    /// AC-16.6, AC-05.1, PRD 4.2 ("Request a complementary order": Production or Manager): a Warehouse-only user gets
    /// <c>403</c> "It needs the Production or Manager role." for the running order with a valid body, and nothing is
    /// saved. A user with Warehouse and Production may request (AC-05.2).
    /// </summary>
    [Fact]
    public async Task Warehouse_only_user_cannot_request()
    {
        var running = await factory.RunningAsync();
        var before = await ComplementaryCountAsync(factory, running.Number);

        await ApiAssert.ProblemAsync(
            await RequestComplementaryAsync(running.Warehouse, running.Number, SpillReason, (running.Cheese.Id, 20m)),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ProductionOrManager);
        Assert.Equal(before, await ComplementaryCountAsync(factory, running.Number));

        using var both = await factory.CreateClientAsAsync(Roles.Warehouse, Roles.Production);
        Assert.Equal(HttpStatusCode.Created,
            (await RequestComplementaryAsync(both, running.Number, SpillReason, (running.Cheese.Id, 20m))).StatusCode);
    }

    /// <summary>
    /// AC-17.4, AC-05.1, PRD 4.2 ("Approve or reject a complementary order": Manager only): a Warehouse-only user, a
    /// Production-only user and a user with both get <c>403</c> "It needs the Manager role." on approve and reject, for a
    /// real PendingApproval request and for an unknown number (the role is checked first, design 2.2). The request stays
    /// PendingApproval.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Warehouse, Roles.Production)]
    public async Task Users_without_the_manager_role_cannot_approve_or_reject(params string[] roles)
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        using var client = await factory.CreateClientAsAsync(roles);

        foreach (var target in new[] { number, 999999 })
        {
            await ApiAssert.ProblemAsync(await ApproveAsync(client, target),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
            await ApiAssert.ProblemAsync(await RejectAsync(client, target, null),
                HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
        }

        Assert.Equal(ComplementaryOrderStatus.PendingApproval, (await StoredComplementaryOrderAsync(factory, number)).Status);
    }

    /// <summary>
    /// PRD 4.2 ("View … complementary orders": every role): a Warehouse-only, a Production-only and a Manager user can
    /// list complementary orders and read one.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_can_list_and_read(string role)
    {
        var running = await factory.RunningAsync();
        var number = await RequestedComplementaryAsync(running.Production, running.Number, SpillReason, (running.Cheese.Id, 20m));
        using var client = await factory.CreateClientAsAsync(role);

        Assert.Contains(number, Numbers(await ListAsync(client, "pageSize=100")));
        Assert.Equal(number, (await ReadComplementaryAsync(client, number)).GetProperty("number").GetInt32());
    }

    // ---- Fixture and helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// The class's shared app: a <see cref="TestApiFactory"/> on a fake clock (08:00) that holds one InProduction order,
    /// set up once, the first time a test asks for it.
    /// </summary>
    /// <remarks>
    /// xUnit creates one instance of a class fixture for all the tests of the class, and runs those tests one after
    /// another, so the lazily created order is shared safely. A subclass of the factory, rather than a second fixture,
    /// because the order belongs to this factory's database.
    /// </remarks>
    public sealed class RunningOrderApp : TestApiFactory
    {
        private Task<RunningOrder>? _running;

        /// <summary>Creates the app with its fake clock; <c>FakeTime</c> is an <c>init</c> property, settable here.</summary>
        public RunningOrderApp() => FakeTime = new FakeTimeProvider(EightAm);

        /// <summary>The running order, set up on the first call.</summary>
        public Task<RunningOrder> RunningAsync() => _running ??= SetUpAsync();

        /// <summary>
        /// A Manager, a Production and a Warehouse user; a two-line order (see <see cref="OrderFlow.TwoLineOrderAsync"/>)
        /// sent in full, so it is InProduction; and one more raw material that is not on it.
        /// </summary>
        private async Task<RunningOrder> SetUpAsync()
        {
            var (manager, managerUser) = await SignedInAsync(this, Roles.Manager);
            var (production, productionUser) = await SignedInAsync(this, Roles.Production);
            var (warehouse, _) = await SignedInAsync(this, Roles.Warehouse);
            var (number, cheese, starch) = await TwoLineOrderAsync(manager);
            var other = await Seed.CreateItemAsync(manager, ItemType.RawMaterial);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1000m), (starch.Id, 15m))).StatusCode);
            return new RunningOrder(FakeTime!, manager, managerUser, production, productionUser, warehouse, number, cheese, starch, other);
        }
    }

    /// <summary>What <see cref="RunningOrderApp"/> set up.</summary>
    public sealed record RunningOrder(
        FakeTimeProvider Time,
        HttpClient Manager,
        AppUser ManagerUser,
        HttpClient Production,
        AppUser ProductionUser,
        HttpClient Warehouse,
        int Number,
        CreatedItem Cheese,
        CreatedItem Starch,
        CreatedItem Other);

    /// <summary>The request under test with exactly this JSON body (an empty string sends no body).</summary>
    private static Task<HttpResponseMessage> RequestRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{OrdersPath}/{number}/complementary-orders", ApiJson.Content(json), Cancel);

    /// <summary>The rejection under test with exactly this JSON body; an empty string sends no body at all.</summary>
    private static Task<HttpResponseMessage> RejectRawAsync(HttpClient client, int number, string json) =>
        client.PostAsync($"{ComplementaryPath}/{number}/reject", json.Length == 0 ? null : ApiJson.Content(json), Cancel);

    /// <summary><c>GET /complementary-orders?{query}</c>, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{ComplementaryPath}?{query}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>The numbers in a page of complementary orders, in order.</summary>
    private static List<int> Numbers(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("number").GetInt32()).ToList();

    /// <summary>The first page of <c>GET /api/v1/notifications</c> (T-012) for the calling Manager, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> NotificationsPageAsync(HttpClient manager)
    {
        var response = await manager.GetAsync("/api/v1/notifications", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>Runs one count query against <paramref name="app"/>'s database in a new scope.</summary>
    private static async Task<int> CountAsync(TestApiFactory app, Func<AppDbContext, Task<int>> count)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await count(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>How many complementary orders order <paramref name="parent"/> has.</summary>
    private static Task<int> ComplementaryCountAsync(TestApiFactory app, int parent) =>
        CountAsync(app, db => db.ComplementaryOrders.CountAsync(complementary => complementary.ParentOrderNumber == parent, Cancel));

    /// <summary>How many of order <paramref name="parent"/>'s complementary orders are still PendingApproval or Approved.</summary>
    private static Task<int> OpenComplementaryCountAsync(TestApiFactory app, int parent) =>
        CountAsync(app, db => db.ComplementaryOrders.CountAsync(complementary => complementary.ParentOrderNumber == parent
            && (complementary.Status == ComplementaryOrderStatus.PendingApproval
                || complementary.Status == ComplementaryOrderStatus.Approved), Cancel));

    /// <summary>How many ComplementaryAwaitingApproval notifications exist.</summary>
    private static Task<int> AwaitingApprovalCountAsync(TestApiFactory app) =>
        CountAsync(app, db => db.Notifications.CountAsync(
            notification => notification.Type == NotificationType.ComplementaryAwaitingApproval, Cancel));

    /// <summary>How many notifications exist (approve and reject raise none, AC-23.2).</summary>
    private static Task<int> NotificationCountAsync(TestApiFactory app) =>
        CountAsync(app, db => db.Notifications.CountAsync(Cancel));

    /// <summary>How many movements exist (T-017 writes none; only T-018's send moves material).</summary>
    private static Task<int> MovementCountAsync(TestApiFactory app) =>
        CountAsync(app, db => db.Movements.CountAsync(Cancel));

    /// <summary>
    /// Design 5.8, AC-16.5: exactly the design's fields, in order, at the top level, on each line and on each history
    /// entry. There is no ordered or produced quantity.
    /// </summary>
    private static void AssertShape(JsonElement body)
    {
        Assert.Equal(
            ["number", "orderNumber", "status", "reason", "requestedAt", "requestedBy", "decisionNote", "cancellationNote", "lines", "history"],
            body.EnumerateObject().Select(field => field.Name));
        Assert.All(body.GetProperty("lines").EnumerateArray(), line => Assert.Equal(
            ["itemId", "itemName", "unit", "requestedAmount", "sendRangeMin", "sendRangeMax", "sentQuantity"],
            line.EnumerateObject().Select(field => field.Name)));
        Assert.All(body.GetProperty("history").EnumerateArray(), entry => Assert.Equal(
            ["status", "at", "by", "note"], entry.EnumerateObject().Select(field => field.Name)));
    }

    /// <summary>Design 5.8: one line with these values, a send range (AC-18.1 at 2%) and no sent quantity yet.</summary>
    private static void AssertLine(
        JsonElement line, int itemId, string itemName, string unit, decimal requested, decimal min, decimal max)
    {
        Assert.Equal(itemId, line.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, line.GetProperty("itemName").GetString());
        Assert.Equal(unit, line.GetProperty("unit").GetString());
        Assert.Equal(requested, line.GetProperty("requestedAmount").GetDecimal());
        Assert.Equal(min, line.GetProperty("sendRangeMin").GetDecimal());
        Assert.Equal(max, line.GetProperty("sendRangeMax").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sentQuantity").ValueKind);
    }

    /// <summary>Design 5.10: one entry of a ComplementaryAwaitingApproval notification's <c>materials</c>, exactly four fields.</summary>
    private static void AssertRequestedMaterial(JsonElement material, int itemId, string itemName, string unit, decimal amount)
    {
        Assert.Equal(["itemId", "itemName", "unit", "requestedAmount"], material.EnumerateObject().Select(field => field.Name));
        Assert.Equal(itemId, material.GetProperty("itemId").GetInt32());
        Assert.Equal(itemName, material.GetProperty("itemName").GetString());
        Assert.Equal(unit, material.GetProperty("unit").GetString());
        Assert.Equal(amount, material.GetProperty("requestedAmount").GetDecimal());
    }

    /// <summary>Design 2.3 "People": <c>{ id, email }</c> of <paramref name="user"/>.</summary>
    private static void AssertPerson(JsonElement person, AppUser user)
    {
        Assert.Equal(["id", "email"], person.EnumerateObject().Select(field => field.Name));
        Assert.Equal(user.Id, person.GetProperty("id").GetString());
        Assert.Equal(user.Email, person.GetProperty("email").GetString());
    }

    /// <summary>AC-05.3, design 5.8: the statuses of the history, oldest first.</summary>
    private static void AssertHistory(JsonElement body, params string[] statuses) =>
        Assert.Equal(statuses, body.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("status").GetString()));
}
