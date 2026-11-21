using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-003: the start-up sequence of architecture 6 (tolerance, signing key, migrations, roles, first Manager) and the
/// design 7.4 messages (US-02, PRD section 5).
/// </summary>
/// <remarks>
/// Every test starts its own app on its own empty database, because what is tested is what happens at start-up.
/// When a check fails, Program.cs logs the message and returns before the app listens, so
/// <c>CreateClient()</c> throws (architecture section 9, risk 5). The tests assert that, read the message from the
/// captured log, and look in the database file directly.
/// <para>
/// A variable set to null in <see cref="TestApiFactory.Settings"/> is "not set" for the app, even if the machine running
/// the tests has, say, <c>ADMIN_EMAIL</c> as an environment variable (<see cref="TestSettingsIsolationTests"/>).
/// </para>
/// </remarks>
public class StartupTests
{
    private const string ToleranceKey = "Inventory:TolerancePercent";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- AC-02.1, AC-02.2: the first Manager -----------------------------------------------------------------

    /// <summary>
    /// AC-02.1: an empty database plus ADMIN_EMAIL and ADMIN_PASSWORD gives exactly one active user with that email and
    /// the Manager role only, who can sign in. Architecture 3.2: the three roles exist.
    /// </summary>
    [Fact]
    public async Task Empty_database_gets_one_active_manager_who_can_sign_in()
    {
        await using var app = new TestApiFactory();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/sign-in",
            new { email = TestApiFactory.AdminEmail, password = TestApiFactory.AdminPassword }, Cancel);
        var body = await response.Content.ReadFromJsonAsync<SignInResponse>(Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Manager"], body!.Roles);

        await using var db = OpenDatabase(app.DatabasePath);
        var user = Assert.Single(await db.Users.AsNoTracking().ToListAsync(Cancel));
        Assert.Equal(TestApiFactory.AdminEmail, user.Email);
        Assert.True(user.IsActive);
        var roleNames = await db.UserRoles.Where(link => link.UserId == user.Id)
            .Join(db.Roles, link => link.RoleId, role => role.Id, (_, role) => role.Name)
            .ToListAsync(Cancel);
        Assert.Equal(["Manager"], roleNames);
        Assert.Equal(["Manager", "Production", "Warehouse"], (await db.Roles.Select(role => role.Name).ToListAsync(Cancel)).Order());
    }

    /// <summary>Architecture 6, Logging: start-up never writes the admin password or the signing key to the log.</summary>
    [Fact]
    public async Task Start_up_does_not_log_secrets()
    {
        await using var app = new TestApiFactory();
        app.CreateClient();

        Assert.Contains(app.Logs.Entries, entry => entry.Message.Contains("Created the first Manager"));
        Assert.DoesNotContain(app.Logs.Entries, entry =>
            entry.Message.Contains(TestApiFactory.AdminPassword) || entry.Message.Contains(TestApiFactory.SigningKey));
    }

    /// <summary>
    /// AC-02.2 and PRD assumption 18: once a user exists, restarting with different values, empty values or a weak
    /// password changes nothing: same single user, same password hash, and the old password still signs in.
    /// </summary>
    [Theory]
    [InlineData("someone.else@inventory.test", "Different-Pass-2026")]
    [InlineData("", "")]
    [InlineData(null, null)]
    [InlineData("someone.else@inventory.test", "weak")]
    public async Task Restart_with_existing_users_changes_no_user(string? newEmail, string? newPassword)
    {
        await using var first = new TestApiFactory();
        first.CreateClient();
        var before = await SnapshotUsersAsync(first.DatabasePath);

        // A second app on the same database file stands in for a restart with new settings.
        await using var restarted = new TestApiFactory { DatabasePath = first.DatabasePath };
        restarted.Settings[StartupChecks.AdminEmailVariable] = newEmail;
        restarted.Settings[StartupChecks.AdminPasswordVariable] = newPassword;
        var client = restarted.CreateClient();

        Assert.Equal(before, await SnapshotUsersAsync(first.DatabasePath));
        var oldSignIn = await client.PostAsJsonAsync("/api/v1/auth/sign-in",
            new { email = TestApiFactory.AdminEmail, password = TestApiFactory.AdminPassword }, Cancel);
        Assert.Equal(HttpStatusCode.OK, oldSignIn.StatusCode);
    }

    // ---- AC-02.3, AC-02.4: refusing to start without a valid first Manager ----------------------------------

    /// <summary>
    /// AC-02.3 and design 7.4: with no users, a missing or empty ADMIN_EMAIL or ADMIN_PASSWORD stops start-up with the
    /// message naming that variable, logged at Critical level, and no user is created.
    /// </summary>
    [Theory]
    [InlineData(StartupChecks.AdminEmailVariable, null, StartupChecks.AdminEmailMissingMessage)]
    [InlineData(StartupChecks.AdminEmailVariable, "", StartupChecks.AdminEmailMissingMessage)]
    [InlineData(StartupChecks.AdminEmailVariable, "   ", StartupChecks.AdminEmailMissingMessage)]
    [InlineData(StartupChecks.AdminPasswordVariable, null, StartupChecks.AdminPasswordMissingMessage)]
    [InlineData(StartupChecks.AdminPasswordVariable, "", StartupChecks.AdminPasswordMissingMessage)]
    public async Task Missing_admin_variable_stops_start_up(string variable, string? value, string message)
    {
        await using var app = new TestApiFactory();
        app.Settings[variable] = value;

        AssertStartupStopped(app, message);
        Assert.Equal(0, await CountUsersAsync(app.DatabasePath));
    }

    /// <summary>The design 7.4 messages word for word (AC-02.3), so a typo in the constants cannot hide behind the tests above.</summary>
    [Fact]
    public void Admin_variable_messages_match_the_design()
    {
        Assert.Equal("Start-up stopped: ADMIN_EMAIL is not set. Set ADMIN_EMAIL and ADMIN_PASSWORD to create the first Manager.",
            StartupChecks.AdminEmailMissingMessage);
        Assert.Equal("Start-up stopped: ADMIN_PASSWORD is not set. Set ADMIN_EMAIL and ADMIN_PASSWORD to create the first Manager.",
            StartupChecks.AdminPasswordMissingMessage);
        Assert.Equal("Start-up stopped: ADMIN_PASSWORD does not meet the password policy. It must be at least 8 characters and include an upper-case letter, a lower-case letter and a digit.",
            StartupChecks.AdminPasswordWeakMessage);
        Assert.Equal("Start-up stopped: JWT_SIGNING_KEY is not set. Set it to a random secret of at least 32 characters.",
            StartupChecks.SigningKeyMissingMessage);
    }

    /// <summary>
    /// AC-02.4 and PRD section 5: with no users, an ADMIN_PASSWORD that breaks the policy (8+ characters, an upper-case
    /// letter, a lower-case letter, a digit) stops start-up with the message stating the policy, and no user is created.
    /// The roles created earlier in the same write transaction are rolled back too, so nothing is left half-done.
    /// </summary>
    [Theory]
    [InlineData("Short1A")]        // 7 characters
    [InlineData("Aa1\U0001F9C0\U0001F9C0\U0001F9C0")] // decision #118: 6 characters (9 UTF-16 code units)
    [InlineData("nouppercase1")]
    [InlineData("NOLOWERCASE1")]
    [InlineData("NoDigitsHere")]
    public async Task Weak_admin_password_stops_start_up(string password)
    {
        await using var app = new TestApiFactory();
        app.Settings[StartupChecks.AdminPasswordVariable] = password;

        AssertStartupStopped(app, StartupChecks.AdminPasswordWeakMessage);
        Assert.Equal(0, await CountUsersAsync(app.DatabasePath));
        Assert.Equal(0, await CountRolesAsync(app.DatabasePath));
    }

    /// <summary>PRD section 5: the policy's minimum is allowed: 8 characters with one of each, no symbol needed.</summary>
    [Fact]
    public async Task Admin_password_at_the_policy_minimum_is_accepted()
    {
        await using var app = new TestApiFactory();
        app.Settings[StartupChecks.AdminPasswordVariable] = "Abcdefg1";

        app.CreateClient();

        Assert.Equal(1, await CountUsersAsync(app.DatabasePath));
    }

    /// <summary>PRD section 5, decision #118: 8 characters, five of them emoji, also meets the minimum at start-up.</summary>
    [Fact]
    public async Task Admin_password_of_8_characters_with_emoji_is_accepted()
    {
        await using var app = new TestApiFactory();
        app.Settings[StartupChecks.AdminPasswordVariable] = "Aa1\U0001F9C0\U0001F9C0\U0001F9C0\U0001F9C0\U0001F9C0";

        app.CreateClient();

        Assert.Equal(1, await CountUsersAsync(app.DatabasePath));
    }

    /// <summary>
    /// Design 7.4 pattern (decision #53): an ADMIN_EMAIL that is not an email address stops start-up. Identity refuses the
    /// user only when it tries to create it, after the roles were added, so this proves the rollback: no user, no role.
    /// </summary>
    [Fact]
    public async Task Invalid_admin_email_stops_start_up()
    {
        await using var app = new TestApiFactory();
        app.Settings[StartupChecks.AdminEmailVariable] = "not-an-email";

        AssertStartupStopped(app, StartupChecks.AdminEmailInvalidMessage);
        Assert.Equal(0, await CountUsersAsync(app.DatabasePath));
        Assert.Equal(0, await CountRolesAsync(app.DatabasePath));
    }

    // ---- Signing key and tolerance (PRD section 5, design 7.4) ----------------------------------------------

    /// <summary>
    /// Design 7.4 and architecture 6: a missing, empty or short (under 32 characters) JWT_SIGNING_KEY stops start-up,
    /// before the database is even created (start-up step 2 comes before migrations).
    /// </summary>
    [Theory]
    [InlineData(null, StartupChecks.SigningKeyMissingMessage)]
    [InlineData("", StartupChecks.SigningKeyMissingMessage)]
    [InlineData("   ", StartupChecks.SigningKeyMissingMessage)]
    [InlineData("0123456789012345678901234567890", StartupChecks.SigningKeyTooShortMessage)] // 31 characters
    public async Task Missing_or_short_signing_key_stops_start_up(string? key, string message)
    {
        await using var app = new TestApiFactory();
        app.Settings["JWT_SIGNING_KEY"] = key;

        AssertStartupStopped(app, message);
        Assert.False(File.Exists(app.DatabasePath));
    }

    /// <summary>Architecture 6: a key of exactly 32 characters is accepted, and tokens signed with it work.</summary>
    [Fact]
    public async Task Signing_key_of_32_characters_is_accepted()
    {
        await using var app = new ProbeApiFactory();
        app.Settings["JWT_SIGNING_KEY"] = "01234567890123456789012345678901";

        var client = app.CreateClientWithToken(await app.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));
        var response = await client.GetAsync("/test-probe/auth/signed-in", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// PRD section 5 and design 7.4: a tolerance below 0, above 100, or not a number stops start-up with
    /// "Tolerance must be between 0 and 100. Current value: {value}.".
    /// </summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("150")]
    [InlineData("abc")]
    public async Task Tolerance_outside_0_to_100_stops_start_up(string tolerance)
    {
        await using var app = new TestApiFactory();
        app.Settings[ToleranceKey] = tolerance;

        AssertStartupStopped(app, $"Start-up stopped: Tolerance must be between 0 and 100. Current value: {tolerance}.");
        Assert.False(File.Exists(app.DatabasePath));
    }

    /// <summary>PRD section 5: the limits 0 and 100 themselves are allowed.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    public async Task Tolerance_at_the_limits_is_accepted(string tolerance)
    {
        await using var app = new TestApiFactory();
        app.Settings[ToleranceKey] = tolerance;

        var response = await app.CreateClient().GetAsync("/health", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    /// <summary>
    /// The app did not start, and the log holds exactly one Critical entry: the expected design 7.4 message.
    /// </summary>
    private static void AssertStartupStopped(TestApiFactory app, string message)
    {
        Assert.ThrowsAny<Exception>(() => app.CreateClient());

        var critical = Assert.Single(app.Logs.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Equal(message, critical.Message);
    }

    /// <summary>Opens the test database directly, without the app, which may not have started.</summary>
    private static AppDbContext OpenDatabase(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);

    private static async Task<int> CountUsersAsync(string path)
    {
        await using var db = OpenDatabase(path);
        return await db.Users.CountAsync(Cancel);
    }

    private static async Task<int> CountRolesAsync(string path)
    {
        await using var db = OpenDatabase(path);
        return await db.Roles.CountAsync(Cancel);
    }

    /// <summary>Everything about the users that a restart must not change.</summary>
    private static async Task<string> SnapshotUsersAsync(string path)
    {
        await using var db = OpenDatabase(path);
        var users = await db.Users.AsNoTracking().OrderBy(user => user.Id)
            .Select(user => $"{user.Id}|{user.Email}|{user.PasswordHash}|{user.SecurityStamp}|{user.IsActive}")
            .ToListAsync(Cancel);
        var links = await db.UserRoles.AsNoTracking().OrderBy(link => link.UserId).ThenBy(link => link.RoleId)
            .Select(link => $"{link.UserId}|{link.RoleId}")
            .ToListAsync(Cancel);
        return string.Join("\n", users.Concat(links));
    }
}

/// <summary>
/// T-003 and architecture 6: when a start-up check fails, the process exits with code 1 without listening.
/// </summary>
/// <remarks>
/// <c>WebApplicationFactory</c> cannot see the exit code, so this test runs the real entry point (<c>Main</c>) in the
/// test process with an empty signing key. It is in a collection with parallel running turned off, so no other test
/// app is starting at the same moment.
/// </remarks>
[Collection(nameof(ProcessWideCollection))]
public class StartupExitCodeTests
{
    /// <summary>Architecture 6: a failed start-up check makes Main return exit code 1 (and it returns, so it never listened).</summary>
    [Fact]
    public async Task Failed_check_exits_with_code_1()
    {
        var entryPoint = typeof(Program).Assembly.EntryPoint!;
        string[] args =
        [
            "--environment=Testing",
            "--JWT_SIGNING_KEY=",
            $"--ConnectionStrings:Default=Data Source={Path.Combine(Path.GetTempPath(), $"inventory-tests-{Guid.NewGuid():N}.db")}",
        ];

        // If the check did not stop the app, Main would run the server and never return; the timeout catches that.
        var exitCode = await Task.Run(() => entryPoint.Invoke(null, [args]), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }
}

/// <summary>
/// The test settings of <see cref="TestApiFactory"/> cannot be overridden from outside: a secret set as an environment
/// variable on the machine running the tests does not reach a test app, not even where the test leaves the setting
/// out on purpose (AC-02.5 and the "missing variable" cases of <see cref="StartupTests"/>).
/// </summary>
/// <remarks>
/// The test sets real environment variables of the test process, which every app started meanwhile would read, so it
/// runs in the same no-parallel collection as <see cref="StartupExitCodeTests"/> and puts the old values back.
/// </remarks>
[Collection(nameof(ProcessWideCollection))]
public class TestSettingsIsolationTests
{
    /// <summary>
    /// A setting left out (null) stays unset even when the same name is an environment variable with a valid value:
    /// start-up still stops with the "is not set" message.
    /// </summary>
    [Theory]
    [InlineData(StartupChecks.AdminEmailVariable, "from-environment@example.com", StartupChecks.AdminEmailMissingMessage)]
    [InlineData(StartupChecks.AdminPasswordVariable, "From-Environment-2026", StartupChecks.AdminPasswordMissingMessage)]
    [InlineData("JWT_SIGNING_KEY", "from-environment-signing-key-0123456789-0123456789", StartupChecks.SigningKeyMissingMessage)]
    public async Task Environment_variable_cannot_fill_a_setting_left_out(string variable, string environmentValue, string message)
    {
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, environmentValue);
        try
        {
            await using var app = new TestApiFactory();
            app.Settings[variable] = null;

            Assert.ThrowsAny<Exception>(() => app.CreateClient());
            var critical = Assert.Single(app.Logs.Entries, entry => entry.Level == LogLevel.Critical);
            Assert.Equal(message, critical.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    /// <summary>A setting the test gives a value keeps that value even when the environment variable says otherwise.</summary>
    [Fact]
    public async Task Environment_variable_cannot_override_a_test_value()
    {
        var previous = Environment.GetEnvironmentVariable(StartupChecks.AdminEmailVariable);
        Environment.SetEnvironmentVariable(StartupChecks.AdminEmailVariable, "from-environment@example.com");
        try
        {
            await using var app = new TestApiFactory();

            app.CreateClient();

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(TestApiFactory.AdminEmail, (await db.Users.SingleAsync(TestContext.Current.CancellationToken)).Email);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StartupChecks.AdminEmailVariable, previous);
        }
    }
}

/// <summary>
/// Tests that touch something the whole test process shares (its entry point, its environment variables). xUnit runs
/// this collection on its own, with no other test running in parallel.
/// </summary>
[CollectionDefinition(nameof(ProcessWideCollection), DisableParallelization = true)]
public class ProcessWideCollection;
