using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.Notifications;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-012: the notifications API, <c>GET /api/v1/notifications</c>, <c>GET /api/v1/notifications/{id}</c>,
/// <c>POST /api/v1/notifications/{id}/read</c> and <c>POST /api/v1/notifications/read-all</c> (US-23, design 3.11,
/// 5.10, 7.2).
/// </summary>
/// <remarks>
/// <para>
/// Most tests read one fixed history of five notifications, one of each type, written once for the class by
/// <see cref="WriteHistoryAsync"/> through <see cref="NotificationComposer"/>, the same code the events use. Read
/// status is per Manager, so each test signs in Managers of its own: what one test marks read cannot change what
/// another test's Managers see, and the shared history never changes. Tests that add notifications or need a fake
/// clock create their own app with the same history. One test (<see cref="Thin_slice_completion_raises_notifications_that_a_Manager_reads_and_marks"/>)
/// raises real notifications by completing an order through the API.
/// </para>
/// <para>
/// The history, newest first (the order <c>GET /notifications</c> must return):
/// </para>
/// <code>
/// name            created (UTC)         type                           order  complementary
/// CancelledB      2026-11-11 12:00:00   OrderCancelledWithMaterial     B
/// LeftoverA       2026-11-11 10:00:00   LeftoverAtCompletion           A      (three in the same second, as one
/// OutputA         2026-11-11 10:00:00   OutputVariance                 A       completion raises them: the id
/// UsageA          2026-11-11 10:00:00   UsageVariance                  A       breaks the tie, newest id first)
/// ComplementaryC  2026-11-11 09:00:00   ComplementaryAwaitingApproval  A      C
/// </code>
/// <para>
/// They are written in the order UsageA, OutputA, LeftoverA, ComplementaryC, CancelledB, so ids rise in that order.
/// ComplementaryC was written after the 10:00 ones but is older, so ordering by id alone would be wrong. The raw
/// material is named "Queso añejo" and one reason holds quotation marks, so the tests also see characters that JSON
/// escapes.
/// </para>
/// </remarks>
public class NotificationsTests(NotificationsTests.HistoryFactory app) : IClassFixture<NotificationsTests.HistoryFactory>
{
    private const string NotificationsPath = "/api/v1/notifications";

    /// <summary>The history's names, newest first.</summary>
    private static readonly string[] NewestFirst = ["CancelledB", "LeftoverA", "OutputA", "UsageA", "ComplementaryC"];

    /// <summary>Design 5.10: the fields of a notification, in order.</summary>
    private static readonly string[] NotificationFields =
        ["id", "type", "orderNumber", "complementaryOrderNumber", "createdAt", "message", "isRead", "data"];

    /// <summary>The four endpoints of design 4, with <c>{id}</c> standing for a notification id.</summary>
    private static readonly (string Method, string Path)[] Endpoints =
    [
        ("GET", NotificationsPath),
        ("GET", NotificationsPath + "/{id}"),
        ("POST", NotificationsPath + "/{id}/read"),
        ("POST", NotificationsPath + "/read-all"),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private History Notes => app.History;

    // ---- AC-23.6 and design 4: who may call ---------------------------------------------------------------------

    /// <summary>
    /// Design 4, architecture 7: every notification endpoint needs a token; without one it is <c>401</c>
    /// <c>not-authenticated</c> with <c>WWW-Authenticate: Bearer</c>.
    /// </summary>
    [Fact]
    public async Task Every_endpoint_needs_a_token()
    {
        using var anonymous = app.CreateClient();

        foreach (var (method, path) in Endpoints)
        {
            var response = await anonymous.SendAsync(Request(method, path, Notes.Ids["UsageA"]), Cancel);
            await ApiAssert.NotAuthenticatedAsync(response);
        }
    }

    /// <summary>
    /// AC-23.6, PRD 4.2 and assumption 14: a Warehouse-only or Production-only user gets <c>403</c> "It needs the
    /// Manager role." on all four endpoints, also for an id that does not exist (design 2.2: the role is checked before
    /// the record, so no id is revealed), and nothing is marked read.
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Warehouse, Roles.Production)]
    public async Task Users_without_the_Manager_role_get_403_on_every_endpoint(params string[] roles)
    {
        using var user = await app.CreateClientAsAsync(roles);
        var readsBefore = (await ReadsAsync(app)).Count;

        foreach (var (method, path) in Endpoints)
        {
            foreach (var id in new[] { Notes.Ids["UsageA"], 999999 })
            {
                var response = await user.SendAsync(Request(method, path, id), Cancel);
                await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);
            }
        }

        Assert.Equal(readsBefore, (await ReadsAsync(app)).Count);
    }

    /// <summary>
    /// AC-05.2, AC-23.1: a user who holds the Manager role together with another role may use every endpoint.
    /// </summary>
    [Fact]
    public async Task A_Manager_with_other_roles_may_use_every_endpoint()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Warehouse, Roles.Manager);
        var id = Notes.Ids["UsageA"];

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(NotificationsPath, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{NotificationsPath}/{id}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await MarkReadRawAsync(manager, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ReadAllRawAsync(manager)).StatusCode);
    }

    // ---- AC-23.1: the list ----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-23.1, PRD 4.1, design 2.5, decision #17: the list is newest first by creation time, not by id and not unread
    /// first, and notifications raised in the same second come newest id first. The default page is 1 of 50, and
    /// <c>totalCount</c> counts them all.
    /// </summary>
    /// <remarks>
    /// This test uses its own app and first drops the index on <c>Notifications.CreatedAt</c>. With the index, SQLite
    /// reads the index backwards and its entries also hold the row id, so equal times already come out newest id
    /// first and the test could not tell whether the service sorts ties itself. Without the index SQLite sorts the rows
    /// itself, and equal times come out oldest id first unless the query says otherwise, so the test fails if
    /// <c>ThenByDescending(Id)</c> is ever removed. (PostgreSQL gives no tie order at all without it.)
    /// </remarks>
    [Fact]
    public async Task List_is_newest_first_with_the_id_breaking_ties()
    {
        await using var own = new TestApiFactory();
        var history = await WriteHistoryAsync(own);
        await using (var scope = own.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlRawAsync("DROP INDEX \"IX_Notifications_CreatedAt\"", Cancel);
        }

        using var manager = await own.CreateClientAsAsync(Roles.Manager);

        // One of them read first, to show read status does not change the order (decision #17).
        await MarkReadAsync(manager, history.Ids["ComplementaryC"]);
        var page = await ListAsync(manager, "");
        var unread = await ListAsync(manager, "?unreadOnly=true");

        Assert.Equal(IdsOf(NewestFirst, history), ApiJson.Ids(page));
        Assert.Equal(IdsOf(["CancelledB", "LeftoverA", "OutputA", "UsageA"], history), ApiJson.Ids(unread));
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(50, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// AC-23.1, design 5.10: each notification shows exactly the eight design fields, in order: id, type, order number,
    /// complementary order number (only for ComplementaryAwaitingApproval, otherwise null), UTC creation time with
    /// <c>Z</c>, the stored message, <c>isRead</c> for the caller, and <c>data</c>. <c>data</c> is a JSON object, not a
    /// string, and holds exactly the stored JSON (same fields in the same order, same strings, same number digits) for
    /// all five types, including characters that JSON escapes.
    /// </summary>
    [Fact]
    public async Task Each_notification_shows_every_design_field_and_its_data_as_stored()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        var rows = (await ListAsync(manager, "")).GetProperty("items").EnumerateArray().ToList();

        var expected = new Dictionary<string, (string Type, int Order, int? Complementary, string CreatedAt)>
        {
            ["CancelledB"] = ("OrderCancelledWithMaterial", Notes.OrderB, null, "2026-11-11T12:00:00Z"),
            ["LeftoverA"] = ("LeftoverAtCompletion", Notes.OrderA, null, "2026-11-11T10:00:00Z"),
            ["OutputA"] = ("OutputVariance", Notes.OrderA, null, "2026-11-11T10:00:00Z"),
            ["UsageA"] = ("UsageVariance", Notes.OrderA, null, "2026-11-11T10:00:00Z"),
            ["ComplementaryC"] = ("ComplementaryAwaitingApproval", Notes.OrderA, Notes.ComplementaryC, "2026-11-11T09:00:00Z"),
        };
        Assert.Equal(5, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var name = NewestFirst[i];
            var stored = await StoredNotificationAsync(app, Notes.Ids[name]);
            var (type, order, complementary, createdAt) = expected[name];

            Assert.Equal(NotificationFields, row.EnumerateObject().Select(field => field.Name));
            Assert.Equal(Notes.Ids[name], row.GetProperty("id").GetInt32());
            Assert.Equal(type, row.GetProperty("type").GetString());
            Assert.Equal(order, row.GetProperty("orderNumber").GetInt32());
            if (complementary is null)
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("complementaryOrderNumber").ValueKind);
            }
            else
            {
                Assert.Equal(complementary, row.GetProperty("complementaryOrderNumber").GetInt32());
            }

            Assert.Equal(createdAt, row.GetProperty("createdAt").GetString());
            Assert.Equal(stored.Message, row.GetProperty("message").GetString());
            Assert.False(row.GetProperty("isRead").GetBoolean());

            var data = row.GetProperty("data");
            Assert.Equal(JsonValueKind.Object, data.ValueKind);
            AssertSameJson(stored.DataJson, data);
        }

        // Spot checks that the values inside data are the design 5.10 ones, read as JSON: a nested object, a list and
        // characters that JSON escapes (ñ and quotation marks) all arrive as the original text.
        var complementaryData = rows[4].GetProperty("data");
        Assert.Equal(["materials", "reason", "requestedBy"], complementaryData.EnumerateObject().Select(field => field.Name));
        var requested = Assert.Single(complementaryData.GetProperty("materials").EnumerateArray());
        Assert.Equal(Notes.Cheese.Id, requested.GetProperty("itemId").GetInt32());
        Assert.Equal("Queso añejo", requested.GetProperty("itemName").GetString());
        Assert.Equal(20m, requested.GetProperty("requestedAmount").GetDecimal());
        Assert.Equal(Notes.Requester.Id, complementaryData.GetProperty("requestedBy").GetProperty("id").GetString());
        Assert.Equal(Notes.Requester.Email, complementaryData.GetProperty("requestedBy").GetProperty("email").GetString());
        Assert.Equal("Last bag line \"B\" jammed", rows[1].GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(2.9m, rows[3].GetProperty("data").GetProperty("percent").GetDecimal());
    }

    /// <summary>
    /// AC-23.1, design 5.10: <c>GET /notifications/{id}</c> returns exactly the list row, for every notification.
    /// </summary>
    [Fact]
    public async Task Each_list_row_equals_the_single_notification()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        await MarkReadAsync(manager, Notes.Ids["OutputA"]);

        foreach (var row in (await ListAsync(manager, "")).GetProperty("items").EnumerateArray())
        {
            var one = await GetAsync(manager, row.GetProperty("id").GetInt32());
            Assert.Equal(row.GetRawText(), one.GetRawText());
        }
    }

    /// <summary>
    /// PRD 4.1, design 2.5: the list is paged. With <c>pageSize=2</c> the newest-first list splits 2, 2, 1; a page past
    /// the end is <c>200</c> with no items; every page reports the page asked for and the full <c>totalCount</c>.
    /// </summary>
    [Fact]
    public async Task Pages_split_the_newest_first_list()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        string[][] expectedPages = [["CancelledB", "LeftoverA"], ["OutputA", "UsageA"], ["ComplementaryC"], []];

        for (var page = 1; page <= expectedPages.Length; page++)
        {
            var body = await ListAsync(manager, $"?page={page}&pageSize=2");

            Assert.Equal(IdsOf(expectedPages[page - 1]), ApiJson.Ids(body));
            Assert.Equal(page, body.GetProperty("page").GetInt32());
            Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
        }
    }

    /// <summary>
    /// AC-23.1, design 5.10 and 3.11: <c>unreadOnly</c> accepts <c>true</c> and <c>false</c> in any letter case, and an
    /// empty value means the default, <c>false</c>. With <c>true</c> the list holds only what this Manager has not
    /// read, still newest first, and <c>totalCount</c> is their unread count; paging applies to the filtered list. A
    /// value of only spaces also means the default: model binding passes it to the API as null, as for every other
    /// filter (decision #102).
    /// </summary>
    [Theory]
    [InlineData("?unreadOnly=true", true)]
    [InlineData("?unreadOnly=TRUE", true)]
    [InlineData("?unreadOnly=True&page=1&pageSize=50", true)]
    [InlineData("?unreadOnly=false", false)]
    [InlineData("?unreadOnly=False", false)]
    [InlineData("?unreadOnly=", false)]
    [InlineData("?unreadOnly=%20%20", false)]
    [InlineData("", false)]
    public async Task UnreadOnly_filters_to_what_the_caller_has_not_read(string query, bool onlyUnread)
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        await MarkReadAsync(manager, Notes.Ids["LeftoverA"]);
        await MarkReadAsync(manager, Notes.Ids["ComplementaryC"]);

        var body = await ListAsync(manager, query);

        var expected = onlyUnread ? ["CancelledB", "OutputA", "UsageA"] : NewestFirst;
        Assert.Equal(IdsOf(expected), ApiJson.Ids(body));
        Assert.Equal(expected.Length, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// AC-23.1 with paging: the unread view pages like any list. With two read, <c>pageSize=2</c> gives the three
    /// unread ones as 2 and 1, each page reporting <c>totalCount</c> 3.
    /// </summary>
    [Fact]
    public async Task The_unread_view_is_paged()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        await MarkReadAsync(manager, Notes.Ids["CancelledB"]);
        await MarkReadAsync(manager, Notes.Ids["OutputA"]);

        var first = await ListAsync(manager, "?unreadOnly=true&pageSize=2");
        var second = await ListAsync(manager, "?unreadOnly=true&pageSize=2&page=2");

        Assert.Equal(IdsOf(["LeftoverA", "UsageA"]), ApiJson.Ids(first));
        Assert.Equal(IdsOf(["ComplementaryC"]), ApiJson.Ids(second));
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, second.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// Design 2.5 and 7.2, decision #102: paging outside its limits and an <c>unreadOnly</c> that is not exactly
    /// <c>true</c> or <c>false</c> (numbers, words such as <c>yes</c>, and surrounding spaces included) are <c>400</c>
    /// <c>validation</c>, each under its query key.
    /// </summary>
    [Theory]
    [InlineData("?unreadOnly=yes", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?unreadOnly=1", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?unreadOnly=0", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?unreadOnly=truee", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?unreadOnly=%20true", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?unreadOnly=false%20", "unreadOnly", NotificationService.UnreadOnlyMessage)]
    [InlineData("?page=0", "page", "page must be 1 or greater.")]
    [InlineData("?pageSize=0", "pageSize", "pageSize must be between 1 and 100.")]
    [InlineData("?pageSize=101&unreadOnly=true", "pageSize", "pageSize must be between 1 and 100.")]
    public async Task Bad_query_values_are_400(string query, string path, string message)
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);

        await ApiAssert.ValidationAsync(await manager.GetAsync(NotificationsPath + query, Cancel), path, message);
    }

    /// <summary>
    /// Design 6: when both paging and <c>unreadOnly</c> are wrong, one <c>400</c> lists both, in query order.
    /// </summary>
    [Fact]
    public async Task Every_bad_query_value_is_reported_at_once()
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);

        var response = await manager.GetAsync($"{NotificationsPath}?page=0&unreadOnly=maybe", Cancel);

        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            $"page must be 1 or greater. {NotificationService.UnreadOnlyMessage}");
        Assert.Equal(["page must be 1 or greater."], ApiJson.Messages(body, "page"));
        Assert.Equal([NotificationService.UnreadOnlyMessage], ApiJson.Messages(body, "unreadOnly"));
    }

    // ---- AC-23.3: read status per Manager ---------------------------------------------------------------------------

    /// <summary>
    /// AC-23.3, design 3.11: Manager A marks one notification read (<c>204</c>, no body). A then sees it as read, in the
    /// list, in the single read and by its absence from the unread view; Manager B still sees it as unread everywhere.
    /// Exactly one read record exists for it, and it is A's.
    /// </summary>
    [Fact]
    public async Task Marking_read_is_for_the_calling_Manager_only()
    {
        var (managerA, userA) = await SignedInAsync(app, Roles.Manager);
        var (managerB, userB) = await SignedInAsync(app, Roles.Manager);
        var id = Notes.Ids["LeftoverA"];

        await MarkReadAsync(managerA, id);

        Assert.True((await GetAsync(managerA, id)).GetProperty("isRead").GetBoolean());
        Assert.Equal([id], ReadIds(await ListAsync(managerA, "")));
        Assert.Equal(IdsOf(["CancelledB", "OutputA", "UsageA", "ComplementaryC"]), ApiJson.Ids(await ListAsync(managerA, "?unreadOnly=true")));

        Assert.False((await GetAsync(managerB, id)).GetProperty("isRead").GetBoolean());
        Assert.Empty(ReadIds(await ListAsync(managerB, "")));
        Assert.Equal(IdsOf(NewestFirst), ApiJson.Ids(await ListAsync(managerB, "?unreadOnly=true")));

        // Other tests' Managers may have read it too; of these two, only A has a record.
        var read = Assert.Single(await ReadsAsync(app, notificationId: id),
            candidate => candidate.UserId == userA.Id || candidate.UserId == userB.Id);
        Assert.Equal(userA.Id, read.UserId);
    }

    /// <summary>
    /// AC-23.3, design 7.2 ("Marking an already-read notification read again succeeds"): marking twice gives <c>204</c>
    /// both times, keeps one read record, and keeps the time of the first read.
    /// </summary>
    [Fact]
    public async Task Marking_read_twice_is_harmless_and_keeps_the_first_read_time()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var own = new TestApiFactory { FakeTime = time };
        var history = await WriteHistoryAsync(own);
        var (manager, user) = await SignedInAsync(own, Roles.Manager);
        var id = history.Ids["OutputA"];

        time.SetUtcNow(At(13));
        await MarkReadAsync(manager, id);
        time.SetUtcNow(At(14));
        await MarkReadAsync(manager, id);

        var read = Assert.Single(await ReadsAsync(own, notificationId: id));
        Assert.Equal(user.Id, read.UserId);
        Assert.Equal(At(13).UtcDateTime, read.ReadAt);
        Assert.True((await GetAsync(manager, id)).GetProperty("isRead").GetBoolean());
    }

    /// <summary>
    /// AC-23.3 ("A Manager can also mark all notifications read for themselves"), design 3.11: read-all gives
    /// <c>204</c> and marks every notification read for the caller only. A notification the caller had already read
    /// keeps its first read time; calling again with nothing unread is still <c>204</c>; another Manager still sees
    /// all of them unread; and a notification raised afterwards is unread for the caller.
    /// </summary>
    [Fact]
    public async Task Read_all_marks_everything_for_the_caller_only()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var own = new TestApiFactory { FakeTime = time };
        var history = await WriteHistoryAsync(own);
        var (managerA, userA) = await SignedInAsync(own, Roles.Manager);
        var (managerB, _) = await SignedInAsync(own, Roles.Manager);

        time.SetUtcNow(At(13));
        await MarkReadAsync(managerA, history.Ids["UsageA"]);
        time.SetUtcNow(At(14));
        await ReadAllAsync(managerA);
        await ReadAllAsync(managerA);

        // The caller: nothing unread, every row read.
        var unreadA = await ListAsync(managerA, "?unreadOnly=true");
        Assert.Empty(ApiJson.Ids(unreadA));
        Assert.Equal(0, unreadA.GetProperty("totalCount").GetInt32());
        Assert.Equal(IdsOf(NewestFirst, history), ReadIds(await ListAsync(managerA, "")));

        // One record per notification, all the caller's; the one read before keeps its 13:00 time.
        var reads = await ReadsAsync(own);
        Assert.Equal(IdsOf(NewestFirst, history).Order(), reads.Select(read => read.NotificationId).Order());
        Assert.All(reads, read => Assert.Equal(userA.Id, read.UserId));
        Assert.All(reads, read => Assert.Equal(
            read.NotificationId == history.Ids["UsageA"] ? At(13).UtcDateTime : At(14).UtcDateTime, read.ReadAt));

        // Another Manager is unaffected.
        Assert.Equal(IdsOf(NewestFirst, history), ApiJson.Ids(await ListAsync(managerB, "?unreadOnly=true")));

        // A notification raised after the read-all is unread for the caller.
        var later = await AddNotificationAsync(own, NotificationComposer.OutputVariance(
            history.OrderB, new NotificationItem(history.Product.Id, history.Product.Name, history.Product.UnitName),
            500m, 499m, At(15).UtcDateTime));
        Assert.Equal([later], ApiJson.Ids(await ListAsync(managerA, "?unreadOnly=true")));
    }

    /// <summary>
    /// AC-23.3 at volume, decision #102 (c): read-all is one <c>INSERT ... SELECT</c> statement. Over 1,205
    /// notifications (the history plus 1,200 more), where the caller has already read one and another Manager has read
    /// two, it leaves the caller with exactly one read record per notification: the one read earlier keeps its 13:00
    /// time, every other gets the read-all time, and the other Manager's records and unread count are unchanged. The
    /// records the statement wrote store <c>ReadAt</c> in exactly the text form EF Core writes for a single mark, so both
    /// kinds of record read back the same way. No timing is measured.
    /// </summary>
    [Fact]
    public async Task Read_all_over_many_notifications_adds_exactly_one_record_each()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var own = new TestApiFactory { FakeTime = time };
        var history = await WriteHistoryAsync(own);
        var product = new NotificationItem(history.Product.Id, history.Product.Name, history.Product.UnitName);
        await InTransactionAsync(own, (db, _, _) =>
        {
            db.Notifications.AddRange(Enumerable.Range(0, 1200).Select(i => NotificationComposer.OutputVariance(
                history.OrderB, product, 500m, 501m + i, At(11).UtcDateTime.AddSeconds(i))));
            return Task.CompletedTask;
        });
        var allIds = await NotificationIdsAsync(own);
        Assert.Equal(1205, allIds.Count);
        var (managerA, userA) = await SignedInAsync(own, Roles.Manager);
        var (managerB, userB) = await SignedInAsync(own, Roles.Manager);

        time.SetUtcNow(At(13));
        await MarkReadAsync(managerA, history.Ids["UsageA"]);
        await MarkReadAsync(managerB, history.Ids["OutputA"]);
        await MarkReadAsync(managerB, allIds[^1]);
        time.SetUtcNow(At(14));
        await ReadAllAsync(managerA);

        // The caller: one record for every notification (the sorted lists can only be equal if none is missing or
        // doubled), the earlier one unchanged, nothing left unread.
        var reads = await ReadsAsync(own);
        var readsA = reads.Where(read => read.UserId == userA.Id).ToList();
        Assert.Equal(allIds.Order(), readsA.Select(read => read.NotificationId).Order());
        Assert.All(readsA, read => Assert.Equal(
            read.NotificationId == history.Ids["UsageA"] ? At(13).UtcDateTime : At(14).UtcDateTime, read.ReadAt));
        Assert.Equal(0, (await ListAsync(managerA, "?unreadOnly=true")).GetProperty("totalCount").GetInt32());

        // The other Manager: still exactly their own two records.
        var readsB = reads.Where(read => read.UserId == userB.Id).ToList();
        Assert.Equal(new[] { history.Ids["OutputA"], allIds[^1] }.Order(), readsB.Select(read => read.NotificationId).Order());
        Assert.All(readsB, read => Assert.Equal(At(13).UtcDateTime, read.ReadAt));
        Assert.Equal(1203, (await ListAsync(managerB, "?unreadOnly=true")).GetProperty("totalCount").GetInt32());
        Assert.Equal(reads.Count, readsA.Count + readsB.Count);

        // The stored text of ReadAt: the record EF Core wrote (13:00) and the ones the SQL statement wrote (14:00)
        // differ only in the hour.
        var texts = await ReadAtTextsAsync(own, userA.Id);
        var written = texts[history.Ids["UsageA"]];
        Assert.Contains("13:00:00", written);
        Assert.All(texts.Where(pair => pair.Key != history.Ids["UsageA"]),
            pair => Assert.Equal(written.Replace("13:00:00", "14:00:00"), pair.Value));
    }

    /// <summary>
    /// AC-23.4, PRD assumption 16, architecture 3.2: a Manager created after the notifications exist, and after another
    /// Manager has read all of them, sees every one of them, as unread.
    /// </summary>
    [Fact]
    public async Task A_Manager_created_later_sees_every_earlier_notification_as_unread()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var own = new TestApiFactory { FakeTime = time };
        var history = await WriteHistoryAsync(own);
        var (earlier, _) = await SignedInAsync(own, Roles.Manager);
        time.SetUtcNow(At(13));
        await ReadAllAsync(earlier);

        time.SetUtcNow(At(13).AddMinutes(30));
        var (later, laterUser) = await SignedInAsync(own, Roles.Manager);

        Assert.True(laterUser.CreatedAt > At(12).UtcDateTime, "The Manager must be created after the newest notification.");
        var unread = await ListAsync(later, "?unreadOnly=true");
        Assert.Equal(IdsOf(NewestFirst, history), ApiJson.Ids(unread));
        Assert.Equal(5, unread.GetProperty("totalCount").GetInt32());
        Assert.Empty(ReadIds(await ListAsync(later, "")));
        foreach (var id in history.Ids.Values)
        {
            Assert.False((await GetAsync(later, id)).GetProperty("isRead").GetBoolean());
        }
    }

    // ---- AC-23.5: unknown ids, no delete ----------------------------------------------------------------------------

    /// <summary>
    /// AC-23.5, design 7.2: an unknown id is <c>404</c> <c>not-found</c> "Notification {id} not found." on the single
    /// read and on mark-read, and mark-read of an unknown id stores nothing.
    /// </summary>
    [Theory]
    [InlineData(999999)]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task An_unknown_id_is_404(int id)
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        var readsBefore = (await ReadsAsync(app)).Count;

        await ApiAssert.ProblemAsync(await manager.GetAsync($"{NotificationsPath}/{id}", Cancel),
            HttpStatusCode.NotFound, "not-found", $"Notification {id} not found.");
        await ApiAssert.ProblemAsync(await MarkReadRawAsync(manager, id),
            HttpStatusCode.NotFound, "not-found", $"Notification {id} not found.");

        Assert.Equal(readsBefore, (await ReadsAsync(app)).Count);
    }

    /// <summary>
    /// AC-23.5, design 4: notifications are never deleted or edited. <c>DELETE</c>, <c>PUT</c> and <c>PATCH</c> on a
    /// notification, and <c>DELETE</c> on the list, get the framework's <c>405</c>, and the notification is unchanged.
    /// </summary>
    [Theory]
    [InlineData("DELETE", "/{id}")]
    [InlineData("PUT", "/{id}")]
    [InlineData("PATCH", "/{id}")]
    [InlineData("DELETE", "")]
    [InlineData("DELETE", "/{id}/read")]
    public async Task Notifications_cannot_be_deleted_or_edited(string method, string suffix)
    {
        using var manager = await app.CreateClientAsAsync(Roles.Manager);
        var id = Notes.Ids["CancelledB"];
        var before = await GetAsync(manager, id);

        var request = Request(method, NotificationsPath + suffix, id);
        request.Content = ApiJson.Content("{}");
        var response = await manager.SendAsync(request, Cancel);

        var body = await ApiJson.ReadAsync(response);
        ApiAssert.Problem(response, body, HttpStatusCode.MethodNotAllowed, ApiAssert.NoTitleCheck,
            "method-not-allowed", "This method is not allowed on this path.");
        Assert.Equal(before.GetRawText(), (await GetAsync(manager, id)).GetRawText());
        Assert.Equal(5, (await ListAsync(manager, "")).GetProperty("totalCount").GetInt32());
    }

    // ---- Concurrency: marking read at the same moment ---------------------------------------------------------------

    /// <summary>
    /// AC-23.3 under concurrency (PRD section 5, architecture 5.2): one Manager sends six marks of the same notification
    /// and two read-alls at the same moment, on separate connections. Every request gets <c>204</c> (none fails on the
    /// composite key), and exactly one read record exists per notification.
    /// </summary>
    [Fact]
    public async Task Simultaneous_marks_by_one_Manager_all_succeed()
    {
        var email = Seed.UniqueEmail("manager");
        var user = await app.CreateUserAsync(email, Roles.Manager);
        var token = await app.SignInAsync(email);
        var id = Notes.Ids["UsageA"];
        var clients = Enumerable.Range(0, 8).Select(_ => app.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(clients.Select((client, i) => i < 6 ? MarkReadRawAsync(client, id) : ReadAllRawAsync(client)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        var reads = (await ReadsAsync(app)).Where(read => read.UserId == user.Id).ToList();
        Assert.Equal(IdsOf(NewestFirst).Order(), reads.Select(read => read.NotificationId).Order());
        clients.ForEach(client => client.Dispose());
    }

    /// <summary>
    /// Architecture 5.2, the reason the marks above cannot collide: a mark holds the write lock from the start of its
    /// transaction. The first mark is paused just before it saves its read record; a second mark of the same
    /// notification by the same Manager then waits instead of running alongside. Once the first commits, the second
    /// finds the record and also answers <c>204</c>, leaving one record.
    /// </summary>
    [Fact]
    public async Task A_second_mark_waits_for_the_first_and_then_succeeds()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<NotificationRead>().Any(entry => entry.State == EntityState.Added),
            work: async (_, cancellationToken) =>
            {
                paused.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        await using var own = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var history = await WriteHistoryAsync(own);
        var email = Seed.UniqueEmail("manager");
        var user = await own.CreateUserAsync(email, Roles.Manager);
        var token = await own.SignInAsync(email);
        using var firstClient = own.CreateClientWithToken(token);
        using var secondClient = own.CreateClientWithToken(token);
        var id = history.Ids["LeftoverA"];

        var first = MarkReadRawAsync(firstClient, id);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        var second = MarkReadRawAsync(secondClient, id);

        // Give the second request ample time to reach its transaction; it must still be waiting for the lock.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
        Assert.False(second.IsCompleted, "The second mark ran while the first held the write transaction.");
        release.SetResult();

        Assert.Equal(HttpStatusCode.NoContent, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await second).StatusCode);
        var read = Assert.Single(await ReadsAsync(own, notificationId: id));
        Assert.Equal(user.Id, read.UserId);
    }

    /// <summary>
    /// Architecture 4.1 item 11, decision #46 (the backstop): if a read record for the same Manager and notification
    /// appeared between the mark's check and its save, which the write transaction rules out, the composite primary key
    /// refuses the insert and the answer is <c>409</c> <c>concurrent-change</c>, never a <c>500</c>. Nothing is saved,
    /// and marking again succeeds.
    /// </summary>
    [Fact]
    public async Task A_duplicate_read_record_at_save_time_is_409_not_500()
    {
        var id = 0;
        var userId = "";
        var interceptor = new BeforeSaveInterceptor(
            shouldRun: context => context.ChangeTracker.Entries<NotificationRead>().Any(entry => entry.State == EntityState.Added),
            // The same row the mark is about to insert, written on the request's own connection and transaction.
            work: (context, cancellationToken) => context.Database.ExecuteSqlAsync(
                $"INSERT INTO NotificationReads (NotificationId, UserId, ReadAt) VALUES ({id}, {userId}, {DateTime.UtcNow})",
                cancellationToken));
        await using var own = new TestApiFactory
        {
            ReplaceServices = services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)),
        };
        var history = await WriteHistoryAsync(own);
        var (manager, user) = await SignedInAsync(own, Roles.Manager);
        id = history.Ids["OutputA"];
        userId = user.Id;

        var response = await MarkReadRawAsync(manager, id);

        Assert.True(interceptor.HasRun);
        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, "concurrent-change",
            "The data changed while your request was being processed. Read it again and retry.");
        Assert.Empty(await ReadsAsync(own, notificationId: id));
        await MarkReadAsync(manager, id);
        Assert.Single(await ReadsAsync(own, notificationId: id));
    }

    // ---- The thin slice: real notifications from a real completion --------------------------------------------------

    /// <summary>
    /// T-012 (the thin slice end to end), AC-23.1, AC-23.2, AC-20.2, AC-20.4, AC-19.7, design 5.10 and 7.3, decisions #58
    /// and #98 (d). The first Manager signs in and sets up units, items and a recipe (1 kg Cheese and 0.015 kg Potato
    /// starch per bag), receives stock and creates order #1 for 1,000 bags; a Warehouse user sends 1,020 kg Cheese and
    /// 15 kg starch; a Production user completes it at 15:40 with 995 bags and 0.5 kg starch left over. That raises four
    /// notifications: Cheese used 1,020 for 995 required (+25 kg, +2.51%), starch used 14.5 for 14.925 required
    /// (−0.425 kg, −2.85%), output 995 of 1,000 (−5 Bag, −0.50%), and the leftover. The Manager reads them through the
    /// API, newest first, with the exact design 7.3 messages and design 5.10 data; each percentage equals the completion
    /// response's (decision #58). The Manager marks one read and then all; a Manager created afterwards sees all four
    /// unread (AC-23.4); the Production user who completed the order may not read them (AC-23.6).
    /// </summary>
    [Fact]
    public async Task Thin_slice_completion_raises_notifications_that_a_Manager_reads_and_marks()
    {
        var time = new FakeTimeProvider(EightAm);
        await using var own = new TestApiFactory { FakeTime = time };

        // Sign in as the first Manager, created at start-up from ADMIN_EMAIL and ADMIN_PASSWORD (US-02).
        using var manager = own.CreateClientWithToken(await own.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var (warehouse, _) = await SignedInAsync(own, Roles.Warehouse);
        var (production, _) = await SignedInAsync(own, Roles.Production);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var bag = await Seed.CreateUnitAsync(manager, "Bag");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Cheese");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await Seed.CreateItemAsync(manager, ItemType.FinishedProduct, bag, name: "Shredded cheese 1 kg bag");
        await PutRecipeAsync(manager, product.Id, (cheese.Id, 1m), (starch.Id, 0.015m));
        await ReceiveAsync(manager, cheese.Id, 1200m);
        await ReceiveAsync(manager, starch.Id, 100m);
        var number = await CreateOrderAsync(manager, product.Id, 1000m);
        Assert.Equal(1, number);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (cheese.Id, 1020m), (starch.Id, 15m))).StatusCode);
        Assert.Empty(ApiJson.Ids(await ListAsync(manager, "")));

        time.SetUtcNow(EightAm.AddHours(7).AddMinutes(40));
        var completed = await CompleteAsync(production, number, 995m, "Starch bag left half full", (cheese.Id, 0m), (starch.Id, 0.5m));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var order = await ApiJson.ReadAsync(completed);

        // Newest first; all four share the completion second, so they come newest id first, the reverse of the order
        // the completion added them in (decision #98 (d): usage variances in recipe line order, output, leftover).
        var unread = await ListAsync(manager, "?unreadOnly=true");
        Assert.Equal(4, unread.GetProperty("totalCount").GetInt32());
        var rows = unread.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["LeftoverAtCompletion", "OutputVariance", "UsageVariance", "UsageVariance"],
            rows.Select(row => row.GetProperty("type").GetString()));
        Assert.Equal(rows.Select(row => row.GetProperty("id").GetInt32()).OrderDescending(), ApiJson.Ids(unread));
        foreach (var row in rows)
        {
            Assert.Equal(NotificationFields, row.EnumerateObject().Select(field => field.Name));
            Assert.Equal(number, row.GetProperty("orderNumber").GetInt32());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("complementaryOrderNumber").ValueKind);
            Assert.Equal("2026-11-11T15:40:00Z", row.GetProperty("createdAt").GetString());
            Assert.False(row.GetProperty("isRead").GetBoolean());
            Assert.Equal(JsonValueKind.Object, row.GetProperty("data").ValueKind);
            AssertSameJson((await StoredNotificationAsync(own, row.GetProperty("id").GetInt32())).DataJson, row.GetProperty("data"));
        }

        // AC-19.7, design 7.3 and 5.10: the leftover, only the material above zero.
        Assert.Equal("Order #1 was completed with leftover in production storage: Potato starch 0.5 kg. Reason: \"Starch bag left half full\"",
            rows[0].GetProperty("message").GetString());
        var leftoverData = rows[0].GetProperty("data");
        Assert.Equal(["materials", "reason"], leftoverData.EnumerateObject().Select(field => field.Name));
        var leftover = Assert.Single(leftoverData.GetProperty("materials").EnumerateArray());
        Assert.Equal(["itemId", "itemName", "unit", "leftover"], leftover.EnumerateObject().Select(field => field.Name));
        Assert.Equal(starch.Id, leftover.GetProperty("itemId").GetInt32());
        Assert.Equal("Potato starch", leftover.GetProperty("itemName").GetString());
        Assert.Equal("kg", leftover.GetProperty("unit").GetString());
        Assert.Equal(0.5m, leftover.GetProperty("leftover").GetDecimal());
        Assert.Equal("Starch bag left half full", leftoverData.GetProperty("reason").GetString());

        // AC-20.2: output 995 of 1,000 = -5 Bag, -0.50%.
        Assert.Equal("Order #1: produced 995 Bag, ordered 1,000 Bag. Difference -5 Bag (-0.50%).", rows[1].GetProperty("message").GetString());
        var outputData = rows[1].GetProperty("data");
        Assert.Equal(["finishedProductId", "finishedProductName", "unit", "ordered", "produced", "difference", "percent"],
            outputData.EnumerateObject().Select(field => field.Name));
        Assert.Equal(product.Id, outputData.GetProperty("finishedProductId").GetInt32());
        Assert.Equal("Shredded cheese 1 kg bag", outputData.GetProperty("finishedProductName").GetString());
        Assert.Equal("Bag", outputData.GetProperty("unit").GetString());
        Assert.Equal(1000m, outputData.GetProperty("ordered").GetDecimal());
        Assert.Equal(995m, outputData.GetProperty("produced").GetDecimal());
        Assert.Equal(-5m, outputData.GetProperty("difference").GetDecimal());
        Assert.Equal(-0.5m, outputData.GetProperty("percent").GetDecimal());
        Assert.Equal(order.GetProperty("outputVariancePercent").GetDecimal(), outputData.GetProperty("percent").GetDecimal());

        // AC-20.4: starch 0.425 ÷ 14.925 × 100 = 2.8475711...%, shown -2.85%; Cheese 25 ÷ 995 × 100 = 2.5125628...%,
        // shown +2.51%. Both are above the 2% tolerance.
        Assert.Equal("Order #1, Potato starch: used 14.5 kg, required 14.925 kg for actual output. Difference -0.425 kg (-2.85%).",
            rows[2].GetProperty("message").GetString());
        AssertUsageData(rows[2].GetProperty("data"), starch, required: 14.925m, used: 14.5m, difference: -0.425m, percent: -2.85m);
        Assert.Equal(Line(order, starch.Id).GetProperty("usageVariancePercent").GetDecimal(),
            rows[2].GetProperty("data").GetProperty("percent").GetDecimal());
        Assert.Equal("Order #1, Cheese: used 1,020 kg, required 995 kg for actual output. Difference +25 kg (+2.51%).",
            rows[3].GetProperty("message").GetString());
        AssertUsageData(rows[3].GetProperty("data"), cheese, required: 995m, used: 1020m, difference: 25m, percent: 2.51m);
        Assert.Equal(Line(order, cheese.Id).GetProperty("usageVariancePercent").GetDecimal(),
            rows[3].GetProperty("data").GetProperty("percent").GetDecimal());

        // AC-23.6: the Production user who completed the order cannot read the notifications it raised.
        await ApiAssert.ProblemAsync(await production.GetAsync(NotificationsPath, Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.ManagerOnly);

        // Design 3.11: mark one read, then the rest.
        var outputId = rows[1].GetProperty("id").GetInt32();
        await MarkReadAsync(manager, outputId);
        Assert.Equal(3, (await ListAsync(manager, "?unreadOnly=true")).GetProperty("totalCount").GetInt32());
        Assert.True((await GetAsync(manager, outputId)).GetProperty("isRead").GetBoolean());
        await ReadAllAsync(manager);
        Assert.Equal(0, (await ListAsync(manager, "?unreadOnly=true")).GetProperty("totalCount").GetInt32());

        // AC-23.4: a Manager added after the completion sees all four, unread.
        time.Advance(TimeSpan.FromHours(1));
        var (newcomer, _) = await SignedInAsync(own, Roles.Manager);
        Assert.Equal(ApiJson.Ids(unread), ApiJson.Ids(await ListAsync(newcomer, "?unreadOnly=true")));
    }

    /// <summary>
    /// AC-23.1, design 5.10 and 7.3, decisions #49 and #57: a real completion where the required amount rounds to 0 kg
    /// (0.0004 kg of Potato starch per bag, 1 bag) but 0.004 kg was used raises a UsageVariance without a percentage.
    /// Through the API its message ends "(percentage not applicable)" and its <c>data</c> holds <c>"percent": null</c>:
    /// the field is present with the JSON value null, not left out and not 0.
    /// </summary>
    [Fact]
    public async Task A_usage_variance_without_a_percentage_has_percent_null_in_data()
    {
        await using var own = new TestApiFactory();
        var (manager, managerUser) = await SignedInAsync(own, Roles.Manager);
        var (warehouse, _) = await SignedInAsync(own, Roles.Warehouse);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var starch = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Potato starch");
        var product = await ProductWithRecipeAsync(manager, (starch.Id, 0.0004m));
        await PutLeftoverAsync(own, starch.Id, 0.004m, managerUser.Id);
        var number = await CreateOrderAsync(manager, product.Id, 1m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(warehouse, number, (starch.Id, 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(manager, number, 1m, null, (starch.Id, 0m))).StatusCode);

        var id = Assert.Single(ApiJson.Ids(await ListAsync(manager, "?unreadOnly=true")));
        var notification = await GetAsync(manager, id);

        Assert.Equal("UsageVariance", notification.GetProperty("type").GetString());
        Assert.Equal(
            $"Order #{number}, Potato starch: used 0.004 kg, required 0 kg for actual output. Difference +0.004 kg (percentage not applicable).",
            notification.GetProperty("message").GetString());
        var data = notification.GetProperty("data");
        Assert.Equal(["itemId", "itemName", "unit", "requiredForActualOutput", "actualUsage", "difference", "percent"],
            data.EnumerateObject().Select(field => field.Name));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("percent").ValueKind);
        Assert.Equal(0m, data.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(0.004m, data.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(0.004m, data.GetProperty("difference").GetDecimal());
        AssertSameJson((await StoredNotificationAsync(own, id)).DataJson, data);
    }

    // ---- Helpers: requests ----------------------------------------------------------------------------------------

    /// <summary>A request for one of <see cref="Endpoints"/>, with <c>{id}</c> replaced by <paramref name="id"/>.</summary>
    private static HttpRequestMessage Request(string method, string path, int id) =>
        new(new HttpMethod(method), path.Replace("{id}", id.ToString()));

    /// <summary><c>GET /notifications{query}</c>, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(NotificationsPath + query, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary><c>GET /notifications/{id}</c>, checked for <c>200</c>.</summary>
    private static async Task<JsonElement> GetAsync(HttpClient client, int id)
    {
        var response = await client.GetAsync($"{NotificationsPath}/{id}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary><c>POST /notifications/{id}/read</c> with no body, unchecked.</summary>
    private static Task<HttpResponseMessage> MarkReadRawAsync(HttpClient client, int id) =>
        client.PostAsync($"{NotificationsPath}/{id}/read", content: null, Cancel);

    /// <summary><c>POST /notifications/read-all</c> with no body, unchecked.</summary>
    private static Task<HttpResponseMessage> ReadAllRawAsync(HttpClient client) =>
        client.PostAsync($"{NotificationsPath}/read-all", content: null, Cancel);

    /// <summary>Design 2.2: marks one read and checks the answer is <c>204</c> with an empty body.</summary>
    private static async Task MarkReadAsync(HttpClient client, int id) => await AssertNoContentAsync(await MarkReadRawAsync(client, id));

    /// <summary>Design 2.2: marks all read and checks the answer is <c>204</c> with an empty body.</summary>
    private static async Task ReadAllAsync(HttpClient client) => await AssertNoContentAsync(await ReadAllRawAsync(client));

    private static async Task AssertNoContentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Cancel));
    }

    // ---- Helpers: reading results ---------------------------------------------------------------------------------

    /// <summary>The ids of the history notifications with these names, in the order given.</summary>
    private IEnumerable<int> IdsOf(IEnumerable<string> names) => IdsOf(names, Notes);

    private static IEnumerable<int> IdsOf(IEnumerable<string> names, History history) =>
        names.Select(name => history.Ids[name]).ToList();

    /// <summary>The ids of the rows in a page that show <c>isRead: true</c>, in list order.</summary>
    private static IEnumerable<int> ReadIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("isRead").GetBoolean())
            .Select(row => row.GetProperty("id").GetInt32())
            .ToList();

    /// <summary>Every stored read record, or only those of one notification, read in a new scope.</summary>
    private static async Task<List<NotificationRead>> ReadsAsync(TestApiFactory app, int? notificationId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationReads.AsNoTracking();
        if (notificationId is { } id)
        {
            reads = reads.Where(read => read.NotificationId == id);
        }

        return await reads.ToListAsync(Cancel);
    }

    /// <summary>Every notification id, lowest first.</summary>
    private static async Task<List<int>> NotificationIdsAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking()
            .Select(notification => notification.Id).OrderBy(id => id).ToListAsync(Cancel);
    }

    /// <summary>
    /// The <c>ReadAt</c> column of one Manager's read records exactly as the database stores it (text, on SQLite), by
    /// notification id. Read with raw SQL so EF Core's own date conversion cannot hide a difference in the stored form.
    /// </summary>
    private static async Task<Dictionary<int, string>> ReadAtTextsAsync(TestApiFactory app, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<ReadAtText>($"""
                SELECT "NotificationId", CAST("ReadAt" AS TEXT) AS "ReadAt"
                FROM "NotificationReads" WHERE "UserId" = {userId}
                """)
            .ToListAsync(Cancel);
        return rows.ToDictionary(row => row.NotificationId, row => row.ReadAt);
    }

    /// <summary>One row of <see cref="ReadAtTextsAsync"/>: EF Core fills the properties by column name.</summary>
    private sealed class ReadAtText
    {
        /// <summary>The notification the record is for.</summary>
        public int NotificationId { get; set; }

        /// <summary>The stored text of <c>ReadAt</c>.</summary>
        public string ReadAt { get; set; } = "";
    }

    /// <summary>The notification as stored, read in a new scope.</summary>
    private static async Task<Notification> StoredNotificationAsync(TestApiFactory app, int id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == id, Cancel);
    }

    /// <summary>
    /// Design 5.10: <paramref name="data"/>, as the API returned it, is the same JSON as <paramref name="storedJson"/>,
    /// the notification's <c>DataJson</c>: the same fields in the same order with the same strings, and the same digits
    /// in every number.
    /// </summary>
    /// <remarks>
    /// The two texts are not compared directly, because JSON allows one character to be written in more than one way:
    /// the stored text has "ñ" as the six-character escape backslash-u00F1 (decision #59), and the response's JSON
    /// writer is free to write either that escape or the letter itself. Both mean the same string. Writing both
    /// through the same JSON writer gives them one spelling, while keeping field order and each number exactly as
    /// written (1001.000 stays 1001.000), so any real change still fails.
    /// </remarks>
    private static void AssertSameJson(string storedJson, JsonElement data)
    {
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal(
            JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(storedJson)),
            JsonSerializer.Serialize(data));
    }

    /// <summary>Design 5.10: a UsageVariance <c>data</c> object, exactly its seven fields in order, with these values.</summary>
    private static void AssertUsageData(
        JsonElement data, CreatedItem material, decimal required, decimal used, decimal difference, decimal percent)
    {
        Assert.Equal(["itemId", "itemName", "unit", "requiredForActualOutput", "actualUsage", "difference", "percent"],
            data.EnumerateObject().Select(field => field.Name));
        Assert.Equal(material.Id, data.GetProperty("itemId").GetInt32());
        Assert.Equal(material.Name, data.GetProperty("itemName").GetString());
        Assert.Equal(material.UnitName, data.GetProperty("unit").GetString());
        Assert.Equal(required, data.GetProperty("requiredForActualOutput").GetDecimal());
        Assert.Equal(used, data.GetProperty("actualUsage").GetDecimal());
        Assert.Equal(difference, data.GetProperty("difference").GetDecimal());
        Assert.Equal(percent, data.GetProperty("percent").GetDecimal());
    }

    // ---- The fixed history ------------------------------------------------------------------------------------------

    /// <summary>11 November 2026 at <paramref name="hour"/>:00 UTC.</summary>
    private static DateTimeOffset At(int hour) => new(2026, 11, 11, hour, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Writes the five notifications of the class remarks into <paramref name="app"/>'s database, with the orders and
    /// the complementary order they refer to, and returns their ids by name.
    /// </summary>
    /// <remarks>
    /// The orders and items are created through the API. The complementary order (T-017 builds them) and the
    /// notifications themselves are written in one write transaction, as the events that raise them do
    /// (architecture 5.2). Each notification is built by <see cref="NotificationComposer"/>, so its message and
    /// <c>DataJson</c> are exactly what a real event would store; it is saved on its own so the ids rise in the order
    /// written.
    /// </remarks>
    private static async Task<History> WriteHistoryAsync(TestApiFactory app)
    {
        var (manager, requester) = await SignedInAsync(app, Roles.Manager);
        var kg = await Seed.CreateUnitAsync(manager, "kg");
        var cheese = await Seed.CreateItemAsync(manager, ItemType.RawMaterial, kg, name: "Queso añejo");
        var product = await ProductWithRecipeAsync(manager, (cheese.Id, 1m));
        var orderA = await CreateOrderAsync(manager, product.Id, 1000m);
        var orderB = await CreateOrderAsync(manager, product.Id, 500m);
        var ids = new Dictionary<string, int>();
        var complementaryNumber = 0;

        var material = new NotificationItem(cheese.Id, cheese.Name, kg.Name);
        NotificationMaterial Cheese(decimal quantity) => new(1, cheese.Id, cheese.Name, kg.Name, quantity);

        await InTransactionAsync(app, async (db, _, _) =>
        {
            var complementary = new ComplementaryOrder
            {
                ParentOrderNumber = orderA, Status = ComplementaryOrderStatus.PendingApproval, Reason = "Hopper spill",
                RequestedAt = At(9).UtcDateTime, RequestedById = requester.Id,
                Lines = [new ComplementaryOrderLine { LineNo = 1, RawMaterialId = cheese.Id, RequestedAmount = 20m }],
            };
            db.ComplementaryOrders.Add(complementary);
            await db.SaveChangesAsync(Cancel);
            complementaryNumber = complementary.Number;

            var notifications = new (string Name, Notification Notification)[]
            {
                ("UsageA", NotificationComposer.UsageVariance(orderA, material,
                    new LineCompletion(RequiredForActualOutput: 1001m, LeftoverRemaining: 0m, ActualUsage: 1030m,
                        UsageVariance: 29m, UsageVariancePercent: 2.897103m, AboveTolerance: true), At(10).UtcDateTime)),
                ("OutputA", NotificationComposer.OutputVariance(orderA,
                    new NotificationItem(product.Id, product.Name, product.UnitName), 1000m, 1001m, At(10).UtcDateTime)),
                ("LeftoverA", NotificationComposer.LeftoverAtCompletion(orderA, [Cheese(4m)], "Last bag line \"B\" jammed", At(10).UtcDateTime)),
                ("ComplementaryC", NotificationComposer.ComplementaryAwaitingApproval(orderA, complementary.Number, [Cheese(20m)],
                    "Hopper spill", new NotificationUser(requester.Id, requester.Email!), At(9).UtcDateTime)),
                ("CancelledB", NotificationComposer.OrderCancelledWithMaterial(orderB, [Cheese(50m)], "Customer order withdrawn", At(12).UtcDateTime)),
            };
            foreach (var (name, notification) in notifications)
            {
                db.Notifications.Add(notification);
                await db.SaveChangesAsync(Cancel);
                ids[name] = notification.Id;
            }
        });

        return new History(ids, orderA, orderB, complementaryNumber, requester, cheese, product);
    }

    /// <summary>Adds one notification in its own write transaction, as an event would, and returns its id.</summary>
    private static async Task<int> AddNotificationAsync(TestApiFactory app, Notification notification)
    {
        await InTransactionAsync(app, (db, _, _) =>
        {
            db.Notifications.Add(notification);
            return Task.CompletedTask;
        });
        return notification.Id;
    }

    /// <summary>
    /// What <see cref="WriteHistoryAsync"/> wrote: the notification ids by name, orders A and B, complementary order C,
    /// the Manager who requested C, the raw material and the finished product.
    /// </summary>
    public sealed record History(
        IReadOnlyDictionary<string, int> Ids, int OrderA, int OrderB, int ComplementaryC, AppUser Requester,
        CreatedItem Cheese, CreatedItem Product);

    /// <summary>The app shared by this class's tests, with the fixed history written once before the first test.</summary>
    public sealed class HistoryFactory : TestApiFactory, IAsyncLifetime
    {
        /// <summary>The fixed history of the class remarks.</summary>
        public History History { get; private set; } = null!;

        /// <summary>Writes the history; xUnit calls this once, before the class's first test.</summary>
        public async ValueTask InitializeAsync() => History = await WriteHistoryAsync(this);
    }
}
