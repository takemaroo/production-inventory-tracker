using System.Globalization;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// The start-up sequence of architecture 6 ("First Manager and start-up checks"), run by Program.cs before the
/// app listens: (1) tolerance 0–100, (2) <c>JWT_SIGNING_KEY</c>, (3) migrations (in <see cref="DatabaseSetup"/>),
/// (4) the four roles of <see cref="Roles.All"/>, (5) the first Manager from <c>ADMIN_EMAIL</c> / <c>ADMIN_PASSWORD</c>.
/// </summary>
/// <remarks>
/// A failed check throws <see cref="StartupException"/> with the design 7.4 message. Checking at start-up, rather
/// than on the first request that needs the value, means a misconfigured app never half-works: the owner sees the
/// problem the moment they start it.
/// </remarks>
public static class StartupChecks
{
    /// <summary>Configuration key (and environment variable name) of the first Manager's email (US-02).</summary>
    public const string AdminEmailVariable = "ADMIN_EMAIL";

    /// <summary>Configuration key (and environment variable name) of the first Manager's password (US-02).</summary>
    public const string AdminPasswordVariable = "ADMIN_PASSWORD";

    /// <summary>Design 7.4 and AC-02.3.</summary>
    public const string AdminEmailMissingMessage =
        "Start-up stopped: ADMIN_EMAIL is not set. Set ADMIN_EMAIL and ADMIN_PASSWORD to create the first Manager.";

    /// <summary>Design 7.4 and AC-02.3.</summary>
    public const string AdminPasswordMissingMessage =
        "Start-up stopped: ADMIN_PASSWORD is not set. Set ADMIN_EMAIL and ADMIN_PASSWORD to create the first Manager.";

    /// <summary>Design 7.4 and AC-02.4: the message states the policy.</summary>
    public const string AdminPasswordWeakMessage =
        "Start-up stopped: ADMIN_PASSWORD does not meet the password policy. It must be at least 8 characters and include an upper-case letter, a lower-case letter and a digit.";

    /// <summary>
    /// Not in design 7.4, written in its pattern: Identity refused <c>ADMIN_EMAIL</c> as an email address.
    /// </summary>
    public const string AdminEmailInvalidMessage =
        "Start-up stopped: ADMIN_EMAIL is not a valid email address. Set ADMIN_EMAIL and ADMIN_PASSWORD to create the first Manager.";

    /// <summary>Design 7.4: the token signing key is missing or empty.</summary>
    public const string SigningKeyMissingMessage =
        "Start-up stopped: JWT_SIGNING_KEY is not set. Set it to a random secret of at least 32 characters.";

    /// <summary>
    /// Design 7.4 pattern for a key that is set but too short; "is not set" would mislead the owner here.
    /// </summary>
    public const string SigningKeyTooShortMessage =
        "Start-up stopped: JWT_SIGNING_KEY is too short. Set it to a random secret of at least 32 characters.";

    private const string ToleranceKey = InventoryOptions.SectionName + ":" + nameof(InventoryOptions.TolerancePercent);

    /// <summary>Steps 1 and 2: the settings that need no database. Throws <see cref="StartupException"/> on the first failure.</summary>
    public static void CheckSettings(IConfiguration configuration)
    {
        CheckTolerance(configuration);
        CheckSigningKey(configuration);
    }

    /// <summary>
    /// Steps 4 and 5: creates any missing role, then, only when the database has no users, the first Manager.
    /// Runs after the migrations, inside one write transaction, so a failure leaves no role or user behind.
    /// </summary>
    public static async Task SeedRolesAndFirstManagerAsync(IServiceProvider services, IConfiguration configuration)
    {
        // Scoped services (DbContext, UserManager) need a scope; at start-up there is no request to provide one.
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = provider.GetRequiredService<UserManager<AppUser>>();
        var clock = provider.GetRequiredService<Clock>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupChecks).FullName!);

        // Identity's managers save through the same scoped DbContext, so their writes join this transaction.
        await db.InWriteTransactionAsync(async () =>
        {
            await EnsureRolesAsync(roleManager);

            // AC-02.2: once any user exists, the variables are ignored. Nothing about users is created or changed,
            // even if ADMIN_EMAIL or ADMIN_PASSWORD now hold different values.
            if (await db.Users.AnyAsync())
            {
                return;
            }

            var manager = await CreateFirstManagerAsync(userManager, configuration, clock);
            logger.LogInformation("Created the first Manager (user {UserId}) from ADMIN_EMAIL and ADMIN_PASSWORD.", manager.Id);
        });
    }

    /// <summary>
    /// PRD section 5: the tolerance must be a number from 0 to 100. The raw text is read, rather than the bound
    /// <see cref="InventoryOptions"/>, so a value that is not a number at all gets the same clear message.
    /// </summary>
    private static void CheckTolerance(IConfiguration configuration)
    {
        var raw = configuration[ToleranceKey];
        if (raw is null)
        {
            return; // not configured: the default of 2 applies (InventoryOptions)
        }

        var isNumber = decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance);
        if (!isNumber || tolerance < 0m || tolerance > 100m)
        {
            throw new StartupException($"Start-up stopped: Tolerance must be between 0 and 100. Current value: {raw.Trim()}.");
        }
    }

    /// <summary>Architecture 6: the signing key must be set and at least 32 characters (HS256 needs a 256-bit key).</summary>
    private static void CheckSigningKey(IConfiguration configuration)
    {
        var key = configuration[JwtOptions.SigningKeyVariable];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new StartupException(SigningKeyMissingMessage);
        }

        if (key.Length < JwtOptions.MinimumSigningKeyLength)
        {
            throw new StartupException(SigningKeyTooShortMessage);
        }
    }

    /// <summary>
    /// Architecture 3.2 and 6: the roles of <see cref="Roles.All"/> (Warehouse, Production, Manager, Seller) always exist.
    /// </summary>
    /// <remarks>
    /// Only a missing role is created; an existing one is left exactly as it is. That makes this step safe to run on
    /// every start: a version 1 database gains the one Seller row on its first v1.1 start, no user is given it, and later
    /// starts change nothing (AC-30.3, decision #155). Roles are data, not schema, so they are seeded here rather than in
    /// a migration.
    /// </remarks>
    private static async Task EnsureRolesAsync(RoleManager<IdentityRole> roleManager)
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                ThrowIfFailed(await roleManager.CreateAsync(new IdentityRole(role)), $"create the {role} role");
            }
        }
    }

    /// <summary>
    /// AC-02.1, AC-02.3, AC-02.4: checks both variables and the password policy, then creates one active user with the
    /// Manager role only.
    /// </summary>
    private static async Task<AppUser> CreateFirstManagerAsync(
        UserManager<AppUser> userManager, IConfiguration configuration, Clock clock)
    {
        // AC-02.3: a missing or empty variable stops start-up and names the variable.
        var email = Text.Clean(configuration[AdminEmailVariable]) ?? throw new StartupException(AdminEmailMissingMessage);
        var password = configuration[AdminPasswordVariable];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new StartupException(AdminPasswordMissingMessage);
        }

        var user = new AppUser { UserName = email, Email = email, IsActive = true, CreatedAt = clock.UtcNow };

        // AC-02.4: the password is checked against the same validators sign-up uses (the options in AuthSetup),
        // before anything is written. CreateAsync would check it too, but mixed with other errors.
        foreach (var validator in userManager.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(userManager, user, password)).Succeeded)
            {
                throw new StartupException(AdminPasswordWeakMessage);
            }
        }

        // CreateAsync stores a salted hash of the password, never the password itself (see SignInService).
        var created = await userManager.CreateAsync(user, password);
        if (created.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.InvalidEmail)))
        {
            throw new StartupException(AdminEmailInvalidMessage);
        }

        ThrowIfFailed(created, "create the first Manager");

        // AC-02.1: the Manager role only.
        ThrowIfFailed(await userManager.AddToRoleAsync(user, Roles.Manager), "give the first Manager the Manager role");
        return user;
    }

    /// <summary>
    /// Any other Identity failure here is unexpected (for example a database problem), so it is a normal crash with
    /// the details in the log, not a configuration message for the owner.
    /// </summary>
    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var reasons = string.Join(" ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"Start-up could not {action}: {reasons}");
        }
    }
}
