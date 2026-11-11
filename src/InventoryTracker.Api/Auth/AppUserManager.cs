using InventoryTracker.Api.Common.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// ASP.NET Core Identity's <see cref="UserManager{TUser}"/>, with the lock-out clock taken from the app's
/// <see cref="Clock"/> instead of the computer's clock.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UserManager{TUser}"/> is Identity's main service. It creates users, hashes and checks passwords,
/// manages roles and counts failed sign-ins. It stores everything through the EF Core user store, so the data ends
/// up in the <c>AspNetUsers</c> and <c>AspNetUserRoles</c> tables of <c>AppDbContext</c>. Code asks for
/// <c>UserManager&lt;AppUser&gt;</c> in its constructor; <see cref="AuthSetup"/> registers this class as that service.
/// </para>
/// <para>
/// Why the subclass: Identity 10 decides "is this account locked?" with <c>DateTimeOffset.UtcNow</c>, the real
/// clock. Everything else in this API reads the time from <see cref="Clock"/> (architecture 6, Time), and the tests
/// replace that clock to check that a lock really ends after 15 minutes (AC-01.4) without waiting 15 minutes. The two
/// lock-out methods below do exactly what Identity's own versions do, but read the time from <see cref="Clock"/>.
/// Everything else (password hashing, roles, security stamps) is Identity's unchanged code.
/// </para>
/// </remarks>
public sealed class AppUserManager(
    IUserStore<AppUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<AppUser> passwordHasher,
    IEnumerable<IUserValidator<AppUser>> userValidators,
    IEnumerable<IPasswordValidator<AppUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<AppUser>> logger,
    Clock clock)
    : UserManager<AppUser>(store, optionsAccessor, passwordHasher, userValidators, passwordValidators,
        keyNormalizer, errors, services, logger)
{
    /// <summary>
    /// AC-01.4: true while the account's lock-out end time is still in the future (by the app's clock).
    /// The lock ends exactly when the 15 minutes are over.
    /// </summary>
    public override async Task<bool> IsLockedOutAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!await GetLockoutEnabledAsync(user))
        {
            return false;
        }

        var lockoutEnd = await GetLockoutEndDateAsync(user);
        return lockoutEnd > Now;
    }

    /// <summary>
    /// AC-01.4: records one failed sign-in. The fifth failure in a row (<c>MaxFailedAccessAttempts</c>) locks the
    /// account for 15 minutes (<c>DefaultLockoutTimeSpan</c>) and starts the count again from zero, as Identity does.
    /// </summary>
    public override async Task<IdentityResult> AccessFailedAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // The EF Core user store implements the lock-out part of Identity's store interface; the cast reaches it.
        var lockoutStore = (IUserLockoutStore<AppUser>)Store;
        var failures = await lockoutStore.IncrementAccessFailedCountAsync(user, CancellationToken);
        if (failures >= Options.Lockout.MaxFailedAccessAttempts)
        {
            await lockoutStore.SetLockoutEndDateAsync(user, Now.Add(Options.Lockout.DefaultLockoutTimeSpan), CancellationToken);
            await lockoutStore.ResetAccessFailedCountAsync(user, CancellationToken);
        }

        // UpdateUserAsync validates the user and saves the changed row. It refuses the write if the row changed since
        // it was read; SignInService prevents that by handling one attempt per account at a time (AccountLocks).
        return await UpdateUserAsync(user);
    }

    private DateTimeOffset Now => new(clock.UtcNow);
}
