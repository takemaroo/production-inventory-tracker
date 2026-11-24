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
/// T-020, (v1.1) T-023: the full role matrix (AC-05.1, AC-05.2, AC-01.5, AC-30.4 to AC-30.8). One table,
/// <see cref="Endpoints"/>, lists every endpoint of design section 4 next to the PRD 4.2 action it performs;
/// <see cref="Prd42"/> is the PRD 4.2 table itself, action by action, with the roles marked "yes". From those two tables
/// every endpoint is called without a token (<c>401</c>) and as each of the six <see cref="Callers"/>: a caller holding
/// any allowed role gets the endpoint's success status on a real record, and any other caller gets <c>403</c> with
/// design 7.1's exact message. A third test compares the table with the endpoints the running app actually serves, so an
/// endpoint added later without a row (or a row whose roles differ from its <c>[Authorize]</c>) fails.
/// </summary>
/// <remarks>
/// <para>
/// (v1.1) Reads are limited by role too (decision #147), so the matrix is also what proves that no v1 role lost a read:
/// the Warehouse, Production and Manager callers get exactly the v1 results on every v1 endpoint (architecture 9,
/// risk 14). The Seller cases and the tests after the theory prove AC-30.6 and AC-30.7 with the endpoints typed out,
/// so a mistake in <see cref="Prd42"/> itself cannot hide behind the matrix.
/// </para>
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
    /// PRD section 4.2, "Roles and permissions", (v1.1) table, row for row: each action and the roles marked "yes" for it.
    /// A user may do an action when they hold at least one of its roles (AC-05.2, AC-30.8). Viewing is limited by role
    /// too: a "View" row with a blank cell means that role gets <c>403</c> for that read (decision #147).
    /// </summary>
    /// <remarks>
    /// Each row lists its roles in <see cref="Roles.All"/> order (Warehouse, Production, Manager, Seller), the order in
    /// which the <c>[Authorize(Roles = ...)]</c> constants are written, so <see cref="MatrixRow.ExpectedAuthorization"/>
    /// can compare the two as plain text. The last four rows are new in v1.1; their endpoints arrive in T-024 to T-030,
    /// which add the matrix rows that use them.
    /// </remarks>
    public static class Prd42
    {
        /// <summary>"Sign in, change own password": every role, (v1.1) Seller included (AC-30.7).</summary>
        public static readonly PrdAction SignInAndOwnPassword =
            new("Sign in, change own password", Roles.Warehouse, Roles.Production, Roles.Manager, Roles.Seller);

        /// <summary>
        /// (v1.1, split from the v1 "View" row) "View units, items, stock": every role (AC-30.7, decision #151).
        /// </summary>
        public static readonly PrdAction ViewUnitsItemsStock =
            new("View units, items, stock", Roles.Warehouse, Roles.Production, Roles.Manager, Roles.Seller);

        /// <summary>
        /// "View recipes, movement history, orders, complementary orders, returns": Warehouse, Production, Manager. The v1
        /// roles keep every v1 read; (v1.1) a Seller gets <c>403</c> (AC-30.6).
        /// </summary>
        public static readonly PrdAction ViewRecipesHistoryAndOrders = new(
            "View recipes, movement history, orders, complementary orders, returns",
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

        /// <summary>(v1.1) "View customers, sales orders and shipped-totals reports": Warehouse, Manager, Seller.</summary>
        public static readonly PrdAction ViewSales = new(
            "View customers, sales orders and shipped-totals reports", Roles.Warehouse, Roles.Manager, Roles.Seller);

        /// <summary>(v1.1) "Create, edit, deactivate, reactivate or delete a customer": Manager, Seller.</summary>
        public static readonly PrdAction ManageCustomers =
            new("Create, edit, deactivate, reactivate or delete a customer", Roles.Manager, Roles.Seller);

        /// <summary>(v1.1) "Create or cancel a sales order": Manager, Seller.</summary>
        public static readonly PrdAction CreateOrCancelSalesOrder =
            new("Create or cancel a sales order", Roles.Manager, Roles.Seller);

        /// <summary>(v1.1) "Ship a sales order": Warehouse, Manager.</summary>
        public static readonly PrdAction ShipSalesOrder = new("Ship a sales order", Roles.Warehouse, Roles.Manager);
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

        new("GET", "/units", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (_, _) => Plain("/units")),
        new("GET", "/units/{id}", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (a, _) => Plain($"/units/{a.Unit.Id}")),
        new("POST", "/units", Prd42.ManageMasterData, HttpStatusCode.Created, (_, _) => Plain("/units", new { name = Seed.UniqueName("unit") })),
        new("PUT", "/units/{id}", Prd42.ManageMasterData, HttpStatusCode.OK,
            async (a, _) => new Call($"/units/{(await Seed.CreateUnitAsync(a.Admin)).Id}", new { name = Seed.UniqueName("unit") })),
        new("DELETE", "/units/{id}", Prd42.ManageMasterData, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/units/{(await Seed.CreateUnitAsync(a.Admin)).Id}")),

        new("GET", "/items", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (_, _) => Plain("/items")),
        new("GET", "/items/{id}", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (a, _) => Plain($"/items/{a.Shelf.Id}")),
        new("POST", "/items", Prd42.ManageMasterData, HttpStatusCode.Created,
            (a, _) => Plain("/items", new { name = Seed.UniqueName("item"), type = "RawMaterial", unitId = a.Unit.Id })),
        new("PUT", "/items/{id}", Prd42.ManageMasterData, HttpStatusCode.OK, async (a, _) =>
            new Call($"/items/{(await a.MaterialAsync()).Id}",
                new { name = Seed.UniqueName("item"), description = "Renamed", type = "RawMaterial", unitId = a.Unit.Id })),
        new("DELETE", "/items/{id}", Prd42.ManageMasterData, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/items/{(await a.MaterialAsync()).Id}")),
        new("GET", "/items/{id}/recipe", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
            (a, _) => Plain($"/items/{a.Product.Id}/recipe")),
        new("PUT", "/items/{id}/recipe", Prd42.ManageMasterData, HttpStatusCode.OK, async (a, _) =>
            new Call($"/items/{(await Seed.CreateItemAsync(a.Admin, ItemType.FinishedProduct, a.Unit)).Id}/recipe",
                new { lines = new[] { new { itemId = a.Shelf.Id, amountPerUnit = 1m } } })),

        new("GET", "/stock", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (_, _) => Plain("/stock")),
        new("GET", "/stock/{itemId}", Prd42.ViewUnitsItemsStock, HttpStatusCode.OK, (a, _) => Plain($"/stock/{a.Shelf.Id}")),
        new("GET", "/movements", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK, (_, _) => Plain("/movements")),
        new("GET", "/movements/{id}", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
            (a, _) => Plain($"/movements/{a.MovementId}")),
        new("POST", "/receipts", Prd42.RecordReceipt, HttpStatusCode.Created,
            (a, _) => Plain("/receipts", new { itemId = a.Shelf.Id, quantity = 1m, receiptDate = "2026-11-01" })),
        new("POST", "/adjustments", Prd42.MakeAdjustment, HttpStatusCode.Created,
            (a, _) => Plain("/adjustments", new { itemId = a.Shelf.Id, location = "Warehouse", quantity = 1m, reason = "Count" })),

        new("GET", "/production-orders", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
            (_, _) => Plain("/production-orders")),
        new("GET", "/production-orders/{number}", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
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

        new("GET", "/complementary-orders", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
            (_, _) => Plain("/complementary-orders")),
        new("GET", "/complementary-orders/{number}", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
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

        new("GET", "/returns", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK, (_, _) => Plain("/returns")),
        new("GET", "/returns/{number}", Prd42.ViewRecipesHistoryAndOrders, HttpStatusCode.OK,
            (a, _) => Plain($"/returns/{a.SharedReturn}")),
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

        // (v1.1) T-024, US-29: the reads look at one shared customer; every change gets a fresh customer of its own, so a
        // delete or deactivate in one case never changes what another case acts on.
        new("GET", "/customers", Prd42.ViewSales, HttpStatusCode.OK, (_, _) => Plain("/customers")),
        new("GET", "/customers/{id}", Prd42.ViewSales, HttpStatusCode.OK, (a, _) => Plain($"/customers/{a.SharedCustomer}")),
        new("POST", "/customers", Prd42.ManageCustomers, HttpStatusCode.Created,
            (_, _) => Plain("/customers", new { name = Seed.UniqueName("Customer"), notes = "Matrix" })),
        new("PUT", "/customers/{id}", Prd42.ManageCustomers, HttpStatusCode.OK,
            async (a, _) => new Call($"/customers/{await a.NewCustomerAsync()}", new { name = Seed.UniqueName("Customer") })),
        new("POST", "/customers/{id}/deactivate", Prd42.ManageCustomers, HttpStatusCode.OK,
            async (a, _) => new Call($"/customers/{await a.NewCustomerAsync()}/deactivate")),
        new("POST", "/customers/{id}/reactivate", Prd42.ManageCustomers, HttpStatusCode.OK, async (a, _) =>
        {
            var id = await a.NewCustomerAsync();
            await a.ExpectAsync(HttpStatusCode.OK, a.Admin.PostAsync($"{ApiPath}/customers/{id}/deactivate", content: null, Cancel));
            return new Call($"/customers/{id}/reactivate");
        }),
        new("DELETE", "/customers/{id}", Prd42.ManageCustomers, HttpStatusCode.NoContent,
            async (a, _) => new Call($"/customers/{await a.NewCustomerAsync()}")),

        // (v1.1) T-025, US-24, US-25: the reads look at one shared sales order. Each create adds a new sales order for the
        // shared customer and item; creating moves and checks no stock (AC-24.2), so the cases never compete.
        new("GET", "/sales-orders", Prd42.ViewSales, HttpStatusCode.OK, (_, _) => Plain("/sales-orders")),
        new("GET", "/sales-orders/{number}", Prd42.ViewSales, HttpStatusCode.OK, (a, _) => Plain($"/sales-orders/{a.SharedSalesOrder}")),
        new("POST", "/sales-orders", Prd42.CreateOrCancelSalesOrder, HttpStatusCode.Created, (a, _) => Plain("/sales-orders",
            new { customerId = a.SharedCustomer, lines = new[] { new { itemId = a.Shelf.Id, quantity = 5m } } })),

        // (v1.1) T-027, US-26: each case ships a new Created sales order of 1 of the shared material, which holds far more
        // Warehouse stock than every case together takes, so an allowed caller's 200 never depends on the other cases.
        new("POST", "/sales-orders/{number}/ship", Prd42.ShipSalesOrder, HttpStatusCode.OK, async (a, _) =>
            new Call($"/sales-orders/{await SalesFlow.CreateSalesOrderAsync(a.Admin, a.SharedCustomer, (a.Shelf.Id, 1m))}/ship",
                new { note = "Matrix" })),

        // (v1.1) T-028, US-27: each case cancels a new Created sales order of its own, so a refused caller's 403 and an
        // allowed caller's 200 never depend on another case having cancelled it first.
        new("POST", "/sales-orders/{number}/cancel", Prd42.CreateOrCancelSalesOrder, HttpStatusCode.OK, async (a, _) =>
            new Call($"/sales-orders/{await SalesFlow.CreateSalesOrderAsync(a.Admin, a.SharedCustomer, (a.Shelf.Id, 1m))}/cancel",
                new { reason = "Matrix" })),
    ];

    /// <summary>
    /// The number of endpoints in design section 4 that exist in the app so far. The 48 of version 1; each v1.1 endpoint
    /// task raises it by the endpoints it adds (T-024 to 55, T-025 to 58, T-027 to 59, T-028 to 60, T-030 to 62,
    /// decision #164). <c>OpenApiTests</c> uses it too, so the two lists cannot disagree on the count. Now 60: the 48 of
    /// version 1, T-024's seven customer endpoints, T-025's three sales order endpoints (list, read one, create),
    /// T-027's ship and T-028's cancel.
    /// </summary>
    public const int DesignEndpointCount = 60;

    /// <summary>
    /// The callers of the matrix (decision #164), as role lists: one user per single role (AC-05.1, AC-30.4), Warehouse
    /// plus Production (AC-05.2's own example: such a user may both send materials and complete an order, but nothing only
    /// a Manager may do), and (v1.1) Production plus Seller (AC-30.8: the reads of both roles, from one user).
    /// </summary>
    public static readonly IReadOnlyList<string> Callers =
    [
        Roles.Warehouse, Roles.Production, Roles.Manager, Roles.Seller,
        $"{Roles.Warehouse},{Roles.Production}", $"{Roles.Production},{Roles.Seller}",
    ];

    /// <summary>One case per endpoint, named <c>METHOD /path</c>, for the <c>401</c> test.</summary>
    /// <remarks>
    /// <c>[MemberData(nameof(EndpointCases))]</c> on a <c>[Theory]</c> tells xUnit to run the test once for each entry of
    /// this property. Each case is reported on its own, named by its values (for example
    /// <c>endpoint: "POST /receipts", caller: "Production"</c>), so a failure names the endpoint and the caller.
    /// The cases are plain strings, not the rows themselves, because xUnit must be able to write each case's values out.
    /// </remarks>
    public static TheoryData<string> EndpointCases => new(Endpoints.Select(row => row.Key));

    /// <summary>One case per endpoint and caller, for the role test: <see cref="DesignEndpointCount"/> × 6.</summary>
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
    /// routing, so the test sees what the app really serves), the <see cref="DesignEndpointCount"/> of design section 4,
    /// and each endpoint's <c>[Authorize(Roles = ...)]</c> or <c>[AllowAnonymous]</c> says what the (v1.1) PRD 4.2 row
    /// says. An endpoint added without a row, a row without an endpoint, or roles that differ, fail here with the endpoint
    /// named. (v1.1) Because every attribute on an endpoint is read, a controller-level attribute that combines with an
    /// action's own (decision #156) shows up as "A + B" and fails too. Also AC-13.6 and AC-11.3: since the lists are equal,
    /// no endpoint edits an order or edits or deletes a movement.
    /// </summary>
    [Fact]
    public void Table_lists_every_endpoint_the_app_serves_with_its_roles()
    {
        var served = ServedEndpoints(app);

        Assert.Equal(DesignEndpointCount, Endpoints.Count);
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
    /// AC-05.1, AC-05.2, AC-30.4, AC-30.8, design 2.2 and 7.1: for every endpoint and caller, a caller holding at least
    /// one role PRD 4.2 marks "yes" for the endpoint's action gets its success status on a real record, and a caller
    /// holding none gets <c>403</c> <c>forbidden</c> with the exact message naming the allowed roles (for a Seller on a
    /// v1 read, "It needs the Warehouse, Production or Manager role."). Never <c>403</c> for an allowed role, and never
    /// anything but <c>403</c> for a refused one.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoleCases))]
    public async Task Caller_gets_the_allowed_result_or_403(string endpoint, string caller)
    {
        await CallAsAsync(endpoint, caller, async (row, response) =>
        {
            if (row.Action.Allows(caller.Split(',')))
            {
                await AssertStatusAsync(response, row.Success, $"{endpoint} as {caller}");
            }
            else
            {
                await ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", row.Action.ForbiddenMessage);
            }
        });
    }

    // ---- (v1.1) The Seller's column, typed out ----------------------------------------------------------------------
    //
    // The theory above takes each expected result from Prd42. These tests type the endpoints of AC-30.6 to AC-30.8 out
    // instead, with the status or message each must give, so a wrong Prd42 row would fail here even though the theory
    // would agree with it.

    /// <summary>
    /// AC-30.7: a Seller-only user gets <c>200</c> on the unit, item and stock reads (all items and one item, decision
    /// #151) and <c>204</c> on changing their own password. These keep the fallback "signed in" policy, so no role is
    /// refused.
    /// </summary>
    [Theory]
    [InlineData("GET /units", HttpStatusCode.OK)]
    [InlineData("GET /units/{id}", HttpStatusCode.OK)]
    [InlineData("GET /items", HttpStatusCode.OK)]
    [InlineData("GET /items/{id}", HttpStatusCode.OK)]
    [InlineData("GET /stock", HttpStatusCode.OK)]
    [InlineData("GET /stock/{itemId}", HttpStatusCode.OK)]
    [InlineData("POST /auth/change-password", HttpStatusCode.NoContent)]
    public Task Seller_reads_units_items_and_stock_and_changes_own_password(string endpoint, HttpStatusCode status) =>
        CallAsAsync(endpoint, Roles.Seller, (_, response) => AssertStatusAsync(response, status, $"{endpoint} as Seller"));

    /// <summary>
    /// AC-30.6, AC-28.7, AC-14.1, AC-22.6: a Seller-only user gets <c>403</c> on viewing a recipe, listing or viewing
    /// movements, production orders, complementary orders, returns, users and notifications, each with design 7.1's
    /// sentence for that endpoint's roles.
    /// </summary>
    [Theory]
    [InlineData("GET /items/{id}/recipe", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /movements", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /movements/{id}", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /production-orders", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /production-orders/{number}", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /complementary-orders", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /complementary-orders/{number}", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /returns", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /returns/{number}", ExpectedMessages.WarehouseProductionOrManager)]
    [InlineData("GET /users", ExpectedMessages.ManagerOnly)]
    [InlineData("GET /users/{id}", ExpectedMessages.ManagerOnly)]
    [InlineData("GET /notifications", ExpectedMessages.ManagerOnly)]
    [InlineData("GET /notifications/{id}", ExpectedMessages.ManagerOnly)]
    public Task Seller_gets_403_on_the_reads_of_AC_30_6(string endpoint, string message) =>
        CallAsAsync(endpoint, Roles.Seller,
            (_, response) => ApiAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", message));

    /// <summary>
    /// AC-30.8 (the v1 half), AC-05.2: roles combine for reads as for actions. A user holding Production and Seller gets
    /// <c>200</c> on the movement and production order reads, from their Production role.
    /// </summary>
    [Theory]
    [InlineData("GET /movements")]
    [InlineData("GET /movements/{id}")]
    [InlineData("GET /production-orders")]
    [InlineData("GET /production-orders/{number}")]
    public Task Production_plus_Seller_reads_movements_and_production_orders(string endpoint) =>
        CallAsAsync(endpoint, $"{Roles.Production},{Roles.Seller}",
            (_, response) => AssertStatusAsync(response, HttpStatusCode.OK, $"{endpoint} as Production plus Seller"));

    /// <summary>
    /// AC-30.7, AC-10.3, design 5.5: with an InProduction order, a Seller's <c>GET /stock/{itemId}</c> for its material
    /// shows the same <c>committedOrderNumber</c> as every other role sees, and the Seller's <c>GET
    /// /production-orders/{number}</c> for that very order is <c>403</c>. A Warehouse user opens the same order, so the
    /// <c>403</c> is the role check and not a missing order.
    /// </summary>
    [Fact]
    public async Task Seller_sees_the_committed_order_number_but_cannot_open_the_order()
    {
        var (number, material) = await app.InProductionOrderAsync();
        try
        {
            var seller = app.Callers[Roles.Seller];

            var sellerStock = await GetAsync(seller, $"{ApiPath}/stock/{material.Id}");
            var managerStock = await GetAsync(app.Admin, $"{ApiPath}/stock/{material.Id}");
            Assert.Equal(number, sellerStock.GetProperty("committedOrderNumber").GetInt32());
            Assert.Equal(managerStock.GetRawText(), sellerStock.GetRawText()); // one stock view for all roles (decision #147)

            var order = await seller.GetAsync($"{ApiPath}/production-orders/{number}", Cancel);
            await ApiAssert.ProblemAsync(order, HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseProductionOrManager);

            var control = await app.Callers[Roles.Warehouse].GetAsync($"{ApiPath}/production-orders/{number}", Cancel);
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        }
        finally
        {
            await app.EndOrderAsync(number);
        }
    }

    /// <summary>
    /// Design 2.2 (the role check comes first), architecture 4.1 item 13: a Seller-only user's
    /// <c>GET /production-orders/99999</c> (no such order) is <c>403</c>, not <c>404</c>, and their
    /// <c>GET /movements?type=Nope</c> (an unknown type) is <c>403</c>, not <c>400</c>. The same requests from a Warehouse
    /// user give <c>404</c> and <c>400</c>, which shows that the Seller's <c>403</c> really comes before those checks.
    /// </summary>
    [Fact]
    public async Task Seller_gets_403_before_the_404_and_400_checks()
    {
        var seller = app.Callers[Roles.Seller];
        var warehouse = app.Callers[Roles.Warehouse];

        await ApiAssert.ProblemAsync(await seller.GetAsync($"{ApiPath}/production-orders/99999", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseProductionOrManager);
        await ApiAssert.ProblemAsync(await seller.GetAsync($"{ApiPath}/movements?type=Nope", Cancel),
            HttpStatusCode.Forbidden, "forbidden", ExpectedMessages.WarehouseProductionOrManager);

        Assert.Equal(HttpStatusCode.NotFound, (await warehouse.GetAsync($"{ApiPath}/production-orders/99999", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await warehouse.GetAsync($"{ApiPath}/movements?type=Nope", Cancel)).StatusCode);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static MatrixRow Row(string key) => Endpoints.Single(row => row.Key == key);

    /// <summary>
    /// Arranges <paramref name="endpoint"/>'s row for <paramref name="caller"/>, sends the request as that caller, runs
    /// <paramref name="check"/> on the response, and always runs the row's clean-up afterwards, even when the check fails.
    /// </summary>
    private async Task CallAsAsync(string endpoint, string caller, Func<MatrixRow, HttpResponseMessage, Task> check)
    {
        var row = Row(endpoint);
        var call = await row.Arrange(app, caller.Split(','));
        try
        {
            var response = await CallAsync(call.As ?? app.Callers[caller], row.Method, call.Path, call.Body);
            await check(row, response);
        }
        finally
        {
            if (call.CleanUp is not null)
            {
                await call.CleanUp();
            }
        }
    }

    /// <summary>Asserts the status, and on a mismatch shows the response body, so a failure says why.</summary>
    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        var body = await response.Content.ReadAsStringAsync(Cancel);
        Assert.True(response.StatusCode == expected, $"{what}: expected {(int)expected}, got {(int)response.StatusCode} {body}");
    }

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
    /// order (both cancelled), a Pending return, a notification and (v1.1) a customer with a Created sales order. xUnit calls <see cref="InitializeAsync"/> before the
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

        /// <summary>
        /// (v1.1) The id of an active customer that the two customer reads look at, and that every sales order create
        /// case sells to.
        /// </summary>
        public int SharedCustomer { get; private set; }

        /// <summary>(v1.1) The number of a Created sales order for <see cref="SharedCustomer"/>, which the sales order detail read looks at.</summary>
        public int SharedSalesOrder { get; private set; }

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
            SharedCustomer = await NewCustomerAsync();
            SharedSalesOrder = await SalesFlow.CreateSalesOrderAsync(Admin, SharedCustomer, (Shelf.Id, 1m));
        }

        /// <summary>(v1.1) A new active customer with a unique name, created by the start-up Manager; returns the id.</summary>
        public async Task<int> NewCustomerAsync() => (await SalesFlow.CreateCustomerAsync(Admin)).Id;

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
    /// Design 7.1's <c>403</c> message for this action, typed out in <see cref="ExpectedMessages"/> (decision #88): one
    /// sentence per roles set of design 4. An action every role may do has none.
    /// </summary>
    public string ForbiddenMessage => string.Join(",", Allowed) switch
    {
        Roles.Manager => ExpectedMessages.ManagerOnly,
        Roles.WarehouseOrManager => ExpectedMessages.WarehouseOrManager,
        Roles.ProductionOrManager => ExpectedMessages.ProductionOrManager,
        Roles.WarehouseProductionOrManager => ExpectedMessages.WarehouseProductionOrManager,
        Roles.WarehouseManagerOrSeller => ExpectedMessages.WarehouseManagerOrSeller,
        Roles.ManagerOrSeller => ExpectedMessages.ManagerOrSeller,
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
