using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-014: user management (US-03), changing one's own password (US-04) and AC-01.6, through the real HTTP endpoints.
/// </summary>
/// <remarks>
/// Most tests share one app and database (<see cref="TestApiFactory"/>) and create their own users with unique emails.
/// Tests of the last-Manager rule (AC-03.8) and of list order need to know every user in the database, so they start
/// their own app with an empty database, where the only user is the first Manager from <c>ADMIN_EMAIL</c>.
/// </remarks>
public class UsersTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string UsersPath = "/api/v1/users";
    private const string ChangePasswordPath = "/api/v1/auth/change-password";
    private const string SignInPath = "/api/v1/auth/sign-in";
    private const string NewPassword = "Brand-New-Pass-7";
    private const string WeakPasswordMessage =
        "Password must be at least 8 characters and include an upper-case letter, a lower-case letter and a digit.";
    private const string AccountLockedMessage = "Account temporarily locked. Try again later.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Every user-management endpoint (design 4), with a body that would be valid.</summary>
    public static TheoryData<string, string, string?> UserEndpoints => new()
    {
        { "GET", UsersPath, null },
        { "POST", UsersPath, """{ "email": "x@inventory.test", "password": "Initial2026", "roles": ["Warehouse"] }""" },
        { "GET", UsersPath + "/{id}", null },
        { "PUT", UsersPath + "/{id}/roles", """{ "roles": ["Production"] }""" },
        { "POST", UsersPath + "/{id}/deactivate", null },
        { "POST", UsersPath + "/{id}/reactivate", null },
        { "PUT", UsersPath + "/{id}/password", """{ "newPassword": "Reset-2026a" }""" },
    };

    // ---- 401 and 403 -----------------------------------------------------------------------------------------

    /// <summary>
    /// AC-01.6 and AC-01.5: no endpoint lets an unauthenticated caller create a user. POST /users without a token is
    /// 401 not-authenticated, and no user is created.
    /// </summary>
    [Fact]
    public async Task Creating_a_user_without_a_token_gives_401_and_creates_nothing()
    {
        var email = UniqueEmail();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(UsersPath,
            new { email, password = "Initial2026", roles = new[] { "Manager" } }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated",
            "Your sign-in is missing or has expired. Sign in again.");
        Assert.False(await UserExistsAsync(factory, email));
    }

    /// <summary>AC-01.5: every user-management endpoint answers 401 without a token.</summary>
    [Theory]
    [MemberData(nameof(UserEndpoints))]
    public async Task Every_user_endpoint_gives_401_without_a_token(string method, string path, string? body)
    {
        var target = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);
        using var anonymous = factory.CreateClient();

        var response = await SendAsync(anonymous, method, path.Replace("{id}", target.Id), body);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated",
            "Your sign-in is missing or has expired. Sign in again.");
    }

    /// <summary>
    /// AC-03.9: a user without the Manager role gets 403 on every user-management endpoint, including a user holding
    /// both other roles, and nothing changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(UserEndpoints))]
    public async Task Every_user_endpoint_gives_403_without_the_Manager_role(string method, string path, string? body)
    {
        var target = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);
        var stampBefore = await StampAsync(factory, target.Id);

        foreach (var roles in new[] { new[] { Roles.Warehouse }, [Roles.Production], [Roles.Warehouse, Roles.Production] })
        {
            using var client = await factory.CreateClientAsAsync(roles);
            var response = await SendAsync(client, method, path.Replace("{id}", target.Id), body);

            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden",
                "Your roles do not allow this action. It needs the Manager role.");
        }

        var user = await LoadUserAsync(factory, target.Id);
        Assert.True(user.IsActive);
        Assert.Equal(stampBefore, user.SecurityStamp);
        Assert.Equal([Roles.Warehouse], await RolesInDatabaseAsync(factory, target.Id));
    }

    /// <summary>
    /// AC-03.9 and design 2.2 (403 before 404): a non-Manager naming a user id that does not exist gets 403, not 404,
    /// so a user without the role cannot learn which ids exist.
    /// </summary>
    [Theory]
    [InlineData("GET", "/no-such-user", null)]
    [InlineData("PUT", "/no-such-user/roles", """{ "roles": ["Manager"] }""")]
    [InlineData("POST", "/no-such-user/deactivate", null)]
    [InlineData("PUT", "/no-such-user/password", """{ "newPassword": "Reset-2026a" }""")]
    public async Task Role_check_comes_before_the_unknown_id_check(string method, string path, string? body)
    {
        using var client = await factory.CreateClientAsAsync(Roles.Warehouse, Roles.Production);

        var response = await SendAsync(client, method, UsersPath + path, body);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden",
            "Your roles do not allow this action. It needs the Manager role.");
    }

    /// <summary>AC-01.5: POST /auth/change-password without a token is 401.</summary>
    [Fact]
    public async Task Change_password_without_a_token_gives_401()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(ChangePasswordPath,
            new { currentPassword = TestApiFactory.UserPassword, newPassword = NewPassword }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated",
            "Your sign-in is missing or has expired. Sign in again.");
    }

    /// <summary>
    /// PRD 4.2: every role may change its own password, so this endpoint has no role that gets 403. Each role on its
    /// own gets 204 (the role-check counterpart of the 403 tests).
    /// </summary>
    [Theory]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Production)]
    [InlineData(Roles.Manager)]
    public async Task Every_role_may_change_its_own_password(string role)
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, role);
        using var client = factory.CreateClientWithToken(await factory.SignInAsync(email));

        var response = await ChangePasswordAsync(client, TestApiFactory.UserPassword, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- AC-03.1 to AC-03.3: creating users ------------------------------------------------------------------

    /// <summary>
    /// AC-03.1: a Manager creates a user: 201 with the design 5.2 user object and a Location header; the new user can
    /// sign in and holds exactly the roles given. Role names are accepted in any letter case (design 2.3).
    /// </summary>
    [Fact]
    public async Task Manager_creates_a_user_who_can_sign_in_with_exactly_those_roles()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();

        var response = await manager.PostAsJsonAsync(UsersPath,
            new { email, password = "Initial2026", roles = new[] { "production", "Warehouse", "PRODUCTION" } }, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetString()!;
        Assert.Equal($"/api/v1/users/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(["Warehouse", "Production"], RoleNames(body));
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.EndsWith("Z", body.GetProperty("createdAt").GetString());
        Assert.Equal(["id", "email", "roles", "isActive", "createdAt"], body.EnumerateObject().Select(field => field.Name));

        var (signInStatus, signIn) = await SignInRawAsync(email, "Initial2026");
        Assert.Equal(HttpStatusCode.OK, signInStatus);
        Assert.Equal(["Warehouse", "Production"], RoleNames(signIn));
        Assert.Equal([Roles.Production, Roles.Warehouse], (await RolesInDatabaseAsync(factory, id)).Order());
    }

    /// <summary>
    /// Decision #64 and architecture 3.2: the email is trimmed and stored with its letter case, and UserName is exactly
    /// the same text as Email (checked in the database).
    /// </summary>
    [Fact]
    public async Task Created_user_has_UserName_equal_to_the_trimmed_Email()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var stored = $"Mixed.Case-{Guid.NewGuid():N}@Example.com";

        var response = await manager.PostAsJsonAsync(UsersPath,
            new { email = "  " + stored + " ", password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(stored, body.GetProperty("email").GetString());
        var user = await LoadUserAsync(factory, body.GetProperty("id").GetString()!);
        Assert.Equal(stored, user.Email);
        Assert.Equal(user.Email, user.UserName);
        Assert.Equal(user.NormalizedEmail, user.NormalizedUserName);
    }

    /// <summary>
    /// AC-03.2 and decision #64: "Ana@Example.com" exists, so " ana@example.com " (another letter case, with spaces) is
    /// refused with 409 email-taken and the PRD message, and no second user is saved.
    /// </summary>
    [Fact]
    public async Task Duplicate_email_ignoring_case_and_spaces_gives_409()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var first = await manager.PostAsJsonAsync(UsersPath,
            new { email = "Ana@Example.com", password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await manager.PostAsJsonAsync(UsersPath,
            new { email = " ana@example.com ", password = "Initial2026", roles = new[] { "Manager" } }, Cancel);

        await AssertProblemAsync(second, HttpStatusCode.Conflict, "email-taken", "A user with this email already exists.");
        Assert.Equal(1, await CountUsersAsync(factory, "ANA@EXAMPLE.COM"));
    }

    /// <summary>
    /// AC-03.2 under concurrency (PRD section 5): two simultaneous creates for the same email give one 201 and one 409
    /// email-taken, and exactly one user exists.
    /// </summary>
    [Fact]
    public async Task Simultaneous_creates_for_one_email_give_one_201_and_one_409()
    {
        var token = await ManagerTokenAsync(factory);
        var email = UniqueEmail();
        var clients = Enumerable.Range(0, 2).Select(_ => factory.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(clients.Select((client, index) => client.PostAsJsonAsync(UsersPath,
            new { email = index == 0 ? email : email.ToUpperInvariant(), password = "Initial2026", roles = new[] { "Warehouse" } },
            Cancel)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(response => response.StatusCode).Order());
        var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("email-taken", (await ReadJsonAsync(conflict)).GetProperty("code").GetString());
        Assert.Equal(1, await CountUsersAsync(factory, email.ToUpperInvariant()));
        clients.ForEach(client => client.Dispose());
    }

    /// <summary>
    /// AC-03.3 and design 7.2: an invalid email (including one with a space or tab inside, decision #64), an email
    /// longer than 256 characters, a missing email, an empty or missing role list, an unknown role and a missing
    /// password are each refused with 400 validation naming the problem, and no user is created.
    /// </summary>
    [Theory]
    [InlineData("""{ "email": "ana smith@example.com", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is not a valid email address.")]
    [InlineData("""{ "email": "ana\tsmith@example.com", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is not a valid email address.")]
    [InlineData("""{ "email": "{long-email}", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email must be 256 characters or fewer.")]
    [InlineData("""{ "email": "not-an-email", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is not a valid email address.")]
    [InlineData("""{ "email": "ana@", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is not a valid email address.")]
    [InlineData("""{ "email": "   ", "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is required.")]
    [InlineData("""{ "password": "Initial2026", "roles": ["Warehouse"] }""", "email", "Email is required.")]
    [InlineData("""{ "email": "{email}", "password": "Initial2026", "roles": [] }""", "roles", "At least one role is required.")]
    [InlineData("""{ "email": "{email}", "password": "Initial2026" }""", "roles", "At least one role is required.")]
    [InlineData("""{ "email": "{email}", "password": "Initial2026", "roles": ["Warehouse", "Admin"] }""", "roles[1]", "Unknown role 'Admin'. Roles are Warehouse, Production and Manager.")]
    [InlineData("""{ "email": "{email}", "roles": ["Warehouse"] }""", "password", "Password is required.")]
    public async Task Invalid_create_input_gives_400_validation(string json, string path, string message)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var usersBefore = await CountUsersAsync(factory);

        // 245 + 12 = 257 characters: one more than Identity's 256-character Email and UserName columns.
        var tooLong = new string('a', 245) + "@example.com";
        var response = await SendAsync(manager, "POST", UsersPath,
            json.Replace("{email}", email).Replace("{long-email}", tooLong));

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Equal([message], body.GetProperty("errors").GetProperty(path).EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(usersBefore, await CountUsersAsync(factory));
    }

    /// <summary>
    /// Architecture 4.1 item 2: a JSON body of <c>null</c> on any user endpoint that needs a body is 400 validation,
    /// never a 500.
    /// </summary>
    [Theory]
    [InlineData("POST", "")]
    [InlineData("PUT", "/{id}/roles")]
    [InlineData("PUT", "/{id}/password")]
    public async Task A_null_body_gives_400(string method, string path)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);

        var response = await SendAsync(manager, method, UsersPath + path.Replace("{id}", user.Id), "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>Design 6: several input problems are reported together, joined with a space in detail.</summary>
    [Fact]
    public async Task Several_create_problems_are_reported_together()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.PostAsJsonAsync(UsersPath,
            new { email = "bad email", password = "Initial2026", roles = Array.Empty<string>() }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Email is not a valid email address. At least one role is required.");
    }

    /// <summary>
    /// AC-03.3 and design 7.1: a password that fails the policy (too short, no digit, no upper-case, no lower-case) is
    /// 400 weak-password with the policy message, and no user is created.
    /// </summary>
    [Theory]
    [InlineData("Short1a")]
    [InlineData("NoDigitsHere")]
    [InlineData("lower-case-only-1")]
    [InlineData("UPPER-CASE-ONLY-1")]
    public async Task Weak_initial_password_gives_400_weak_password(string password)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();

        var response = await manager.PostAsJsonAsync(UsersPath, new { email, password, roles = new[] { "Warehouse" } }, Cancel);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "weak-password", WeakPasswordMessage);
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out _));
        Assert.False(await UserExistsAsync(factory, email));
    }

    /// <summary>Design 2.2 order: input problems (400) are reported before a duplicate email (409).</summary>
    [Fact]
    public async Task Input_problems_come_before_the_duplicate_check()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Warehouse);

        var response = await manager.PostAsJsonAsync(UsersPath,
            new { email, password = "Initial2026", roles = new[] { "Nobody" } }, Cancel);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation",
            "Unknown role 'Nobody'. Roles are Warehouse, Production and Manager.");
    }

    // ---- AC-03.4: listing and reading users ------------------------------------------------------------------

    /// <summary>
    /// AC-03.4: a Manager lists users with email, roles and active status, in the design 2.5 page envelope. PRD 4.1:
    /// newest first. Runs on its own database so the whole list is known.
    /// </summary>
    [Fact]
    public async Task Manager_lists_users_newest_first_with_email_roles_and_status()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        await using var app = new TestApiFactory { FakeTime = time };
        using var manager = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        time.Advance(TimeSpan.FromMinutes(1));
        var older = await app.CreateUserAsync("older@inventory.test", Roles.Warehouse, Roles.Production);
        time.Advance(TimeSpan.FromMinutes(1));
        var newer = await app.CreateUserAsync("newer@inventory.test", Roles.Production);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"{UsersPath}/{newer.Id}/deactivate", null, Cancel)).StatusCode);

        var response = await manager.GetAsync(UsersPath, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["newer@inventory.test", "older@inventory.test", TestApiFactory.AdminEmail],
            items.Select(item => item.GetProperty("email").GetString()));
        Assert.Equal(newer.Id, items[0].GetProperty("id").GetString());
        Assert.False(items[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(["Production"], RoleNames(items[0]));
        Assert.Equal(older.Id, items[1].GetProperty("id").GetString());
        Assert.True(items[1].GetProperty("isActive").GetBoolean());
        Assert.Equal(["Warehouse", "Production"], RoleNames(items[1]));
        Assert.Equal(["Manager"], RoleNames(items[2]));
        Assert.Equal("2026-11-11T08:02:00Z", items[0].GetProperty("createdAt").GetString());

        var secondPage = await ReadJsonAsync(await manager.GetAsync($"{UsersPath}?page=2&pageSize=2", Cancel));
        Assert.Equal([TestApiFactory.AdminEmail],
            secondPage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("email").GetString()));
    }

    /// <summary>Design 2.5 and 7.2: a page size outside 1 to 100 is 400.</summary>
    [Fact]
    public async Task Listing_with_a_bad_page_size_gives_400()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await manager.GetAsync($"{UsersPath}?pageSize=101", Cancel);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", "pageSize must be between 1 and 100.");
    }

    /// <summary>
    /// AC-03.4: no user response ever contains a password or a password hash: not create, list, read, role change,
    /// deactivate or reactivate. No field beyond the design 5.2 user object appears.
    /// </summary>
    [Fact]
    public async Task No_user_response_contains_a_password_or_hash()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        const string password = "Secret-Initial-2026";
        var created = await manager.PostAsJsonAsync(UsersPath, new { email, password, roles = new[] { "Warehouse" } }, Cancel);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
        var hash = (await LoadUserAsync(factory, id)).PasswordHash!;

        var responses = new List<HttpResponseMessage>
        {
            created,
            await manager.GetAsync(UsersPath + "?pageSize=100", Cancel),
            await manager.GetAsync($"{UsersPath}/{id}", Cancel),
            await manager.PutAsJsonAsync($"{UsersPath}/{id}/roles", new { roles = new[] { "Production" } }, Cancel),
            await manager.PostAsync($"{UsersPath}/{id}/deactivate", null, Cancel),
            await manager.PostAsync($"{UsersPath}/{id}/reactivate", null, Cancel),
        };

        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
            var text = await response.Content.ReadAsStringAsync(Cancel);
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(hash, text);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stamp", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A Manager reads one user by id: 200 with the design 5.2 user object.</summary>
    [Fact]
    public async Task Manager_reads_one_user()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Production, Roles.Manager);

        var response = await manager.GetAsync($"{UsersPath}/{user.Id}", Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(user.Id, body.GetProperty("id").GetString());
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(["Production", "Manager"], RoleNames(body));
        Assert.True(body.GetProperty("isActive").GetBoolean());
    }

    /// <summary>
    /// Design 7.2: an unknown user id is 404 "User not found." on every endpoint that names a user. Design 2.2: the
    /// 404 comes before input checks, so the bodies here are deliberately invalid.
    /// </summary>
    [Theory]
    [InlineData("GET", "/{id}", null)]
    [InlineData("PUT", "/{id}/roles", """{ "roles": [] }""")]
    [InlineData("POST", "/{id}/deactivate", null)]
    [InlineData("POST", "/{id}/reactivate", null)]
    [InlineData("PUT", "/{id}/password", """{ "newPassword": "weak" }""")]
    public async Task Unknown_user_id_gives_404(string method, string path, string? body)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);

        var response = await SendAsync(manager, method, UsersPath + path.Replace("{id}", "no-such-user"), body);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found", "User not found.");
    }

    /// <summary>AC-03.6 and design 4: users are never deleted; DELETE /users/{id} is 405 and the user remains.</summary>
    [Fact]
    public async Task Users_cannot_be_deleted()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);

        var response = await manager.DeleteAsync($"{UsersPath}/{user.Id}", Cancel);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.True(await UserExistsAsync(factory, user.Email!));
    }

    // ---- AC-03.5: role changes -------------------------------------------------------------------------------

    /// <summary>
    /// AC-03.5, PRD assumption 15, architecture risk 4: after Manager is removed from a user, the token they already hold
    /// keeps working with its old roles (it still opens a Manager-only endpoint), because a role change does not change
    /// the security stamp. Their next sign-in has the new roles, and the new token no longer opens it.
    /// </summary>
    [Fact]
    public async Task Removing_a_role_leaves_the_old_token_working_with_its_old_roles()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse, Roles.Manager);
        using var oldSession = factory.CreateClientWithToken(await factory.SignInAsync(email));
        var stampBefore = await StampAsync(factory, user.Id);

        var response = await manager.PutAsJsonAsync($"{UsersPath}/{user.Id}/roles", new { roles = new[] { "Warehouse" } }, Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Warehouse"], RoleNames(body));
        Assert.Equal([Roles.Warehouse], await RolesInDatabaseAsync(factory, user.Id));
        Assert.Equal(stampBefore, await StampAsync(factory, user.Id)); // risk 4: Identity left the stamp alone

        // The old token still works, and still carries Manager.
        Assert.Equal(HttpStatusCode.OK, (await oldSession.GetAsync(UsersPath, Cancel)).StatusCode);

        // The next sign-in has the new roles only.
        var (status, signIn) = await SignInRawAsync(email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Warehouse"], RoleNames(signIn));
        using var newSession = factory.CreateClientWithToken(signIn.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await newSession.GetAsync(UsersPath, Cancel)).StatusCode);
    }

    /// <summary>
    /// AC-03.5: after Manager is added to a user, their old token keeps working with its old roles (still 403 on a
    /// Manager-only endpoint, not 401); the next sign-in has the new roles.
    /// </summary>
    [Fact]
    public async Task Adding_a_role_applies_from_the_next_sign_in()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var oldSession = factory.CreateClientWithToken(await factory.SignInAsync(email));
        var stampBefore = await StampAsync(factory, user.Id);

        var response = await manager.PutAsJsonAsync($"{UsersPath}/{user.Id}/roles",
            new { roles = new[] { "manager", "Warehouse" } }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Warehouse", "Manager"], RoleNames(await ReadJsonAsync(response)));
        Assert.Equal(stampBefore, await StampAsync(factory, user.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await oldSession.GetAsync(UsersPath, Cancel)).StatusCode);

        using var newSession = factory.CreateClientWithToken(await factory.SignInAsync(email));
        Assert.Equal(HttpStatusCode.OK, (await newSession.GetAsync(UsersPath, Cancel)).StatusCode);
    }

    /// <summary>
    /// AC-03.5 ("at least one role") and AC-03.3: an empty or missing role list and an unknown role are 400 validation;
    /// the user's roles do not change.
    /// </summary>
    [Theory]
    [InlineData("""{ "roles": [] }""", "roles", "At least one role is required.")]
    [InlineData("""{ }""", "roles", "At least one role is required.")]
    [InlineData("""{ "roles": ["Boss"] }""", "roles[0]", "Unknown role 'Boss'. Roles are Warehouse, Production and Manager.")]
    public async Task Invalid_role_change_gives_400_and_changes_nothing(string json, string path, string message)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);

        var response = await SendAsync(manager, "PUT", $"{UsersPath}/{user.Id}/roles", json);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.True(body.GetProperty("errors").TryGetProperty(path, out _));
        Assert.Equal([Roles.Warehouse], await RolesInDatabaseAsync(factory, user.Id));
    }

    // ---- AC-03.6: deactivation and reactivation --------------------------------------------------------------

    /// <summary>
    /// AC-03.6: after deactivation the user's existing token gets 401 at once and sign-in is refused; after
    /// reactivation they can sign in again, while the token from before stays invalid (architecture 4.1 item 6).
    /// </summary>
    [Fact]
    public async Task Deactivation_ends_existing_tokens_and_reactivation_allows_sign_in_again()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var oldSession = factory.CreateClientWithToken(await factory.SignInAsync(email));
        // Before: the token works (a wrong current password reaches the service and gets 400, not 401).
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ChangePasswordAsync(oldSession, "Wrong-Current-1", NewPassword)).StatusCode);

        var deactivated = await manager.PostAsync($"{UsersPath}/{user.Id}/deactivate", null, Cancel);
        var body = await ReadJsonAsync(deactivated);

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(["Warehouse"], RoleNames(body));
        await AssertProblemAsync(await ChangePasswordAsync(oldSession, TestApiFactory.UserPassword, NewPassword),
            HttpStatusCode.Unauthorized, "not-authenticated", "Your sign-in is missing or has expired. Sign in again.");
        var (refusedStatus, refused) = await SignInRawAsync(email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, refusedStatus);
        Assert.Equal("invalid-credentials", refused.GetProperty("code").GetString());

        var reactivated = await manager.PostAsync($"{UsersPath}/{user.Id}/reactivate", null, Cancel);

        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.True((await ReadJsonAsync(reactivated)).GetProperty("isActive").GetBoolean());
        var (status, _) = await SignInRawAsync(email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ChangePasswordAsync(oldSession, TestApiFactory.UserPassword, NewPassword)).StatusCode);
    }

    /// <summary>
    /// Design 5.2: deactivating an inactive user, or reactivating an active one, succeeds with 200 and changes nothing
    /// (the security stamp stays the same).
    /// </summary>
    [Fact]
    public async Task Deactivate_and_reactivate_are_idempotent()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Production);

        var stampWhileActive = await StampAsync(factory, user.Id);
        var reactivateActive = await manager.PostAsync($"{UsersPath}/{user.Id}/reactivate", null, Cancel);
        Assert.Equal(HttpStatusCode.OK, reactivateActive.StatusCode);
        Assert.True((await ReadJsonAsync(reactivateActive)).GetProperty("isActive").GetBoolean());
        Assert.Equal(stampWhileActive, await StampAsync(factory, user.Id));

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"{UsersPath}/{user.Id}/deactivate", null, Cancel)).StatusCode);
        var stampAfterFirst = await StampAsync(factory, user.Id);
        Assert.NotEqual(stampWhileActive, stampAfterFirst);

        var again = await manager.PostAsync($"{UsersPath}/{user.Id}/deactivate", null, Cancel);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await ReadJsonAsync(again)).GetProperty("isActive").GetBoolean());
        Assert.Equal(stampAfterFirst, await StampAsync(factory, user.Id));
    }

    // ---- AC-03.7: a Manager sets a password ------------------------------------------------------------------

    /// <summary>
    /// AC-03.7: a Manager sets a new password for a user: 204; the old password no longer signs in, the new one does,
    /// and the user's existing token stops working (architecture 4.1 item 6, decision #26).
    /// </summary>
    [Fact]
    public async Task Manager_sets_a_new_password_for_a_user()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var oldSession = factory.CreateClientWithToken(await factory.SignInAsync(email));

        var response = await manager.PutAsJsonAsync($"{UsersPath}/{user.Id}/password", new { newPassword = NewPassword }, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, NewPassword)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ChangePasswordAsync(oldSession, NewPassword, "Another-Pass-8")).StatusCode);
    }

    /// <summary>AC-03.7: the policy applies; a weak or missing new password is 400 and the old password still works.</summary>
    [Theory]
    [InlineData("""{ "newPassword": "weakpass" }""", "weak-password", WeakPasswordMessage)]
    [InlineData("""{ }""", "validation", "New password is required.")]
    public async Task Manager_setting_an_invalid_password_gives_400(string json, string code, string message)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        var stampBefore = await StampAsync(factory, user.Id);

        var response = await SendAsync(manager, "PUT", $"{UsersPath}/{user.Id}/password", json);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, code, message);
        Assert.True(body.GetProperty("errors").TryGetProperty("newPassword", out _));
        Assert.Equal(stampBefore, await StampAsync(factory, user.Id));
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
    }

    // ---- AC-03.8: the last active Manager --------------------------------------------------------------------

    /// <summary>
    /// AC-03.8: when the first Manager is the only active Manager, removing their Manager role and deactivating them
    /// are both 409 last-manager with the PRD message, and nothing changes. This includes the Manager acting on
    /// themselves.
    /// </summary>
    [Fact]
    public async Task The_last_active_Manager_cannot_lose_the_role_or_be_deactivated()
    {
        await using var app = new TestApiFactory();
        using var admin = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var adminId = await UserIdAsync(app, TestApiFactory.AdminEmail);
        var stampBefore = await StampAsync(app, adminId);

        var removeRole = await admin.PutAsJsonAsync($"{UsersPath}/{adminId}/roles", new { roles = new[] { "Warehouse" } }, Cancel);
        var deactivate = await admin.PostAsync($"{UsersPath}/{adminId}/deactivate", null, Cancel);

        await AssertProblemAsync(removeRole, HttpStatusCode.Conflict, "last-manager", "At least one active Manager is required.");
        await AssertProblemAsync(deactivate, HttpStatusCode.Conflict, "last-manager", "At least one active Manager is required.");
        var stored = await LoadUserAsync(app, adminId);
        Assert.True(stored.IsActive);
        Assert.Equal(stampBefore, stored.SecurityStamp);
        Assert.Equal([Roles.Manager], await RolesInDatabaseAsync(app, adminId));
    }

    /// <summary>
    /// AC-03.8: an inactive Manager does not count. With a second Manager who is deactivated, the first Manager is
    /// still the last active one.
    /// </summary>
    [Fact]
    public async Task An_inactive_Manager_does_not_count_as_another_Manager()
    {
        await using var app = new TestApiFactory();
        using var admin = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var adminId = await UserIdAsync(app, TestApiFactory.AdminEmail);
        var second = await app.CreateUserAsync("second-manager@inventory.test", Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{UsersPath}/{second.Id}/deactivate", null, Cancel)).StatusCode);

        var removeRole = await admin.PutAsJsonAsync($"{UsersPath}/{adminId}/roles", new { roles = new[] { "Production" } }, Cancel);

        await AssertProblemAsync(removeRole, HttpStatusCode.Conflict, "last-manager", "At least one active Manager is required.");

        // Changing an inactive Manager's roles is allowed: it does not reduce the number of active Managers.
        var removeFromInactive = await admin.PutAsJsonAsync($"{UsersPath}/{second.Id}/roles", new { roles = new[] { "Warehouse" } }, Cancel);
        Assert.Equal(HttpStatusCode.OK, removeFromInactive.StatusCode);
    }

    /// <summary>
    /// AC-03.8 and design 9: while another active Manager exists, a Manager may deactivate themselves. A role change
    /// that keeps Manager is never refused. (Removing one's own Manager role: the next test.)
    /// </summary>
    [Fact]
    public async Task With_another_active_Manager_the_rule_does_not_block()
    {
        await using var app = new TestApiFactory();
        var adminToken = await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword);
        using var admin = app.CreateClientWithToken(adminToken);
        var adminId = await UserIdAsync(app, TestApiFactory.AdminEmail);
        await app.CreateUserAsync("other-manager@inventory.test", Roles.Manager);

        var keepManager = await admin.PutAsJsonAsync($"{UsersPath}/{adminId}/roles",
            new { roles = new[] { "Manager", "Warehouse" } }, Cancel);
        Assert.Equal(HttpStatusCode.OK, keepManager.StatusCode);

        var deactivateSelf = await admin.PostAsync($"{UsersPath}/{adminId}/deactivate", null, Cancel);
        Assert.Equal(HttpStatusCode.OK, deactivateSelf.StatusCode);
        Assert.False((await LoadUserAsync(app, adminId)).IsActive);

        // AC-03.6: deactivating themselves ends their own token too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(UsersPath, Cancel)).StatusCode);
    }

    /// <summary>
    /// AC-03.8 with AC-03.5 and PRD assumption 15: while another active Manager exists, a Manager may remove their own
    /// Manager role. Their current token keeps its old roles until the next sign-in, which has the new roles only. The
    /// other Manager is then the last active one, so removing their Manager role is refused with 409.
    /// </summary>
    [Fact]
    public async Task A_Manager_may_remove_their_own_Manager_role_while_another_remains()
    {
        await using var app = new TestApiFactory();
        using var admin = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var adminId = await UserIdAsync(app, TestApiFactory.AdminEmail);
        var other = await app.CreateUserAsync("remaining-manager@inventory.test", Roles.Manager);
        var stampBefore = await StampAsync(app, adminId);

        var removeOwn = await admin.PutAsJsonAsync($"{UsersPath}/{adminId}/roles", new { roles = new[] { "Warehouse" } }, Cancel);

        Assert.Equal(HttpStatusCode.OK, removeOwn.StatusCode);
        Assert.Equal(["Warehouse"], RoleNames(await ReadJsonAsync(removeOwn)));
        Assert.Equal([Roles.Warehouse], await RolesInDatabaseAsync(app, adminId));
        Assert.Equal(stampBefore, await StampAsync(app, adminId));

        // The old token still carries Manager, so it still opens a Manager-only endpoint (AC-03.5) ...
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(UsersPath, Cancel)).StatusCode);

        // ... and the rule now counts only the one remaining active Manager, whoever asks (AC-03.8).
        var removeLast = await admin.PutAsJsonAsync($"{UsersPath}/{other.Id}/roles", new { roles = new[] { "Production" } }, Cancel);
        await AssertProblemAsync(removeLast, HttpStatusCode.Conflict, "last-manager", "At least one active Manager is required.");
        Assert.Equal([Roles.Manager], await RolesInDatabaseAsync(app, other.Id));

        // The next sign-in has the new roles only, so the Manager-only endpoint is now refused.
        using var newSession = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await newSession.GetAsync(UsersPath, Cancel)).StatusCode);
    }

    /// <summary>
    /// AC-03.8 under concurrency (architecture 5.2): with exactly two active Managers, deactivating both at the same
    /// moment gives one 200 and one 409 last-manager; one active Manager remains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requests come from a third user whose Manager role was already removed in the database but whose token
    /// still carries it (AC-03.5). So the caller is not one of the two counted Managers, and neither request can be
    /// refused by the token check instead of by the rule under test.
    /// </para>
    /// <para>
    /// Two requests sent at once rarely overlap for long enough to show a race: the code review removed the transaction
    /// and this test still passed. So the window is opened on purpose. <see cref="PausingDeactivationValidator"/> holds
    /// the first deactivation after its last-Manager check and before its write. The second request is sent then.
    /// With the write transaction, it waits for SQLite's write lock and only checks after the first has committed, so
    /// it sees one Manager left and gets 409. Without it, it would check now, still see two active Managers, and both
    /// would succeed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Simultaneous_deactivations_cannot_remove_the_last_active_Manager()
    {
        var validator = new PausingDeactivationValidator();
        await using var app = new TestApiFactory
        {
            // Added next to Identity's own user validator, so UserManager runs both when it saves a user.
            ReplaceServices = services => services.AddSingleton<IUserValidator<AppUser>>(validator),
        };
        using var admin = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var adminId = await UserIdAsync(app, TestApiFactory.AdminEmail);
        var other = await app.CreateUserAsync("other-manager@inventory.test", Roles.Manager);
        var actor = await app.CreateUserAsync("actor@inventory.test", Roles.Manager);
        var actorToken = await app.SignInAsync(actor.Email!);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"{UsersPath}/{actor.Id}/roles", new { roles = new[] { "Warehouse" } }, Cancel)).StatusCode);
        Assert.Equal(2, await CountActiveManagersAsync(app));
        using var first = app.CreateClientWithToken(actorToken);
        using var second = app.CreateClientWithToken(actorToken);

        HttpResponseMessage[] responses;
        try
        {
            validator.Armed = true;
            var firstDeactivation = first.PostAsync($"{UsersPath}/{adminId}/deactivate", null, Cancel);
            await validator.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            var secondDeactivation = second.PostAsync($"{UsersPath}/{other.Id}/deactivate", null, Cancel);

            // Correct code: the second request is waiting for the write lock and never gets here, so this waits the
            // full second. Without the transaction it gets here within milliseconds.
            await Task.WhenAny(validator.SecondEntered.Task, Task.Delay(TimeSpan.FromSeconds(1), Cancel));
            validator.Release();
            responses = await Task.WhenAll(firstDeactivation, secondDeactivation);
        }
        finally
        {
            validator.Release();
        }

        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        await AssertProblemAsync(responses[1], HttpStatusCode.Conflict, "last-manager", "At least one active Manager is required.");
        Assert.Equal(1, await CountActiveManagersAsync(app));
        Assert.True((await LoadUserAsync(app, other.Id)).IsActive);
    }

    // ---- US-04: changing one's own password ------------------------------------------------------------------

    /// <summary>
    /// AC-04.1: with the right current password and a new one that meets the policy, the answer is 204; the old
    /// password no longer signs in and the new one does. Architecture 4.1 item 6: the token used for the change stops
    /// working, so the user signs in again.
    /// </summary>
    [Fact]
    public async Task Changing_own_password_replaces_it_and_ends_existing_tokens()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Production);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));

        var response = await ChangePasswordAsync(session, TestApiFactory.UserPassword, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, NewPassword)).Status);
        await AssertProblemAsync(await ChangePasswordAsync(session, NewPassword, "Another-Pass-8"),
            HttpStatusCode.Unauthorized, "not-authenticated", "Your sign-in is missing or has expired. Sign in again.");
    }

    /// <summary>
    /// AC-04.2: a wrong current password is 400 current-password-incorrect "Current password is incorrect."; the
    /// password does not change and the token keeps working.
    /// </summary>
    [Fact]
    public async Task Wrong_current_password_gives_400_and_changes_nothing()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));
        var stampBefore = await StampAsync(factory, user.Id);

        var response = await ChangePasswordAsync(session, "Not-My-Password-1", NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "current-password-incorrect", "Current password is incorrect.");
        Assert.Equal(stampBefore, await StampAsync(factory, user.Id));
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, NewPassword)).Status);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ChangePasswordAsync(session, TestApiFactory.UserPassword, NewPassword)).StatusCode); // token still works
    }

    /// <summary>
    /// AC-04.1 and design 7.1: a new password that fails the policy is 400 weak-password; missing fields are 400
    /// validation. The password does not change.
    /// </summary>
    [Theory]
    [InlineData($$"""{ "currentPassword": "{{TestApiFactory.UserPassword}}", "newPassword": "alllowercase1" }""", "weak-password", WeakPasswordMessage)]
    [InlineData($$"""{ "newPassword": "{{NewPassword}}" }""", "validation", "Current password is required.")]
    [InlineData($$"""{ "currentPassword": "{{TestApiFactory.UserPassword}}" }""", "validation", "New password is required.")]
    [InlineData("""{ }""", "validation", "Current password is required. New password is required.")]
    public async Task Invalid_change_password_input_gives_400(string json, string code, string message)
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));
        var stampBefore = await StampAsync(factory, user.Id);

        var response = await SendAsync(session, "POST", ChangePasswordPath, json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code, message);
        Assert.Equal(stampBefore, await StampAsync(factory, user.Id));
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
    }

    // ---- Decisions #71 and #72: change-password and the sign-in lock-out (AC-01.4) ---------------------------

    /// <summary>
    /// Decision #71 and AC-01.4: five wrong current passwords in a row lock the account. Each one is 400
    /// current-password-incorrect (the fifth too, as the fifth wrong sign-in still says "Invalid email or password.").
    /// Then sign-in is refused with account-locked even with the right password, and change-password gets the very same
    /// account-locked problem, even with the right current password.
    /// </summary>
    [Fact]
    public async Task Five_wrong_current_passwords_lock_the_account()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await AssertProblemAsync(await ChangePasswordAsync(session, $"Wrong-Guess-{attempt}", NewPassword),
                HttpStatusCode.BadRequest, "current-password-incorrect", "Current password is incorrect.");
        }

        Assert.NotNull((await LoadUserAsync(factory, user.Id)).LockoutEnd);
        using var anonymous = factory.CreateClient();
        var signIn = await anonymous.PostAsJsonAsync(SignInPath, new { email, password = TestApiFactory.UserPassword }, Cancel);
        var change = await ChangePasswordAsync(session, TestApiFactory.UserPassword, NewPassword);

        foreach (var response in new[] { signIn, change })
        {
            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "account-locked", AccountLockedMessage);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, NewPassword)).Status); // nothing changed
    }

    /// <summary>
    /// Decision #71 and AC-01.4: sign-in failures and wrong current passwords share one count. Three of one and two of
    /// the other, in any order, add up to five and lock the account.
    /// </summary>
    [Fact]
    public async Task Sign_in_failures_and_wrong_current_passwords_add_up()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Production);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));

        Assert.Equal(HttpStatusCode.BadRequest, (await ChangePasswordAsync(session, "Wrong-Guess-1", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, "Wrong-Guess-2")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangePasswordAsync(session, "Wrong-Guess-3", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, "Wrong-Guess-4")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, "Wrong-Guess-5")).Status);

        var (status, body) = await SignInRawAsync(email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("account-locked", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// Decisions #61 and #71 under concurrency (PRD section 5): ten wrong current passwords sent at the same moment are
    /// counted one at a time. Exactly five are counted (400 current-password-incorrect), the fifth locks the account,
    /// and the other five are refused with account-locked.
    /// </summary>
    [Fact]
    public async Task Simultaneous_wrong_current_passwords_count_exactly_five()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        var token = await factory.SignInAsync(email);
        var clients = Enumerable.Range(0, 10).Select(_ => factory.CreateClientWithToken(token)).ToList();

        var responses = await Task.WhenAll(clients.Select((client, index) =>
            ChangePasswordAsync(client, $"Wrong-Guess-{index}", NewPassword)));

        var codes = new List<string?>();
        foreach (var response in responses)
        {
            codes.Add((await ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(5, codes.Count(code => code == "current-password-incorrect"));
        Assert.Equal(5, codes.Count(code => code == "account-locked"));
        Assert.NotNull((await LoadUserAsync(factory, user.Id)).LockoutEnd);
        clients.ForEach(client => client.Dispose());
    }

    /// <summary>
    /// Decision #71 and AC-01.4 ("in a row"): a correct current password resets the failure count, like a successful
    /// sign-in, even when the new password is then refused as weak. Four more failures afterwards do not lock.
    /// </summary>
    [Fact]
    public async Task A_correct_current_password_resets_the_failure_count()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        using var session = factory.CreateClientWithToken(await factory.SignInAsync(email));
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ChangePasswordAsync(session, $"Wrong-Guess-{attempt}", NewPassword)).StatusCode);
        }

        Assert.Equal(4, (await LoadUserAsync(factory, user.Id)).AccessFailedCount);

        var weak = await ChangePasswordAsync(session, TestApiFactory.UserPassword, "weakpass");

        await AssertProblemAsync(weak, HttpStatusCode.BadRequest, "weak-password", WeakPasswordMessage);
        Assert.Equal(0, (await LoadUserAsync(factory, user.Id)).AccessFailedCount);
        for (var attempt = 5; attempt <= 8; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ChangePasswordAsync(session, $"Wrong-Guess-{attempt}", NewPassword)).StatusCode);
        }

        var stored = await LoadUserAsync(factory, user.Id);
        Assert.Equal(4, stored.AccessFailedCount);
        Assert.Null(stored.LockoutEnd);
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, TestApiFactory.UserPassword)).Status);
    }

    /// <summary>
    /// Decision #72 and AC-03.7: a Manager's reset ends a lock-out. A user locked out by five wrong sign-ins can sign in
    /// straight after the reset, with the new password; the reset clears the lock-out end and the failure count.
    /// </summary>
    [Fact]
    public async Task A_Manager_reset_clears_the_lock_out()
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(email, $"Wrong-Guess-{attempt}")).Status);
        }

        Assert.Equal("account-locked", (await SignInRawAsync(email, TestApiFactory.UserPassword)).Body.GetProperty("code").GetString());

        var reset = await manager.PutAsJsonAsync($"{UsersPath}/{user.Id}/password", new { newPassword = NewPassword }, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var stored = await LoadUserAsync(factory, user.Id);
        Assert.Null(stored.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(email, NewPassword)).Status);
    }

    // ---- Decision #70: slow password hashing stays outside the write lock ------------------------------------

    /// <summary>
    /// Decision #70 and PRD section 5 (writes from different users must not stall each other): while a slow password
    /// hash is in progress, another user's write still completes. Covered: a wrong current password (AC-04.2), a
    /// Manager's reset (AC-03.7) and a new user's initial password (AC-03.1). The hash is held open by
    /// <see cref="PausingHasher"/>; if it ran inside the write transaction, the other write would wait for the lock and
    /// the 10-second limit below would fail the test.
    /// </summary>
    [Theory]
    [InlineData("wrong-current-password", HttpStatusCode.BadRequest)]
    [InlineData("manager-reset", HttpStatusCode.NoContent)]
    [InlineData("create-user", HttpStatusCode.Created)]
    public async Task Slow_password_hashing_does_not_hold_the_write_lock(string operation, HttpStatusCode expected)
    {
        const string subjectEmail = "subject@inventory.test";
        var hasher = new PausingHasher();
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.AddSingleton<IPasswordHasher<AppUser>>(hasher),
        };
        using var manager = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var bystander = await app.CreateUserAsync("bystander@inventory.test", Roles.Production);
        string? subjectId = null;
        HttpClient? subjectSession = null;
        if (operation != "create-user")
        {
            subjectId = (await app.CreateUserAsync(subjectEmail, Roles.Warehouse)).Id;
            subjectSession = app.CreateClientWithToken(await app.SignInAsync(subjectEmail));
        }

        try
        {
            hasher.PauseNextCallFor(subjectEmail);
            var slow = operation switch
            {
                "wrong-current-password" => ChangePasswordAsync(subjectSession!, "Not-My-Password-1", NewPassword),
                "manager-reset" => manager.PutAsJsonAsync($"{UsersPath}/{subjectId}/password", new { newPassword = NewPassword }, Cancel),
                _ => manager.PostAsJsonAsync(UsersPath,
                    new { email = subjectEmail, password = "Initial2026", roles = new[] { "Warehouse" } }, Cancel),
            };
            await hasher.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            // Another user's write, while the hash is still paused.
            var other = await manager.PostAsync($"{UsersPath}/{bystander.Id}/deactivate", null, Cancel)
                .WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);

            hasher.Release();
            Assert.Equal(expected, (await slow).StatusCode);
        }
        finally
        {
            hasher.Release();
            subjectSession?.Dispose();
        }
    }

    /// <summary>
    /// Decision #70, AC-04.2 and architecture 4.1 item 6: the current password is checked before the transaction, so
    /// the transaction checks again that the caller's token is still valid: the user is active and has the token's
    /// security stamp. While the check runs, a Manager resets the password (AC-03.7), deactivates the user (AC-03.6), or
    /// deactivates and at once reactivates them. Each gives the user a new stamp, so the change is refused with 401
    /// not-authenticated and the password the user had afterwards stays in force.
    /// </summary>
    [Theory]
    [InlineData("manager-reset")]
    [InlineData("deactivate")]
    [InlineData("deactivate-and-reactivate")]
    public async Task Change_password_rechecks_the_token_inside_the_transaction(string interference)
    {
        const string subjectEmail = "subject@inventory.test";
        const string resetPassword = "Reset-2026a";
        var hasher = new PausingHasher();
        await using var app = new TestApiFactory
        {
            ReplaceServices = services => services.AddSingleton<IPasswordHasher<AppUser>>(hasher),
        };
        using var manager = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var subject = await app.CreateUserAsync(subjectEmail, Roles.Warehouse);
        using var subjectSession = app.CreateClientWithToken(await app.SignInAsync(subjectEmail));
        var userPath = $"{UsersPath}/{subject.Id}";

        HttpResponseMessage response;
        try
        {
            // The right current password: only the change made meanwhile can make this request fail.
            hasher.PauseNextCallFor(subjectEmail);
            var change = ChangePasswordAsync(subjectSession, TestApiFactory.UserPassword, NewPassword);
            await hasher.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            if (interference == "manager-reset")
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    (await manager.PutAsJsonAsync($"{userPath}/password", new { newPassword = resetPassword }, Cancel)).StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"{userPath}/deactivate", null, Cancel)).StatusCode);
                if (interference == "deactivate-and-reactivate")
                {
                    // Active again, but with the stamp the deactivation gave: the paused request's token is still stale.
                    Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"{userPath}/reactivate", null, Cancel)).StatusCode);
                }
            }

            hasher.Release();
            response = await change;
        }
        finally
        {
            hasher.Release();
        }

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated",
            "Your sign-in is missing or has expired. Sign in again.");
        if (interference == "deactivate")
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"{userPath}/reactivate", null, Cancel)).StatusCode);
        }

        var passwordInForce = interference == "manager-reset" ? resetPassword : TestApiFactory.UserPassword;
        Assert.Equal(HttpStatusCode.OK, (await SignInRawAsync(app, subjectEmail, passwordInForce)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInRawAsync(app, subjectEmail, NewPassword)).Status);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static string UniqueEmail() => $"users-{Guid.NewGuid():N}@inventory.test";

    private static async Task<string> ManagerTokenAsync(TestApiFactory app)
    {
        var email = UniqueEmail();
        await app.CreateUserAsync(email, Roles.Manager);
        return await app.SignInAsync(email);
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync(ChangePasswordPath, new { currentPassword, newPassword }, Cancel);

    /// <summary>Signs in and returns the status and body, without failing the test on a refusal.</summary>
    private Task<(HttpStatusCode Status, JsonElement Body)> SignInRawAsync(string email, string password) =>
        SignInRawAsync(factory, email, password);

    /// <summary>The same as the overload above, against a test's own app.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> SignInRawAsync(TestApiFactory app, string email, string password)
    {
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync(SignInPath, new { email, password }, Cancel);
        return (response.StatusCode, await ReadJsonAsync(response));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? json)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Cancel);
    }

    private static IEnumerable<string?> RoleNames(JsonElement body) =>
        body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToList();

    private static async Task<AppUser> LoadUserAsync(TestApiFactory app, string id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(user => user.Id == id, Cancel);
    }

    private static async Task<string> StampAsync(TestApiFactory app, string id) => (await LoadUserAsync(app, id)).SecurityStamp!;

    private static async Task<string> UserIdAsync(TestApiFactory app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(Cancel);
    }

    private static async Task<bool> UserExistsAsync(TestApiFactory app, string email) =>
        await CountUsersAsync(app, email.Trim().ToUpperInvariant()) > 0;

    /// <summary>Counts users, or only those whose NormalizedUserName equals <paramref name="normalizedUserName"/>.</summary>
    private static async Task<int> CountUsersAsync(TestApiFactory app, string? normalizedUserName = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.CountAsync(
            user => normalizedUserName == null || user.NormalizedUserName == normalizedUserName, Cancel);
    }

    private static async Task<List<string>> RolesInDatabaseAsync(TestApiFactory app, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await (from userRole in db.UserRoles
                      join role in db.Roles on userRole.RoleId equals role.Id
                      where userRole.UserId == userId
                      select role.Name!).ToListAsync(Cancel);
    }

    private static async Task<int> CountActiveManagersAsync(TestApiFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await (from userRole in db.UserRoles
                      join role in db.Roles on userRole.RoleId equals role.Id
                      join user in db.Users on userRole.UserId equals user.Id
                      where role.Name == Roles.Manager && user.IsActive
                      select user.Id).CountAsync(Cancel);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Checks the design 6 error body: status, title, code, detail, traceId and content type.</summary>
    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string detail)
    {
        var body = await ReadJsonAsync(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        var title = status switch
        {
            HttpStatusCode.BadRequest => "Invalid request",
            HttpStatusCode.Unauthorized => "Not signed in",
            HttpStatusCode.Forbidden => "Not allowed",
            HttpStatusCode.NotFound => "Not found",
            _ => "Conflict with current state",
        };
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        return body;
    }

    /// <summary>
    /// Identity's real password hasher, except that it can hold the next hash or hash check for one email open until
    /// the test lets it go. Lets a test prove that the slow hashing is not done while the write lock is held
    /// (decision #70). Registered in place of the app's hasher through <see cref="TestApiFactory.ReplaceServices"/>.
    /// </summary>
    private sealed class PausingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _real = new();
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private string? _pauseFor;

        /// <summary>Completes when the paused call has started and is waiting.</summary>
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next call (and only that one) for a user with this email waits until <see cref="Release"/>.</summary>
        public void PauseNextCallFor(string email) => Volatile.Write(ref _pauseFor, email);

        /// <summary>Lets the paused call finish. Safe to call more than once.</summary>
        public void Release() => _release.Set();

        public string HashPassword(AppUser user, string password)
        {
            PauseIfArmed(user);
            return _real.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            PauseIfArmed(user);
            return _real.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }

        private void PauseIfArmed(AppUser user)
        {
            var armed = Volatile.Read(ref _pauseFor);
            if (armed is not null && string.Equals(user.Email, armed, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.CompareExchange(ref _pauseFor, null, armed) == armed)
            {
                Paused.TrySetResult();

                // A synchronous wait, like the real hash's CPU work; the timeout keeps a broken test from hanging.
                _release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>
    /// A user validator that accepts everything, but once <see cref="Armed"/> holds every save of a deactivated user
    /// until <see cref="Release"/>. Identity runs user validators inside <c>UpdateSecurityStampAsync</c>, which
    /// deactivation calls after its last-Manager check and before the write, so this opens the race window on purpose.
    /// </summary>
    private sealed class PausingDeactivationValidator : IUserValidator<AppUser>
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        /// <summary>Off while the test sets up its users, so only the deactivations under test are held.</summary>
        public volatile bool Armed;

        /// <summary>Completes when the first held deactivation has reached the validator.</summary>
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a second deactivation reaches the validator while the first is still held.</summary>
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets every held deactivation continue. Safe to call more than once.</summary>
        public void Release() => _release.TrySetResult();

        public async Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user)
        {
            if (Armed && !user.IsActive)
            {
                var entered = Interlocked.Increment(ref _entered);
                (entered == 1 ? FirstEntered : SecondEntered).TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }

            return IdentityResult.Success;
        }
    }
}
