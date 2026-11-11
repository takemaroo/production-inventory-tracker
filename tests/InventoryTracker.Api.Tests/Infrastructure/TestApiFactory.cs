using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Starts the real API in memory for integration tests, with its own empty SQLite database file, test-only secrets,
/// and helpers to create users and get tokens.
/// </summary>
/// <remarks>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> runs Program.cs exactly as production does, but serves
/// requests through an in-memory test server instead of a network port. <c>CreateClient()</c> returns an
/// <see cref="HttpClient"/> wired to that server.
///
/// Each factory gets a fresh database file in the temp folder. A real file is used instead of SQLite's
/// in-memory mode because the concurrency tests (PRD section 5) need several connections and real
/// file locking, just like production. Tests in one class share a factory through xUnit's
/// <c>IClassFixture&lt;TestApiFactory&gt;</c>; a test that needs a clean database creates its own factory.
///
/// The "Testing" environment means user-secrets are not loaded, so a developer's local secrets never leak into test
/// runs. The secrets the app needs (<c>JWT_SIGNING_KEY</c>, <c>ADMIN_EMAIL</c>, <c>ADMIN_PASSWORD</c>) are set here in
/// code, with values that exist only for tests (AC-02.5: no real secret in a tracked file). They reach the app as an
/// in-memory configuration source added after all the others (appsettings, environment variables, command line).
/// Configuration answers from the last source that knows a key, so these values win, and the owner's own environment
/// variables cannot change a test run (<c>TestSettingsIsolationTests</c> proves it).
/// </remarks>
public class TestApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Test-only token signing key. Start-up needs at least 32 characters; this one has 72, so that tests can also sign
    /// HS384 and HS512 tokens with it (the library needs 48 and 64 bytes for those) and prove the API refuses them.
    /// </summary>
    public const string SigningKey = "test-only-signing-key-not-a-secret-0123456789-abcdefghijklmnopqrstuvwxyz";

    /// <summary>Email of the first Manager that start-up creates in every test database (US-02).</summary>
    public const string AdminEmail = "admin@inventory.test";

    /// <summary>Password of the first Manager; it meets the password policy.</summary>
    public const string AdminPassword = "Admin-Test-2026";

    /// <summary>Password <see cref="CreateUserAsync"/> gives every user it creates; it meets the password policy.</summary>
    public const string UserPassword = "Staff-Test-2026";

    /// <summary>
    /// Full path of this factory's SQLite database file. A test may point a second factory at the file of a first one
    /// (after disposing the first) to restart the app on the same data.
    /// </summary>
    public string DatabasePath { get; init; } =
        Path.Combine(Path.GetTempPath(), $"inventory-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Configuration values given to the app, by key. Change them before the app starts (before the first
    /// <c>CreateClient()</c> or <c>Services</c>). A value of null means "not set": the key is still in the in-memory
    /// source with a null value, which hides any value from an earlier source such as an environment variable.
    /// </summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        [JwtOptions.SigningKeyVariable] = SigningKey,
        [StartupChecks.AdminEmailVariable] = AdminEmail,
        [StartupChecks.AdminPasswordVariable] = AdminPassword,
    };

    /// <summary>
    /// When set, the app uses this clock instead of the real one: tests move it forward with <c>Advance</c> to check
    /// token expiry (AC-01.2), the end of a lock-out (AC-01.4) and newest-first ordering, without waiting.
    /// Leave it null for the real clock.
    /// </summary>
    /// <example><c>await using var factory = new TestApiFactory { FakeTime = new FakeTimeProvider(start) };</c></example>
    public FakeTimeProvider? FakeTime { get; init; }

    /// <summary>
    /// Optional extra service registrations, applied after Program.cs and after the fake clock, so a test can replace
    /// one service with a test double.
    /// </summary>
    /// <example><c>new TestApiFactory { ReplaceServices = services => services.AddSingleton&lt;IPasswordHasher&lt;AppUser&gt;&gt;(hasher) }</c></example>
    public Action<IServiceCollection>? ReplaceServices { get; init; }

    /// <summary>Every log entry the app wrote while this factory was running, including start-up messages.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>
    /// Points the app at the temporary database, selects the Testing environment, sets the test secrets and, when
    /// asked, the fake clock.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");

        // Added last, so it wins over every other source; null values included, so they hide earlier ones.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(Settings));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);

            // ConfigureTestServices runs after Program.cs, so this registration is the last one and wins.
            if (FakeTime is not null)
            {
                services.AddSingleton<TimeProvider>(FakeTime);
            }

            ReplaceServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Creates an active user with <see cref="UserPassword"/> and the given roles, directly through Identity (the
    /// user management API arrives in T-014). Use the <see cref="Roles"/> constants.
    /// </summary>
    public async Task<AppUser> CreateUserAsync(string email, params string[] roles)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<Clock>();

        var user = new AppUser { UserName = email, Email = email, CreatedAt = clock.UtcNow };
        ThrowIfFailed(await users.CreateAsync(user, UserPassword));
        if (roles.Length > 0)
        {
            ThrowIfFailed(await users.AddToRolesAsync(user, roles));
        }

        return user;
    }

    /// <summary>Signs in through the real endpoint and returns the access token. Fails the test if sign-in fails.</summary>
    public async Task<string> SignInAsync(string email, string password = UserPassword)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/sign-in", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>An <see cref="HttpClient"/> that sends <c>Authorization: Bearer {token}</c> on every request.</summary>
    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Creates a new user with the given roles, signs them in, and returns a client that sends their token. The usual
    /// way for a test to call an endpoint as, say, a Warehouse user: <c>await factory.CreateClientAsAsync(Roles.Warehouse)</c>.
    /// </summary>
    public async Task<HttpClient> CreateClientAsAsync(params string[] roles)
    {
        var email = $"user-{Guid.NewGuid():N}@inventory.test";
        await CreateUserAsync(email, roles);
        return CreateClientWithToken(await SignInAsync(email));
    }

    /// <summary>Stops the app and deletes its database files.</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // SQLite keeps pooled connections open; release them so the files can be deleted.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(DatabasePath + suffix);
        }

        GC.SuppressFinalize(this);
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }
}
