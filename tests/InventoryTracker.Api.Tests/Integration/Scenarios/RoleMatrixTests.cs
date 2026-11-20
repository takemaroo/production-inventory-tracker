using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Integration.Scenarios.ScenarioApi;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// T-020: the full role matrix (AC-05.1, AC-05.2, AC-01.5). One table, <see cref="Endpoints"/>, lists every endpoint of
/// design section 4 next to the PRD 4.2 action it performs; <see cref="Prd42"/> is the PRD 4.2 table itself, action by
/// action, with the roles marked "yes". From those two tables every endpoint is called without a token (<c>401</c>) and
/// as a Warehouse, a Production, a Manager, and a Warehouse plus Production user: a caller holding any allowed role gets
/// the endpoint's success status on a real record, and any other caller gets <c>403</c> with design 7.1's exact message.
/// A third test compares the table with the endpoints the running app actually serves, so an endpoint added later
/// without a row (or a row whose roles differ from its <c>[Authorize]</c>) fails.
/// </summary>
/// <remarks>
/// <para>
/// Each case gets a fresh record to act on from its row's <see cref="Arrange"/> (a new order to send, a new unit to
/// delete, and so on), made through the API by the start-up Manager, so allowed and refused callers act on the same kind
/// of record and no case depends on another. Only one order can be InProduction at a time (AC-15.10), so a row that
/// needs one cancels it again in its clean-up, whatever the case's outcome.
/// </para>
/// <para>
/// All cases of this class share one app (<see cref="MatrixApp"/>, an xUnit class fixture), and xUnit runs the tests of
/// one class one at a time, so the clean-up of one case is finished before the next case arranges its records.
/// </para>
/// </remarks>
public class RoleMatrixTests(RoleMatrixTests.MatrixApp app) : IClassFixture<RoleMatrixTests.MatrixApp>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- PRD 4.2 ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// PRD section 4.2, "Roles and permissions", row for row: each action and the roles marked "yes" for it. A user may do
    /// an action when they hold at least one of its roles (AC-05.2).
    /// </summary>
    public static class Prd42
    {
        /// <summary>"Sign in, change own password": every role.</summary>
        public static readonly PrdAction SignInAndOwnPassword =
            new("Sign in, change own password", Roles.Warehouse, Roles.Production, Roles.Manager);

        /// <summary>"View units, items, recipes, stock, movement history, orders, complementary orders, returns": every role.</summary>
        public static readonly PrdAction View = new(
            "View units, items, recipes, stock, movement history, orders, complementary orders, returns",
            Roles.Warehouse, Roles.Production, Roles.Manager);

        /// <summary>"Record a stock receipt": Warehouse, Manager.</summary>
        public static readonly PrdAction RecordReceipt = new("Record a stock receipt", Roles.Warehouse, Roles.Manager);

        /// <summary>"Send materials for an order or an approved complementary order": Warehouse, Manager.</summary>
        public static readonly PrdAction SendMaterials =
            new("Send materials for an order or an approved complementary order", Roles.Warehouse, Roles.Manager);

        /// <summary>"Record receiving a leftover return": Warehouse, Manager.</summary>
        public static readonly PrdAction ReceiveReturn = new("Record receiving a leftover return", Roles.Warehouse, Roles.Manager);

        /// <summary>"Complete an order (output, leftover, reason)": Production, Manager.</summary>
        public static readonly PrdAction CompleteOrder = new("Complete an order (output, leftover, reason)", Roles.Production, Roles.Manager);

        /// <summary>"Request a complementary order": Production, Manager.</summary>
        public static readonly PrdAction RequestComplementary = new("Request a complementary order", Roles.Production, Roles.Manager);

        /// <summary>"Create or cancel an order": Manager.</summary>
        public static readonly PrdAction CreateOrCancelOrder = new("Create or cancel an order", Roles.Manager);

        /// <summary>"Approve or reject a complementary order": Manager.</summary>
        public static readonly PrdAction ApproveOrReject = new("Approve or reject a complementary order", Roles.Manager);

        /// <summary>"Instruct or cancel a leftover return": Manager.</summary>
        public static readonly PrdAction InstructOrCancelReturn = new("Instruct or cancel a leftover return", Roles.Manager);

        /// <summary>"Make an adjustment": Manager.</summary>
        public static readonly PrdAction MakeAdjustment = new("Make an adjustment", Roles.Manager);

        /// <summary>"Manage units, items, recipes": Manager.</summary>
        public static readonly PrdAction ManageMasterData = new("Manage units, items, recipes", Roles.Manager);

        /// <summary>"Read notifications and mark them read": Manager.</summary>
        public static readonly PrdAction Notifications = new("Read notifications and mark them read", Roles.Manager);

        /// <summary>"Manage users": Manager.</summary>
        public static readonly PrdAction ManageUsers = new("Manage users", Roles.Manager);
    }

    // ---- Design section 4: every endpoint, with its PRD 4.2 action ---------------------------------------------------

    /// <summary>
    /// Design section 4, row for row (paths relative to <c>/api/v1</c>): method, path template, the PRD 4.2 action, the
    /// success status, and how to arrange a real request for a caller. Sign-in is the one endpoint that needs no token
    /// (AC-01.5).
    /// </summary>
    public static readonly IReadOnlyList<MatrixRow> Endpoints =
    [
        new("POST", "/auth/sign-in", Prd42.SignInAndOwnPassword, HttpStatusCode.OK, async (a, roles) =>
        {
            var email = Seed.UniqueEmail("matrix");
            await CreateUserAsync(a.Admin, email, roles);
            return new Call("/auth/sign-in", new { email, password = TestApiFactory.UserPassword });
        }, NeedsToken: false),
        new("POST", "/auth/change-password", Prd42.SignInAndOwnPassword, HttpStatusCode.NoContent, async (a, roles) =>
        {
            // Changing a password ends the user's tokens (decision #26), so a user of the caller's roles is made for it.
            var own = await CreateStaffAsync(a, a.Admin, Seed.UniqueEmail("matrix"), roles);
            return new Call("/auth/change-password",
                new { currentPassword = TestApiFactory.UserPassword, newPassword = "Changed-2026a" }, As: own.Client);
        }),

        new("GET", "/users", Prd42.ManageUsers, HttpStatusCode.OK, (_, _) => Plain("/users")),
        new("POST", "/users", Prd42.ManageUsers, HttpStatusCode.Created, (_, _) => Plain("/users",
            new { email = Seed.UniqueEmail("matrix"), password = TestApiFactory.UserPassword, roles = new[] { Roles.Warehouse } })),
        new("GET", "/users/{id}", Prd42.ManageUsers, HttpStatusCode.OK, async (a, _) => new Call($"/users/{await a.NewUserAsync()}")),
        new("PUT", "/users/{id}/roles", Prd42.ManageUsers, HttpStatusCode.OK,
            async (a, _) => new Call($"/users/{await a.NewUserAsync()}/roles", new { roles = new[] { Roles.Production } })),
        new("POST", "/users/{id}/deactivate", Prd42.ManageUsers, HttpStatusCode.OK,
            async (a, _) => new Call($"/users/{await a.NewUserAsync()}/deactivate")),
        new("POST", "/users/{id}/reactivate", Prd42.ManageUsers, HttpStatusCode.OK, async (a, _) =>
        {
            var id = await a.NewUserAsync();
            await a.ExpectAsync(HttpStatusCode.OK, a.Admin.PostAsync($"{ApiPath}/users/{id}/deactivate", content: null, Cancel));
            return new Call($"/users/{id}/reactivate");
        }),
        new("PUT", "/users/{id}/password", Prd42.ManageUsers, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/users/{await a.NewUserAsync()}/password", new { newPassword = "Reset-2026a" })),

        new("GET", "/units", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/units")),
        new("GET", "/units/{id}", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/units/{a.Unit.Id}")),
        new("POST", "/units", Prd42.ManageMasterData, HttpStatusCode.Created, (_, _) => Plain("/units", new { name = Seed.UniqueName("unit") })),
        new("PUT", "/units/{id}", Prd42.ManageMasterData, HttpStatusCode.OK,
            async (a, _) => new Call($"/units/{(await Seed.CreateUnitAsync(a.Admin)).Id}", new { name = Seed.UniqueName("unit") })),
        new("DELETE", "/units/{id}", Prd42.ManageMasterData, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/units/{(await Seed.CreateUnitAsync(a.Admin)).Id}")),

        new("GET", "/items", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/items")),
        new("GET", "/items/{id}", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/items/{a.Shelf.Id}")),
        new("POST", "/items", Prd42.ManageMasterData, HttpStatusCode.Created,
            (a, _) => Plain("/items", new { name = Seed.UniqueName("item"), type = "RawMaterial", unitId = a.Unit.Id })),
        new("PUT", "/items/{id}", Prd42.ManageMasterData, HttpStatusCode.OK, async (a, _) =>
            new Call($"/items/{(await a.MaterialAsync()).Id}",
                new { name = Seed.UniqueName("item"), description = "Renamed", type = "RawMaterial", unitId = a.Unit.Id })),
        new("DELETE", "/items/{id}", Prd42.ManageMasterData, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/items/{(await a.MaterialAsync()).Id}")),
        new("GET", "/items/{id}/recipe", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/items/{a.Product.Id}/recipe")),
        new("PUT", "/items/{id}/recipe", Prd42.ManageMasterData, HttpStatusCode.OK, async (a, _) =>
            new Call($"/items/{(await Seed.CreateItemAsync(a.Admin, ItemType.FinishedProduct, a.Unit)).Id}/recipe",
                new { lines = new[] { new { itemId = a.Shelf.Id, amountPerUnit = 1m } } })),

        new("GET", "/stock", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/stock")),
        new("GET", "/stock/{itemId}", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/stock/{a.Shelf.Id}")),
        new("GET", "/movements", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/movements")),
        new("GET", "/movements/{id}", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/movements/{a.MovementId}")),
        new("POST", "/receipts", Prd42.RecordReceipt, HttpStatusCode.Created,
            (a, _) => Plain("/receipts", new { itemId = a.Shelf.Id, quantity = 1m, receiptDate = "2026-11-01" })),
        new("POST", "/adjustments", Prd42.MakeAdjustment, HttpStatusCode.Created,
            (a, _) => Plain("/adjustments", new { itemId = a.Shelf.Id, location = "Warehouse", quantity = 1m, reason = "Count" })),

        new("GET", "/production-orders", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/production-orders")),
        new("GET", "/production-orders/{number}", Prd42.View, HttpStatusCode.OK,
            (a, _) => Plain($"/production-orders/{a.SharedOrder}")),
        new("POST", "/production-orders", Prd42.CreateOrCancelOrder, HttpStatusCode.Created,
            (a, _) => Plain("/production-orders", new { finishedProductId = a.Product.Id, orderedQuantity = 10m })),
        new("POST", "/production-orders/{number}/send", Prd42.SendMaterials, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, material) = await a.CreatedOrderAsync();
            return new Call($"/production-orders/{number}/send", new { lines = new[] { new { itemId = material.Id, quantity = 10m } } },
                CleanUp: () => a.EndOrderAsync(number));
        }),
        new("POST", "/production-orders/{number}/complete", Prd42.CompleteOrder, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, material) = await a.InProductionOrderAsync();
            return new Call($"/production-orders/{number}/complete",
                new { producedQuantity = 10m, lines = new[] { new { itemId = material.Id, leftover = 0m } }, reason = (string?)null },
                CleanUp: () => a.EndOrderAsync(number));
        }),
        new("POST", "/production-orders/{number}/cancel", Prd42.CreateOrCancelOrder, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, _) = await a.CreatedOrderAsync();
            return new Call($"/production-orders/{number}/cancel", new { reason = "Customer order withdrawn" },
                CleanUp: () => a.EndOrderAsync(number));
        }),
        new("POST", "/production-orders/{number}/complementary-orders", Prd42.RequestComplementary, HttpStatusCode.Created, async (a, _) =>
        {
            var (number, material) = await a.InProductionOrderAsync();
            return new Call($"/production-orders/{number}/complementary-orders",
                new { reason = "Hopper spill", lines = new[] { new { itemId = material.Id, requestedAmount = 5m } } },
                CleanUp: () => a.EndOrderAsync(number));
        }),

        new("GET", "/complementary-orders", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/complementary-orders")),
        new("GET", "/complementary-orders/{number}", Prd42.View, HttpStatusCode.OK,
            (a, _) => Plain($"/complementary-orders/{a.SharedComplementary}")),
        new("POST", "/complementary-orders/{number}/approve", Prd42.ApproveOrReject, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, complementary, _) = await a.PendingComplementaryAsync();
            return new Call($"/complementary-orders/{complementary}/approve", CleanUp: () => a.EndOrderAsync(number));
        }),
        new("POST", "/complementary-orders/{number}/reject", Prd42.ApproveOrReject, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, complementary, _) = await a.PendingComplementaryAsync();
            return new Call($"/complementary-orders/{complementary}/reject", new { note = "Use the spare" },
                CleanUp: () => a.EndOrderAsync(number));
        }),
        new("POST", "/complementary-orders/{number}/send", Prd42.SendMaterials, HttpStatusCode.OK, async (a, _) =>
        {
            var (number, complementary, material) = await a.PendingComplementaryAsync();
            await a.ExpectAsync(HttpStatusCode.OK, ApproveAsync(a.Admin, complementary));
            return new Call($"/complementary-orders/{complementary}/send", new { lines = new[] { new { itemId = material.Id, quantity = 5m } } },
                CleanUp: () => a.EndOrderAsync(number));
        }),

        new("GET", "/returns", Prd42.View, HttpStatusCode.OK, (_, _) => Plain("/returns")),
        new("GET", "/returns/{number}", Prd42.View, HttpStatusCode.OK, (a, _) => Plain($"/returns/{a.SharedReturn}")),
        new("POST", "/returns", Prd42.InstructOrCancelReturn, HttpStatusCode.Created, async (a, _) =>
            new Call("/returns", new { itemId = (await a.FreeLeftoverAsync()).Id, quantity = 5m, reason = "Not needed" })),
        new("POST", "/returns/{number}/receive", Prd42.ReceiveReturn, HttpStatusCode.OK,
            async (a, _) => new Call($"/returns/{await InstructedReturnAsync(a.Admin, (await a.FreeLeftoverAsync()).Id, 5m)}/receive")),
        new("POST", "/returns/{number}/cancel", Prd42.InstructOrCancelReturn, HttpStatusCode.OK,
            async (a, _) => new Call($"/returns/{await InstructedReturnAsync(a.Admin, (await a.FreeLeftoverAsync()).Id, 5m)}/cancel")),

        new("GET", "/notifications", Prd42.Notifications, HttpStatusCode.OK, (_, _) => Plain("/notifications")),
        new("GET", "/notifications/{id}", Prd42.Notifications, HttpStatusCode.OK, (a, _) => Plain($"/notifications/{a.SharedNotification}")),
        new("POST", "/notifications/{id}/read", Prd42.Notifications, HttpStatusCode.NoContent,
            (a, _) => Plain($"/notifications/{a.SharedNotification}/read")),
        new("POST", "/notifications/read-all", Prd42.Notifications, HttpStatusCode.NoContent, (_, _) => Plain("/notifications/read-all")),
    ];

    /// <summary>
    /// The callers of the matrix, as role lists: one user per single role (AC-05.1), and Warehouse plus Production
    /// (AC-05.2's own example: such a user may both send materials and complete an order, but nothing only a Manager may do).
    /// </summary>
    public static readonly IReadOnlyList<string> Callers =
        [Roles.Warehouse, Roles.Production, Roles.Manager, $"{Roles.Warehouse},{Roles.Production}"];

    /// <summary>One case per endpoint, named <c>METHOD /path</c>, for the <c>401</c> test.</summary>
    /// <remarks>
    /// <c>[MemberData(nameof(EndpointCases))]</c> on a <c>[Theory]</c> tells xUnit to run the test once for each entry of
    /// this property. Each case is reported on its own, named by its values (for example
    /// <c>endpoint: "POST /receipts", caller: "Production"</c>), so a failure names the endpoint and the caller.
    /// The cases are plain strings, not the rows themselves, because xUnit must be able to write each case's values out.
    /// </remarks>
    public static TheoryData<string> EndpointCases => new(Endpoints.Select(row => row.Key));

    /// <summary>One case per endpoint and caller, for the role test: 48 × 4.</summary>
    public static TheoryData<string, string> RoleCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var row in Endpoints)
            {
                foreach (var caller in Callers)
                {
                    cases.Add(row.Key, caller);
                }
            }

            return cases;
        }
    }

    // ---- The tests --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table is complete: its method and path pairs are exactly the app's own API endpoints (read from ASP.NET Core's
    /// routing, so the test sees what the app really serves), the 48 of design section 4, and each endpoint's
    /// <c>[Authorize(Roles = ...)]</c> or <c>[AllowAnonymous]</c> says what PRD 4.2 says. An endpoint added without a row,
    /// a row without an endpoint, or roles that differ, fail here with the endpoint named. Also AC-13.6 and AC-11.3:
    /// since the lists are equal, no endpoint edits an order or edits or deletes a movement.
    /// </summary>
    [Fact]
    public void Table_lists_every_endpoint_the_app_serves_with_its_roles()
    {
        var served = ServedEndpoints(app);

        Assert.Equal(48, Endpoints.Count);
        Assert.Equal(Endpoints.Select(row => row.Key).Order(StringComparer.Ordinal), served.Keys.Order(StringComparer.Ordinal));
        foreach (var row in Endpoints)
        {
            Assert.True(served[row.Key] == row.ExpectedAuthorization,
                $"{row.Key}: the app declares '{served[row.Key]}', PRD 4.2 ('{row.Action.Text}') gives '{row.ExpectedAuthorization}'.");
        }
    }

    /// <summary>
    /// AC-01.5: every endpoint except sign-in answers <c>401</c> <c>not-authenticated</c> (with
    /// <c>WWW-Authenticate: Bearer</c>) without a token. Sign-in needs none, so without a token it signs a user in
    /// (<c>200</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task Endpoint_without_a_token(string endpoint)
    {
        var row = Row(endpoint);
        using var anonymous = app.CreateClient();

        if (!row.NeedsToken)
        {
            var call = await row.Arrange(app, [Roles.Warehouse]);
            Assert.Equal(row.Success, (await CallAsync(anonymous, row.Method, call.Path, call.Body)).StatusCode);
            return;
        }

        // Ids are only placeholders: the token check comes before anything looks at the record or the body (design 2.2).
        var path = Regex.Replace(row.Template, @"\{\w+\}", "1");
        var response = await CallAsync(anonymous, row.Method, path, row.Method is "POST" or "PUT" ? new { } : null);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// AC-05.1, AC-05.2, design 2.2 and 7.1: for every endpoint and caller, a caller holding at least one role PRD 4.2
    /// marks "yes" for the endpoint's action gets its success status on a real record, and a caller holding none gets
    /// <c>403</c> <c>forbidden</c> with the exact message naming the allowed roles. Never <c>403</c> for an allowed role,
    /// and never anything but <c>403</c> for a refused one.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoleCases))]
    public async Task Caller_gets_the_allowed_result_or_403(string endpoint, string caller)
    {
        var row = Row(endpoint);
        var roles = caller.Split(',');
        var call = await row.Arrange(app, roles);
        try
        {
            var response = await CallAsync(call.As ?? app.Callers[caller], row.Method, call.Path, call.Body);

            if (row.Action.Allows(roles))
            {
                var body = await response.Content.ReadAsStringAsync(Cancel);
                Assert.True(response.StatusCode == row.Success,
                    $"{endpoint} as {caller}: expected {(int)row.Success}, got {(int)response.StatusCode} {body}");
            }
            else
            {
                await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", row.Action.ForbiddenMessage);
            }
        }
        finally
        {
            if (call.CleanUp is not null)
            {
                await call.CleanUp();
            }
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static MatrixRow Row(string key) => Endpoints.Single(row => row.Key == key);

    /// <summary>A call with no set-up, as a finished task (rows that only need a path and maybe a body).</summary>
    private static Task<Call> Plain(string path, object? body = null) => Task.FromResult(new Call(path, body));

    /// <summary>Sends <paramref name="method"/> to <c>/api/v1</c> + <paramref name="path"/>, with a JSON body when given.</summary>
    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, string method, string path, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), ApiPath + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request, Cancel);
    }

    /// <summary>
    /// The app's API endpoints, keyed <c>METHOD /path</c> like <see cref="MatrixRow.Key"/>, each with its authorization
    /// in the form <see cref="MatrixRow.ExpectedAuthorization"/> uses.
    /// </summary>
    /// <remarks>
    /// <see cref="EndpointDataSource"/> is ASP.NET Core routing's list of every endpoint the app maps (each controller
    /// action is one). Its route pattern carries constraints such as <c>{id:int}</c>, which design 4 does not write, so
    /// they are removed. Each endpoint's <em>metadata</em> holds the attributes on the action and its controller:
    /// <see cref="IAuthorizeData"/> is <c>[Authorize]</c> (with its <c>Roles</c>), <see cref="IAllowAnonymous"/> is
    /// <c>[AllowAnonymous]</c>.
    /// </remarks>
    private static Dictionary<string, string> ServedEndpoints(TestApiFactory app)
    {
        var served = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText?.TrimStart('/') ?? "";
            if (!pattern.StartsWith("api/v1/", StringComparison.Ordinal))
            {
                continue; // infrastructure, such as /health (architecture 4.1 item 1)
            }

            var template = Regex.Replace(pattern["api/v1".Length..], @"\{(\w+):[^}]*\}", "{$1}");
            var authorization = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                ? MatrixRow.Anonymous
                : string.Join(" + ", endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(data => data.Roles).Where(roles => roles is not null));
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                served.Add($"{method} {template}", authorization);
            }
        }

        return served;
    }

    // ---- The shared app ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The app every case shares, set up once through the API: the start-up Manager's client, one signed-in user per
    /// caller of <see cref="Callers"/> (created through <c>POST /users</c>), and the records that read-only rows look at:
    /// a unit, a raw material with stock and its receipt movement, a product with a recipe, an order and a complementary
    /// order (both cancelled), a Pending return and a notification. xUnit calls <see cref="InitializeAsync"/> before the
    /// first case (<see cref="IAsyncLifetime"/>) and disposes the app after the last.
    /// </summary>
    public sealed class MatrixApp : TestApiFactory, IAsyncLifetime
    {
        private readonly Dictionary<string, HttpClient> _callers = [];

        /// <summary>The start-up Manager (US-02), who arranges every case's records.</summary>
        public HttpClient Admin { get; private set; } = null!;

        /// <summary>A signed-in client for each entry of <see cref="Callers"/>.</summary>
        public IReadOnlyDictionary<string, HttpClient> Callers => _callers;

        /// <summary>The unit every arranged item uses.</summary>
        public CreatedUnit Unit { get; private set; } = null!;

        /// <summary>A raw material with plenty of Warehouse stock, for receipts, adjustments and recipes.</summary>
        public CreatedItem Shelf { get; private set; } = null!;

        /// <summary>A finished product whose recipe is 1 of <see cref="Shelf"/>.</summary>
        public CreatedItem Product { get; private set; } = null!;

        /// <summary>The id of <see cref="Shelf"/>'s receipt movement.</summary>
        public int MovementId { get; private set; }

        /// <summary>A cancelled order (it was InProduction), parent of <see cref="SharedComplementary"/>.</summary>
        public int SharedOrder { get; private set; }

        /// <summary>A complementary order, Cancelled with its parent.</summary>
        public int SharedComplementary { get; private set; }

        /// <summary>A Pending return.</summary>
        public int SharedReturn { get; private set; }

        /// <summary>A notification (the complementary request's ComplementaryAwaitingApproval).</summary>
        public int SharedNotification { get; private set; }

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            Admin = await StartUpManagerAsync(this);
            foreach (var caller in RoleMatrixTests.Callers)
            {
                _callers[caller] = (await CreateStaffAsync(this, Admin, Seed.UniqueEmail("caller"), caller.Split(','))).Client;
            }

            Unit = await Seed.CreateUnitAsync(Admin);
            Shelf = await Seed.CreateItemAsync(Admin, ItemType.RawMaterial, Unit);
            var receipt = await Admin.PostAsJsonAsync($"{ApiPath}/receipts",
                new { itemId = Shelf.Id, quantity = 100_000m, receiptDate = "2026-11-01" }, Cancel);
            Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
            MovementId = (await ApiJson.ReadAsync(receipt)).GetProperty("id").GetInt32();
            Product = await ProductWithRecipeAsync(Admin, (Shelf.Id, 1m));

            var (order, complementary, _) = await PendingComplementaryAsync();
            (SharedOrder, SharedComplementary) = (order, complementary);
            await EndOrderAsync(order);
            SharedNotification = (await AllRowsAsync(Admin, $"{ApiPath}/notifications"))
                .Single(row => row.GetProperty("type").GetString() == "ComplementaryAwaitingApproval").GetProperty("id").GetInt32();
            SharedReturn = await InstructedReturnAsync(Admin, (await FreeLeftoverAsync()).Id, 5m);
        }

        /// <summary>A new user (Warehouse only, unless given roles) through <c>POST /users</c>; returns the id.</summary>
        public Task<string> NewUserAsync() => ScenarioApi.CreateUserAsync(Admin, Seed.UniqueEmail("matrix"), Roles.Warehouse);

        /// <summary>A new raw material in <see cref="Unit"/>, with <paramref name="received"/> received when above 0.</summary>
        public async Task<CreatedItem> MaterialAsync(decimal received = 0m)
        {
            var material = await Seed.CreateItemAsync(Admin, ItemType.RawMaterial, Unit);
            if (received > 0m)
            {
                await ReceiveAsync(Admin, material.Id, received);
            }

            return material;
        }

        /// <summary>
        /// A Created order for 10 of a new product whose recipe is 1 of a new material with 100 in the Warehouse. A new
        /// material each time, so no leftover from an earlier case changes the send range.
        /// </summary>
        public async Task<(int Number, CreatedItem Material)> CreatedOrderAsync()
        {
            var material = await MaterialAsync(received: 100m);
            var product = await ProductWithRecipeAsync(Admin, (material.Id, 1m));
            return (await CreateOrderAsync(Admin, product.Id, 10m), material);
        }

        /// <summary><see cref="CreatedOrderAsync"/>, sent (10) by the start-up Manager, so it is InProduction.</summary>
        public async Task<(int Number, CreatedItem Material)> InProductionOrderAsync()
        {
            var (number, material) = await CreatedOrderAsync();
            await ExpectAsync(HttpStatusCode.OK, SendAsync(Admin, number, (material.Id, 10m)));
            return (number, material);
        }

        /// <summary>An InProduction order with a PendingApproval complementary request for 5 of its material.</summary>
        public async Task<(int Number, int Complementary, CreatedItem Material)> PendingComplementaryAsync()
        {
            var (number, material) = await InProductionOrderAsync();
            return (number, await RequestedComplementaryAsync(Admin, number, "Hopper spill", (material.Id, 5m)), material);
        }

        /// <summary>A new material with 5 of free leftover in production storage (a Manager's adjustment).</summary>
        public async Task<CreatedItem> FreeLeftoverAsync()
        {
            var material = await MaterialAsync();
            await AdjustAsync(Admin, material.Id, "ProductionStorage", 5m, "Leftover from an earlier run");
            return material;
        }

        /// <summary>
        /// Clean-up: cancels order <paramref name="number"/> so it no longer blocks the next send (AC-15.10). A case that
        /// completed or cancelled it already gets <c>409</c> here, which is just as good.
        /// </summary>
        public async Task EndOrderAsync(int number)
        {
            var response = await CancelAsync(Admin, number, "Matrix case finished");
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Cancelling order #{number} gave {(int)response.StatusCode}.");
        }

        /// <summary>Awaits a set-up request and checks its status, so a broken set-up fails where it broke.</summary>
        public async Task ExpectAsync(HttpStatusCode status, Task<HttpResponseMessage> request) =>
            Assert.Equal(status, (await request).StatusCode);
    }
}

/// <summary>
/// One PRD 4.2 action and the roles marked "yes" for it (<see cref="RoleMatrixTests.Prd42"/>).
/// </summary>
public sealed record PrdAction(string Text, params string[] Allowed)
{
    /// <summary>AC-05.2: a user may do the action when they hold at least one of its roles.</summary>
    public bool Allows(IEnumerable<string> roles) => roles.Any(Allowed.Contains);

    /// <summary>
    /// Design 7.1's <c>403</c> message for this action, typed out in <see cref="ExpectedMessages"/>. An action every role
    /// may do has none.
    /// </summary>
    public string ForbiddenMessage => string.Join(",", Allowed) switch
    {
        Roles.Manager => ExpectedMessages.ManagerOnly,
        Roles.WarehouseOrManager => ExpectedMessages.WarehouseOrManager,
        Roles.ProductionOrManager => ExpectedMessages.ProductionOrManager,
        _ => throw new InvalidOperationException($"'{Text}' is open to every role; it has no 403."),
    };
}

/// <summary>
/// One row of the matrix: an endpoint of design section 4 (method and path template relative to <c>/api/v1</c>), the
/// PRD 4.2 action it performs, its success status (design 4), how to arrange a real request, and whether it needs a
/// token (only sign-in does not, AC-01.5).
/// </summary>
public sealed record MatrixRow(
    string Method, string Template, PrdAction Action, HttpStatusCode Success, Arrange Arrange, bool NeedsToken = true)
{
    /// <summary>The authorization value <see cref="ExpectedAuthorization"/> uses for <c>[AllowAnonymous]</c>.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>The case name: <c>METHOD /path</c>, as in design section 4.</summary>
    public string Key => $"{Method} {Template}";

    /// <summary>
    /// What the endpoint's attributes must say for PRD 4.2 to hold: <c>anonymous</c> for sign-in; nothing (the fallback
    /// "signed in" policy, architecture 6) when every role may; otherwise the allowed roles, comma-separated, as one
    /// <c>[Authorize(Roles = ...)]</c> (for example <c>Warehouse,Manager</c>).
    /// </summary>
    public string ExpectedAuthorization =>
        !NeedsToken ? Anonymous
        : Action.Allowed.Length == Roles.All.Count ? ""
        : string.Join(",", Action.Allowed);
}

/// <summary>
/// Makes, through the API, whatever one case needs (for example a Created order to send), and returns the request to
/// make. Gets the shared app and the roles of the caller the case is for.
/// </summary>
public delegate Task<Call> Arrange(RoleMatrixTests.MatrixApp app, string[] callerRoles);

/// <summary>
/// The request one case makes: a path relative to <c>/api/v1</c>, an optional JSON body, an optional client to send it
/// with instead of the caller's own, and an optional clean-up that runs after the case, whatever its outcome.
/// </summary>
public sealed record Call(string Path, object? Body = null, HttpClient? As = null, Func<Task>? CleanUp = null);
