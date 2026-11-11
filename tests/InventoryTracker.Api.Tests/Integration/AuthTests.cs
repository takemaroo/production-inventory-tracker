using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-003: sign-in, tokens, the per-request user check, the fallback "signed-in" policy and role checks
/// (US-01, US-05, architecture 4.1 items 5 to 7). Access rules are exercised through the test-only
/// <see cref="AuthProbeController"/>.
/// </summary>
/// <remarks>
/// Tests that move the clock create their own factory with a <see cref="FakeTimeProvider"/>, so they do not disturb
/// the shared one. Every test creates its own users with unique emails.
/// </remarks>
public class AuthTests(ProbeApiFactory factory) : IClassFixture<ProbeApiFactory>
{
    private const string SignInPath = "/api/v1/auth/sign-in";
    private const string NotAuthenticatedMessage = "Your sign-in is missing or has expired. Sign in again.";
    private const string InvalidCredentialsMessage = "Invalid email or password.";
    private const string LockedMessage = "Account temporarily locked. Try again later.";

    /// <summary>A fixed, readable start time for the fake clock.</summary>
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-01.1: successful sign-in ------------------------------------------------------------------------

    /// <summary>
    /// AC-01.1: sign-in returns 200 with accessToken, tokenType "Bearer", expiresAt = now + 8 h in UTC, the stored email
    /// and the roles (in the order Warehouse, Production, Manager). Design 5.1: the email's letter case does not matter.
    /// </summary>
    [Fact]
    public async Task Sign_in_returns_token_expiry_email_and_roles()
    {
        await using var timed = new ProbeApiFactory { FakeTime = new FakeTimeProvider(Start.AddMilliseconds(700)) };
        var email = UniqueEmail();
        await timed.CreateUserAsync(email, Roles.Production, Roles.Warehouse);

        var (response, body) = await SignInAsync(timed, email.ToUpperInvariant(), TestApiFactory.UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal("2026-11-11T16:00:00Z", body.GetProperty("expiresAt").GetString()); // whole seconds, UTC
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(["Warehouse", "Production"], body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(5, body.EnumerateObject().Count());
    }

    /// <summary>AC-01.1: the token works: a signed-in endpoint sees the user's email and roles from it.</summary>
    [Fact]
    public async Task Token_from_sign_in_is_accepted()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Manager);
        var client = factory.CreateClientWithToken(await factory.SignInAsync(email));

        var response = await client.GetAsync("/test-probe/auth/signed-in", Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(["Manager"], body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    /// <summary>
    /// Architecture 6, Tokens: HS256, with the claims sub, email, one role claim per role, stamp and jti, and a lifetime
    /// of exactly 8 hours. The readable payload holds no password or password hash.
    /// </summary>
    [Fact]
    public async Task Token_is_hs256_with_the_agreed_claims()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse, Roles.Manager);

        var token = new JsonWebToken(await factory.SignInAsync(email));

        Assert.Equal("HS256", token.Alg);
        Assert.Equal(user.Id, token.Subject);
        Assert.Equal(email, token.GetClaim("email").Value);
        Assert.Equal(["Warehouse", "Manager"], token.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value));
        Assert.Equal(user.SecurityStamp, token.GetClaim("stamp").Value);
        Assert.False(string.IsNullOrEmpty(token.Id)); // jti
        Assert.Equal(TimeSpan.FromHours(8), token.ValidTo - token.IssuedAt);
        Assert.Equal("InventoryTracker", token.Issuer);
        Assert.Equal(["InventoryTracker"], token.Audiences);

        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        Assert.DoesNotContain(TestApiFactory.UserPassword, payload);
        Assert.DoesNotContain("PasswordHash", payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PRD section 5: passwords are stored only as salted hashes; the same password gives different hashes.</summary>
    [Fact]
    public async Task Passwords_are_stored_only_as_salted_hashes()
    {
        var first = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);
        var second = await factory.CreateUserAsync(UniqueEmail(), Roles.Warehouse);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hashes = await db.Users.AsNoTracking()
            .Where(user => user.Id == first.Id || user.Id == second.Id)
            .Select(user => user.PasswordHash!)
            .ToListAsync(Cancel);

        Assert.Equal(2, hashes.Count);
        Assert.All(hashes, hash => Assert.DoesNotContain(TestApiFactory.UserPassword, hash));
        Assert.NotEqual(hashes[0], hashes[1]); // different salts
    }

    // ---- AC-01.2: expiry ------------------------------------------------------------------------------------

    /// <summary>
    /// AC-01.2 and architecture 4.1 item 6: a token works until its 8 hours are over, and from the 8-hour mark on gets
    /// 401 not-authenticated. There is no grace period (ClockSkew zero). The fake clock stands in for waiting.
    /// </summary>
    [Fact]
    public async Task Token_expires_after_exactly_8_hours()
    {
        var time = new FakeTimeProvider(Start);
        await using var timed = new ProbeApiFactory { FakeTime = time };
        var email = UniqueEmail();
        await timed.CreateUserAsync(email, Roles.Warehouse);
        var client = timed.CreateClientWithToken(await timed.SignInAsync(email));

        time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));
        var lastSecond = await client.GetAsync("/test-probe/auth/signed-in", Cancel);

        time.Advance(TimeSpan.FromSeconds(1));
        var expired = await client.GetAsync("/test-probe/auth/signed-in", Cancel);

        Assert.Equal(HttpStatusCode.OK, lastSecond.StatusCode);
        await AssertNotAuthenticatedAsync(expired);
    }

    // ---- AC-01.3: refused sign-in ---------------------------------------------------------------------------

    /// <summary>
    /// AC-01.3: a wrong password, an unknown email and a deactivated user all give 401 invalid-credentials with the same
    /// message "Invalid email or password.", the design 6 body and WWW-Authenticate: Bearer.
    /// </summary>
    [Theory]
    [InlineData("wrong-password")]
    [InlineData("unknown-email")]
    [InlineData("inactive-user")]
    public async Task Refused_sign_in_gives_invalid_credentials(string scenario)
    {
        var email = UniqueEmail();
        var password = TestApiFactory.UserPassword;
        switch (scenario)
        {
            case "wrong-password":
                await factory.CreateUserAsync(email, Roles.Warehouse);
                password = "Wrong-Password-1";
                break;
            case "inactive-user":
                var user = await factory.CreateUserAsync(email, Roles.Warehouse);
                await SetActiveAsync(factory, user.Id, false);
                break;
        }

        var (response, body) = await SignInAsync(factory, email, password);

        AssertProblem(response, body, HttpStatusCode.Unauthorized, "Not signed in", "invalid-credentials", InvalidCredentialsMessage);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>
    /// Architecture 4.1 item 5: attempts on a deactivated account count no failures, so they cannot lock it; after
    /// reactivation the right password works at once.
    /// </summary>
    [Fact]
    public async Task Attempts_on_an_inactive_account_count_no_failures()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        await SetActiveAsync(factory, user.Id, false);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await SignInAsync(factory, email, "Wrong-Password-1");
        }

        await SetActiveAsync(factory, user.Id, true);
        var (response, _) = await SignInAsync(factory, email, TestApiFactory.UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await AccessFailedCountAsync(factory, user.Id));
    }

    /// <summary>Design 2.2 step 4: a sign-in body without email or password is 400 validation naming both fields.</summary>
    [Fact]
    public async Task Sign_in_without_email_or_password_gives_400()
    {
        var (response, body) = await PostSignInAsync(factory, """{ "email": "   " }""");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "Email is required. Password is required.");
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    /// <summary>Architecture 4.1 item 2: sign-in with no body at all is 400 "A request body is required.".</summary>
    [Fact]
    public async Task Sign_in_without_a_body_gives_400()
    {
        var (response, body) = await PostSignInAsync(factory, "");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation", "A request body is required.");
    }

    /// <summary>AC-01.5: sign-in is the endpoint that needs no token; a stale or broken Authorization header does not block it.</summary>
    [Fact]
    public async Task Sign_in_works_without_a_token_even_with_a_bad_header()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Warehouse);
        var client = factory.CreateClientWithToken("not-a-token");

        var response = await client.PostAsJsonAsync(SignInPath, new { email, password = TestApiFactory.UserPassword }, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- AC-01.4: lock-out ----------------------------------------------------------------------------------

    /// <summary>
    /// AC-01.4 and architecture 4.1 item 5: the fifth wrong password in a row still says "Invalid email or password."
    /// and locks the account. While locked, the right password (and a wrong one, which does not extend the lock) gives
    /// 401 account-locked. Exactly 15 minutes after the lock began, the right password works again.
    /// </summary>
    [Fact]
    public async Task Five_failures_lock_the_account_for_15_minutes()
    {
        var time = new FakeTimeProvider(Start);
        await using var timed = new ProbeApiFactory { FakeTime = time };
        var email = UniqueEmail();
        await timed.CreateUserAsync(email, Roles.Warehouse);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (failed, failedBody) = await SignInAsync(timed, email, "Wrong-Password-1");
            AssertProblem(failed, failedBody, HttpStatusCode.Unauthorized, "Not signed in", "invalid-credentials", InvalidCredentialsMessage);
        }

        var (locked, lockedBody) = await SignInAsync(timed, email, TestApiFactory.UserPassword);
        AssertProblem(locked, lockedBody, HttpStatusCode.Unauthorized, "Not signed in", "account-locked", LockedMessage);
        Assert.Equal("Bearer", locked.Headers.WwwAuthenticate.ToString());

        time.Advance(TimeSpan.FromMinutes(10));
        var (wrongWhileLocked, wrongWhileLockedBody) = await SignInAsync(timed, email, "Wrong-Password-1");
        AssertProblem(wrongWhileLocked, wrongWhileLockedBody, HttpStatusCode.Unauthorized, "Not signed in", "account-locked", LockedMessage);

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var (stillLocked, stillLockedBody) = await SignInAsync(timed, email, TestApiFactory.UserPassword);
        AssertProblem(stillLocked, stillLockedBody, HttpStatusCode.Unauthorized, "Not signed in", "account-locked", LockedMessage);

        time.Advance(TimeSpan.FromSeconds(1));
        var (unlocked, _) = await SignInAsync(timed, email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
    }

    /// <summary>
    /// AC-01.4 under concurrent use (PRD section 5, decision #61): 10 wrong passwords sent at the same moment are handled
    /// one at a time for the account, so exactly 5 count as failures (invalid-credentials) and lock it, the other 5 find
    /// it locked (account-locked), and the right password afterwards is refused too.
    /// </summary>
    [Fact]
    public async Task Simultaneous_wrong_passwords_still_lock_the_account()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Warehouse);

        // Each attempt uses its own HttpClient, as separate callers would.
        var attempts = Enumerable.Range(0, 10)
            .Select(_ => SignInAsync(factory, email, "Wrong-Password-1"));
        var results = await Task.WhenAll(attempts);

        var codes = results.Select(result => result.Body.GetProperty("code").GetString()).ToList();
        Assert.All(results, result => Assert.Equal(HttpStatusCode.Unauthorized, result.Response.StatusCode));
        Assert.Equal(5, codes.Count(code => code == "invalid-credentials"));
        Assert.Equal(5, codes.Count(code => code == "account-locked"));

        var (rightPassword, rightPasswordBody) = await SignInAsync(factory, email, TestApiFactory.UserPassword);
        AssertProblem(rightPassword, rightPasswordBody, HttpStatusCode.Unauthorized, "Not signed in", "account-locked", LockedMessage);
    }

    /// <summary>
    /// Decision #61: the per-account locks do not pile up. After simultaneous attempts on unknown emails (as an attacker
    /// might send) and on a real account, no lock entry is left in memory.
    /// </summary>
    [Fact]
    public async Task Account_locks_are_removed_after_use()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Warehouse);

        var attempts = Enumerable.Range(0, 10).Select(_ => SignInAsync(factory, email, "Wrong-Password-1"))
            .Concat(Enumerable.Range(0, 10).Select(_ => SignInAsync(factory, UniqueEmail(), "Wrong-Password-1")));
        await Task.WhenAll(attempts);

        Assert.Equal(0, factory.Services.GetRequiredService<AccountLocks>().Count);
    }

    /// <summary>
    /// Decision #61: the lock is per account, never global. While one account's lock is held, another account's lock is
    /// granted at once, but a second attempt on the same account waits until the first lets go.
    /// </summary>
    [Fact]
    public async Task Account_lock_holds_back_the_same_account_only()
    {
        var locks = new AccountLocks();
        var first = await locks.AcquireAsync("ANA@EXAMPLE.COM");

        var otherAccount = locks.AcquireAsync("LUIS@EXAMPLE.COM");
        var sameAccount = locks.AcquireAsync("ANA@EXAMPLE.COM");

        Assert.True(otherAccount.IsCompletedSuccessfully);
        Assert.False(sameAccount.IsCompleted);

        first.Dispose();
        (await sameAccount.WaitAsync(TimeSpan.FromSeconds(5), Cancel)).Dispose();
        (await otherAccount).Dispose();
        Assert.Equal(0, locks.Count);
    }

    /// <summary>
    /// Decision #62: the dummy password check runs after the account's lock is released. While one refused attempt is
    /// stuck in its dummy check, a second attempt on the same account (locked, or unknown email) still gets its answer.
    /// If the check ran inside the lock, the second attempt would wait for the first and this test would time out.
    /// </summary>
    [Theory]
    [InlineData("locked-account")]
    [InlineData("unknown-email")]
    public async Task Dummy_password_check_does_not_hold_the_account_lock(string scenario)
    {
        var hasher = new PausingPasswordHasher();
        await using var app = new ProbeApiFactory
        {
            ReplaceServices = services => services.AddSingleton<IPasswordHasher<AppUser>>(hasher),
        };
        var email = UniqueEmail();
        var expectedCode = "invalid-credentials";
        if (scenario == "locked-account")
        {
            await app.CreateUserAsync(email, Roles.Warehouse);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await SignInAsync(app, email, "Wrong-Password-1");
            }

            expectedCode = "account-locked";
        }

        hasher.PauseNextDummyCheck();
        var first = SignInAsync(app, email, "Wrong-Password-1");
        await hasher.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        // The first attempt is now inside its dummy check. The second must not wait for it.
        var (secondResponse, secondBody) = await SignInAsync(app, email, "Wrong-Password-1")
            .WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        Assert.False(first.IsCompleted);

        hasher.Release();
        var (firstResponse, firstBody) = await first.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
        Assert.Equal(expectedCode, secondBody.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);
        Assert.Equal(expectedCode, firstBody.GetProperty("code").GetString());
    }

    /// <summary>AC-01.4 "in a row": a successful sign-in resets the count, so 4 failures, a success and 4 more do not lock.</summary>
    [Fact]
    public async Task A_successful_sign_in_resets_the_failure_count()
    {
        var email = UniqueEmail();
        await factory.CreateUserAsync(email, Roles.Warehouse);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await SignInAsync(factory, email, "Wrong-Password-1");
        }

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(factory, email, TestApiFactory.UserPassword)).Response.StatusCode);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await SignInAsync(factory, email, "Wrong-Password-1");
        }

        var (response, _) = await SignInAsync(factory, email, TestApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- AC-01.5: missing or bad tokens ---------------------------------------------------------------------

    /// <summary>
    /// AC-01.5 and architecture 4.1 item 7: a signed-in endpoint called without a token, with a garbage token, or with a
    /// token that is unsigned, signed with another key, altered after signing, issued for another issuer or audience,
    /// signed with the real key but by HS384 or HS512 instead of HS256 (the algorithm is pinned, architecture 6), or
    /// with no expiry time at all, gives 401 not-authenticated with the ProblemDetails body and WWW-Authenticate: Bearer.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("three-garbage-parts")]
    [InlineData("unsigned")]
    [InlineData("wrong-key")]
    [InlineData("tampered-roles")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("hs384")]
    [InlineData("hs512")]
    [InlineData("no-expiry")]
    public async Task Missing_or_bad_token_gives_401(string kind)
    {
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Production);
        var token = kind switch
        {
            "none" => null,
            "garbage" => "not-a-token",
            "three-garbage-parts" => "abc.def.ghi",
            "unsigned" => ForgeToken(user, key: null),
            "wrong-key" => ForgeToken(user, key: "another-key-that-is-long-enough-0123456789"),
            "tampered-roles" => AddManagerRole(await factory.SignInAsync(user.Email!)),
            "wrong-issuer" => ForgeToken(user, issuer: "someone-else"),
            "wrong-audience" => ForgeToken(user, audience: "another-api"),
            "hs384" => ForgeToken(user, algorithm: SecurityAlgorithms.HmacSha384),
            "hs512" => ForgeToken(user, algorithm: SecurityAlgorithms.HmacSha512),
            "no-expiry" => ForgeToken(user, withExpiry: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var client = token is null ? factory.CreateClient() : factory.CreateClientWithToken(token);

        var response = await client.GetAsync("/test-probe/auth/signed-in", Cancel);

        await AssertNotAuthenticatedAsync(response);
    }

    /// <summary>The forged-token helper itself is right: signed with the real key and settings, the same token is accepted.</summary>
    [Fact]
    public async Task Forged_token_with_the_real_key_is_accepted()
    {
        var user = await factory.CreateUserAsync(UniqueEmail(), Roles.Production);
        var client = factory.CreateClientWithToken(ForgeToken(user));

        var response = await client.GetAsync("/test-probe/auth/signed-in", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// AC-01.5: the fallback policy protects every endpoint. The only endpoints that allow anonymous callers are sign-in
    /// and /health (decisions #28, #33; OpenAPI is mapped in Development only). Guards later tasks too: an
    /// [AllowAnonymous] added anywhere else makes this test fail.
    /// </summary>
    [Fact]
    public async Task Only_sign_in_and_health_allow_anonymous_callers()
    {
        await using var app = new TestApiFactory();

        var anonymous = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/'))
            .Order()
            .ToArray();

        Assert.Equal(["api/v1/auth/sign-in", "health"], anonymous);
    }

    // ---- Architecture 4.1 item 6: the per-request user check -------------------------------------------------

    /// <summary>
    /// Architecture 4.1 item 6 (the mechanism behind AC-03.6): deactivating a user directly in the database makes their
    /// existing token get 401 on the very next request.
    /// </summary>
    [Fact]
    public async Task Token_of_a_deactivated_user_gives_401()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        var client = factory.CreateClientWithToken(await factory.SignInAsync(email));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-probe/auth/signed-in", Cancel)).StatusCode);

        await SetActiveAsync(factory, user.Id, false);

        await AssertNotAuthenticatedAsync(await client.GetAsync("/test-probe/auth/signed-in", Cancel));
    }

    /// <summary>
    /// Architecture 4.1 item 6: changing the user's security stamp (as a password change does) makes existing tokens get
    /// 401; signing in again gives a token that works.
    /// </summary>
    [Fact]
    public async Task Token_with_an_old_security_stamp_gives_401()
    {
        var email = UniqueEmail();
        var user = await factory.CreateUserAsync(email, Roles.Warehouse);
        var oldClient = factory.CreateClientWithToken(await factory.SignInAsync(email));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.Id))!);
        }

        var newClient = factory.CreateClientWithToken(await factory.SignInAsync(email));

        await AssertNotAuthenticatedAsync(await oldClient.GetAsync("/test-probe/auth/signed-in", Cancel));
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/test-probe/auth/signed-in", Cancel)).StatusCode);
    }

    // ---- AC-05.1, AC-05.2: roles ----------------------------------------------------------------------------

    /// <summary>
    /// AC-05.1 and design 7.1: a signed-in user holding none of the allowed roles gets 403 forbidden, title "Not allowed",
    /// and a message naming the roles: "the Warehouse or Manager role" for two, "the Manager role" for one.
    /// </summary>
    [Theory]
    [InlineData("/test-probe/auth/warehouse-or-manager", new[] { Roles.Production },
        "Your roles do not allow this action. It needs the Warehouse or Manager role.")]
    [InlineData("/test-probe/auth/production-or-manager", new[] { Roles.Warehouse },
        "Your roles do not allow this action. It needs the Production or Manager role.")]
    [InlineData("/test-probe/auth/manager", new[] { Roles.Warehouse, Roles.Production },
        "Your roles do not allow this action. It needs the Manager role.")]
    public async Task Role_the_user_lacks_gives_403(string path, string[] roles, string message)
    {
        var client = await factory.CreateClientAsAsync(roles);

        var response = await client.GetAsync(path, Cancel);
        var body = await ReadJsonAsync(response);

        AssertProblem(response, body, HttpStatusCode.Forbidden, "Not allowed", "forbidden", message);
    }

    /// <summary>
    /// AC-05.2: a user may do everything any of their roles allows. The PRD's example: Warehouse plus Production can both
    /// send (Warehouse or Manager) and complete (Production or Manager). A Manager passes every role check.
    /// </summary>
    [Theory]
    [InlineData("/test-probe/auth/warehouse-or-manager", new[] { Roles.Warehouse, Roles.Production })]
    [InlineData("/test-probe/auth/production-or-manager", new[] { Roles.Warehouse, Roles.Production })]
    [InlineData("/test-probe/auth/warehouse-or-manager", new[] { Roles.Warehouse })]
    [InlineData("/test-probe/auth/warehouse-or-manager", new[] { Roles.Manager })]
    [InlineData("/test-probe/auth/production-or-manager", new[] { Roles.Manager })]
    [InlineData("/test-probe/auth/manager", new[] { Roles.Manager })]
    [InlineData("/test-probe/auth/signed-in", new[] { Roles.Production })]
    public async Task Any_allowed_role_gives_200(string path, string[] roles)
    {
        var client = await factory.CreateClientAsAsync(roles);

        var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Design 2.2: the token is checked before the role, so a role-restricted endpoint without a token gives 401, not 403.</summary>
    [Fact]
    public async Task Role_endpoint_without_a_token_gives_401_not_403()
    {
        var response = await factory.CreateClient().GetAsync("/test-probe/auth/manager", Cancel);

        await AssertNotAuthenticatedAsync(response);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static string UniqueEmail() => $"auth-{Guid.NewGuid():N}@inventory.test";

    private static Task<(HttpResponseMessage Response, JsonElement Body)> SignInAsync(
        TestApiFactory app, string email, string password) =>
        PostSignInAsync(app, JsonSerializer.Serialize(new { email, password }));

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostSignInAsync(TestApiFactory app, string json)
    {
        var client = app.CreateClient();
        var response = await client.PostAsync(SignInPath, new StringContent(json, Encoding.UTF8, "application/json"), Cancel);
        return (response, await ReadJsonAsync(response));
    }

    private static async Task SetActiveAsync(TestApiFactory app, string userId, bool active)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, active), Cancel);
    }

    private static async Task<int> AccessFailedCountAsync(TestApiFactory app, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(user => user.Id == userId).Select(user => user.AccessFailedCount).SingleAsync(Cancel);
    }

    /// <summary>
    /// Builds a token by hand with the user's real id and stamp, so the only thing wrong with it is what the test
    /// changes: the key (null for an unsigned token), the issuer, the audience, the signing algorithm, or the absence
    /// of an expiry time.
    /// </summary>
    private static string ForgeToken(AppUser user, string? key = TestApiFactory.SigningKey,
        string issuer = "InventoryTracker", string audience = "InventoryTracker",
        string algorithm = SecurityAlgorithms.HmacSha256, bool withExpiry = true)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id,
                ["email"] = user.Email!,
                ["stamp"] = user.SecurityStamp!,
                ["role"] = new[] { Roles.Manager },
            },
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = withExpiry ? now.AddHours(1) : null,
            SigningCredentials = key is null
                ? null
                : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm),
        };

        // By default the handler adds an expiry of one hour to a token that has none; switched off for "no-expiry".
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = withExpiry };
        var token = handler.CreateToken(descriptor);
        Assert.Equal(withExpiry, new JsonWebToken(token).TryGetPayloadValue<long>("exp", out _));
        return token;
    }

    /// <summary>Rewrites a real token's payload to add the Manager role, keeping the original signature.</summary>
    private static string AddManagerRole(string token)
    {
        var parts = token.Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["role"] = new JsonArray(Roles.Production, Roles.Manager);
        parts[1] = Base64UrlEncoder.Encode(payload.ToJsonString());
        return string.Join('.', parts);
    }

    private static async Task AssertNotAuthenticatedAsync(HttpResponseMessage response)
    {
        var body = await ReadJsonAsync(response);
        AssertProblem(response, body, HttpStatusCode.Unauthorized, "Not signed in", "not-authenticated", NotAuthenticatedMessage);
        Assert.Equal(new AuthenticationHeaderValue("Bearer").ToString(), response.Headers.WwwAuthenticate.ToString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Checks the fields every design 6 error body has.</summary>
    private static void AssertProblem(HttpResponseMessage response, JsonElement body,
        HttpStatusCode status, string title, string code, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }
}
