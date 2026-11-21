using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// Decision #125: <c>POST /users</c> and <c>PUT /users/{id}/roles</c> take at most <see cref="RequestLimits.MaxRoles"/>
/// (10) roles. More answer <c>400</c> <c>validation</c> "At most 10 roles are allowed." under <c>roles</c>, checked before
/// the write transaction.
/// </summary>
/// <remarks>
/// "Before the write transaction" is proved the same way as for lines (<c>RequestLinesLimitTests</c>): the test holds
/// SQLite's write lock itself (<see cref="HeldWriteLock"/>), and an 11-role body must still be answered, while a
/// 10-role body of the same kind, which passes the cap, is checked to be still waiting for the lock. Before this change,
/// a role change checked its roles inside the transaction, so an 11-role body waited too.
/// </remarks>
public class RequestRolesLimitTests(TestApiFactory app) : IClassFixture<TestApiFactory>
{
    private const string UsersPath = "/api/v1/users";

    /// <summary>A user id that names no user, so the role change's control answers <c>404</c>.</summary>
    private const string NoSuchUser = "00000000-0000-0000-0000-000000000000";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Decision #125 on <c>POST /users</c>: 11 roles get the 400 under <c>roles</c> alone, answered while another
    /// connection holds the write lock; 10 valid roles (repeats allowed, AC-03.3) pass the cap, wait for the lock, and
    /// then create the user (<c>201</c>) with the roles once each.
    /// </summary>
    [Fact]
    public async Task Create_with_too_many_roles_is_refused_without_the_write_lock()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        await using var writeLock = await HeldWriteLock.TakeAsync(app.DatabasePath);

        var control = SendAsync(manager, "POST", UsersPath, CreateBody(RequestLimits.MaxRoles));
        var refused = await SendAsync(manager, "POST", UsersPath, CreateBody(RequestLimits.MaxRoles + 1))
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        await AssertTooManyRolesAsync(refused);
        await AssertWaitingThenAsync(control, writeLock, HttpStatusCode.Created);
        var created = await ApiJson.ReadAsync(await control);
        Assert.Equal(["Warehouse", "Production"], created.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    /// <summary>
    /// Decision #125 on <c>PUT /users/{id}/roles</c>: 11 roles get the 400 even for a user that does not exist (the cap
    /// comes before the <c>404</c>), answered while the write lock is held; 10 roles pass the cap, wait for the lock, and
    /// then get the <c>404</c>.
    /// </summary>
    [Fact]
    public async Task Role_change_with_too_many_roles_is_refused_without_the_write_lock()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var path = $"{UsersPath}/{NoSuchUser}/roles";
        await using var writeLock = await HeldWriteLock.TakeAsync(app.DatabasePath);

        var control = SendAsync(manager, "PUT", path, RolesBody(RequestLimits.MaxRoles));
        var refused = await SendAsync(manager, "PUT", path, RolesBody(RequestLimits.MaxRoles + 1))
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        await AssertTooManyRolesAsync(refused);
        await AssertWaitingThenAsync(control, writeLock, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Decision #125: 10 roles pass the cap and then follow the normal rules (AC-03.3, AC-03.5): 10 valid names set the
    /// roles once each (<c>200</c>), and 10 names with an unknown one get that role's own message under its path.
    /// </summary>
    [Fact]
    public async Task Ten_roles_pass_the_cap_and_meet_the_normal_rules()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var user = await app.CreateUserAsync(Seed.UniqueEmail("roles"), Roles.Warehouse);
        var path = $"{UsersPath}/{user.Id}/roles";

        var accepted = await SendAsync(manager, "PUT", path, RolesBody(RequestLimits.MaxRoles));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(["Warehouse", "Production"],
            (await ApiJson.ReadAsync(accepted)).GetProperty("roles").EnumerateArray().Select(role => role.GetString()));

        var names = Enumerable.Repeat("Production", RequestLimits.MaxRoles - 1).Append("Driver").ToArray();
        var unknown = await SendAsync(manager, "PUT", path, JsonSerializer.Serialize(new { roles = names }));
        await ApiAssert.ValidationAsync(unknown, "roles[9]", "Unknown role 'Driver'. Roles are Warehouse, Production and Manager.");
    }

    /// <summary>
    /// Decision #125 regression, the 1 MB probe at a test-friendly size: 100,000 <c>null</c> roles on a real user's role
    /// change are refused while the write lock is held, in a small response. Before, they were checked inside the
    /// transaction, one message each.
    /// </summary>
    [Fact]
    public async Task Huge_null_role_list_is_refused_quickly_and_briefly()
    {
        var manager = await app.CreateClientAsAsync(Roles.Manager);
        var user = await app.CreateUserAsync(Seed.UniqueEmail("roles"), Roles.Warehouse);
        var nullRoles = "{\"roles\":[" + string.Join(",", Enumerable.Repeat("null", 100_000)) + "]}";
        await using var writeLock = await HeldWriteLock.TakeAsync(app.DatabasePath);

        var response = await SendAsync(manager, "PUT", $"{UsersPath}/{user.Id}/roles", nullRoles)
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        var text = await response.Content.ReadAsStringAsync(Cancel);
        await AssertTooManyRolesAsync(response);
        Assert.True(text.Length < 1_000, $"The response is {text.Length} characters.");
    }

    /// <summary>The decision #125 refusal: 400 <c>validation</c>, the message as <c>detail</c> and as the only error.</summary>
    private static async Task AssertTooManyRolesAsync(HttpResponseMessage response)
    {
        var body = await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", "At most 10 roles are allowed.");
        Assert.Equal(["At most 10 roles are allowed."], ApiJson.Messages(body, "roles"));
        Assert.Single(body.GetProperty("errors").EnumerateObject());
    }

    /// <summary>
    /// Checks that <paramref name="control"/> is still queued behind the held lock, releases the lock, and checks that the
    /// control then finishes with <paramref name="status"/>.
    /// </summary>
    private static async Task AssertWaitingThenAsync(Task<HttpResponseMessage> control, HeldWriteLock writeLock, HttpStatusCode status)
    {
        // The control has had time to reach the lock; it must still be waiting for it.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
        Assert.False(control.IsCompleted, "The 10-role request did not wait for the write lock, so the control proves nothing.");

        await writeLock.ReleaseAsync();
        var afterRelease = await control.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        Assert.Equal(status, afterRelease.StatusCode);
    }

    /// <summary>A <c>POST /users</c> body for a new unique email and a valid password, with <paramref name="count"/> roles.</summary>
    private static string CreateBody(int count) => JsonSerializer.Serialize(new
    {
        email = Seed.UniqueEmail("cap"),
        password = TestApiFactory.UserPassword,
        roles = RoleNames(count),
    });

    /// <summary>A <c>PUT /users/{id}/roles</c> body with <paramref name="count"/> roles.</summary>
    private static string RolesBody(int count) => JsonSerializer.Serialize(new { roles = RoleNames(count) });

    /// <summary><paramref name="count"/> valid role names, alternating Warehouse and Production (repeats are allowed, AC-03.3).</summary>
    private static string[] RoleNames(int count) =>
        Enumerable.Range(0, count).Select(index => index % 2 == 0 ? Roles.Warehouse : Roles.Production).ToArray();

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string json) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = ApiJson.Content(json) }, Cancel);
}
