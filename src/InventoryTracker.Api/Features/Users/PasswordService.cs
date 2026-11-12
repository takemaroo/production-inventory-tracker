using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Users;

/// <summary>
/// Password changes: a user changing their own (US-04, <c>POST /auth/change-password</c>) and a Manager setting a
/// new one for another user (AC-03.7, <c>PUT /users/{id}/password</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a password change signs the user out everywhere</b> (architecture 4.1 item 6, decision #26). Identity gives
/// every user a <em>security stamp</em>: a random value stored on the user row that changes whenever something about
/// the user's credentials changes. Each token carries a copy of the stamp it was issued with, and
/// <see cref="TokenUserCheck"/> compares the copy with the row on every request. Saving a new password here also
/// gives the user a new stamp (<c>UpdateSecurityStampAsync</c>), so every token issued before the change is refused
/// with <c>401</c> from the next request on, including the token that made the change. That is the point: if someone
/// else had the old password or a stolen token, they lose access; the user signs in again with the new password.
/// </para>
/// <para>
/// <b>Why the password hashing happens outside the write transaction</b> (decision #70). Checking or hashing a
/// password is deliberately slow: Identity runs its hash function many thousands of times (about 45 ms here) so that
/// someone who steals the database cannot try billions of guesses. SQLite lets only one write transaction run at a
/// time (architecture 5.2), so 45 ms of hashing inside <c>InWriteTransactionAsync</c> would make every other write in
/// the whole app wait; a user sending wrong current passwords in a loop could stall everyone. So the slow work is done
/// first, on a copy of the user read before the transaction. The transaction then only reloads the row, checks that
/// nothing the slow work relied on has changed meanwhile, and saves: a few milliseconds. This is the one place, like
/// sign-in (decision #61), where the reads and checks are not all inside the transaction.
/// </para>
/// <para>
/// <b>Why wrong current passwords count towards the lock-out</b> (decision #71, AC-01.4). Change-password checks a
/// password, so without a limit someone holding a stolen token could guess the real password as often as they liked
/// for the token's 8 hours. So it shares sign-in's counter: 5 failures in a row, from sign-in and change-password
/// together, lock the account for 15 minutes. Counting has the same race as sign-in (Identity reads the count, adds
/// one and writes it back), so it runs inside the same per-account lock (<see cref="AccountLocks"/>). That lock is
/// held by one account only, in memory; it is not the database's write lock, so the slow check inside it does not
/// hold up anyone else.
/// </para>
/// <para>
/// <b>Why a Manager's reset clears the lock-out</b> (decision #72). A reset is a Manager deliberately helping a user
/// who is stuck, often one who locked themselves out by forgetting the password, so they can sign in with the new
/// password at once.
/// </para>
/// </remarks>
public sealed class PasswordService(
    AppDbContext db, UserManager<AppUser> userManager, AccountLocks accountLocks, ILogger<PasswordService> logger)
{
    /// <summary>Design 7.1 and AC-01.4: the same code sign-in gives while the account is locked (decision #71).</summary>
    public const string AccountLockedCode = "account-locked";

    /// <summary>Design 7.1: the code for a password that fails the policy (AC-03.3, AC-03.7, AC-04.1).</summary>
    public const string WeakPasswordCode = "weak-password";

    /// <summary>Design 7.1: the password policy of PRD section 5, stated in full whichever part of it failed.</summary>
    public const string WeakPasswordMessage =
        "Password must be at least 8 characters and include an upper-case letter, a lower-case letter and a digit.";

    /// <summary>Design 7.1: the code for AC-04.2.</summary>
    public const string CurrentPasswordIncorrectCode = "current-password-incorrect";

    /// <summary>AC-04.2, exact PRD wording.</summary>
    public const string CurrentPasswordIncorrectMessage = "Current password is incorrect.";

    /// <summary>A missing <c>password</c> field when creating a user.</summary>
    public const string PasswordRequiredMessage = "Password is required.";

    /// <summary>A missing <c>newPassword</c> field.</summary>
    public const string NewPasswordRequiredMessage = "New password is required.";

    /// <summary>A missing <c>currentPassword</c> field.</summary>
    public const string CurrentPasswordRequiredMessage = "Current password is required.";

    /// <summary>
    /// <c>POST /auth/change-password</c> (AC-04.1, AC-04.2): with the right current password and a new one that meets
    /// the policy, the new password replaces the old one. The caller's existing tokens stop working.
    /// </summary>
    /// <param name="userId">The signed-in user, from the token's <c>sub</c> claim. Users can only change their own.</param>
    /// <param name="tokenStamp">The security stamp the caller's token was issued with (its <c>stamp</c> claim).</param>
    /// <param name="request">The current and the new password.</param>
    /// <param name="cancellationToken">Cancels the reads, the save and the commit.</param>
    public async Task ChangeOwnPasswordAsync(
        string userId, string tokenStamp, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            errors.Add("currentPassword", CurrentPasswordRequiredMessage);
        }

        if (string.IsNullOrEmpty(request.NewPassword))
        {
            errors.Add("newPassword", NewPasswordRequiredMessage);
        }

        errors.ThrowIfAny();

        // The account's lock is keyed on the normalized email, as in sign-in (decision #61), so this first quick read
        // finds the email. Emails never change in v1, so the key stays right.
        var email = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotAuthenticated();

        // Decision #71: one password check at a time for this account, shared with sign-in. The `using` block releases
        // the lock as soon as the check is decided, even if it throws.
        AppUser snapshot;
        using (await accountLocks.AcquireAsync(userManager.NormalizeEmail(email)))
        {
            snapshot = await CheckCurrentPasswordAsync(userId, tokenStamp, request.CurrentPassword!);
        }

        // The user object above is tracked by this request's DbContext. Forget it, so the transaction below loads the
        // row as it is then, not this copy from memory, and its re-check compares against the database.
        db.ChangeTracker.Clear();

        await ThrowIfWeakAsync(userManager, snapshot, request.NewPassword!, "newPassword");
        var newHash = userManager.PasswordHasher.HashPassword(snapshot, request.NewPassword!);

        await db.InWriteTransactionAsync(async () =>
        {
            // Reloaded inside the transaction, so this is the row as it is now, and no other write can change it
            // until the commit. FindByIdAsync returns a tracked user: changes to it are saved.
            var user = await userManager.FindByIdAsync(userId) ?? throw NotAuthenticated();

            // The slow work above ran outside the transaction, so the account may have changed meanwhile. One check
            // covers every such change: a deactivation (AC-03.6), a deactivation followed by a reactivation, a
            // Manager's reset (AC-03.7) and another change-password that finished first. Each of them gave the user a
            // new security stamp, so the stamp no longer matches the one in the caller's token. That token is then no
            // longer valid (the next request would get 401 anyway), and the current password it proved may be out of
            // date, so the change is refused with the same 401 not-authenticated and nothing is saved.
            ThrowIfSessionEnded(user, tokenStamp);

            await SaveNewHashAsync(user, newHash);
            logger.LogInformation("User {UserId} changed their password.", user.Id);
        }, cancellationToken);
    }

    /// <summary>
    /// Decisions #70 and #71, in sign-in's order (architecture 4.1 item 5), while holding the account's lock: an inactive
    /// user, or a token whose stamp is out of date, is refused (<c>401</c>); a locked account is refused with <c>account-locked</c> even when the password is
    /// right, and nothing is counted; a wrong password counts one failure (the fifth in a row locks the account); a
    /// right one resets the count. Returns the user as read here; the new hash is made from it.
    /// </summary>
    private async Task<AppUser> CheckCurrentPasswordAsync(string userId, string tokenStamp, string currentPassword)
    {
        // Read inside the lock, so the failure count is the one the previous attempt wrote. Tracked, because Identity's
        // counting methods save their changes to this object.
        var user = await userManager.FindByIdAsync(userId) ?? throw NotAuthenticated();

        // The token check at the start of this request found the user active with the token's stamp, but a Manager
        // may have deactivated them or reset the password in the milliseconds since. That window is closed here; the
        // same check in the transaction closes the next one.
        ThrowIfSessionEnded(user, tokenStamp);

        // AC-01.4: the lock check comes before the password check. Otherwise a locked account could still be used to
        // test guesses, and the lock-out would protect nothing. AppUserManager reads the time from Clock (decision #50).
        if (await userManager.IsLockedOutAsync(user))
        {
            throw new AuthenticationFailedException(AccountLockedCode, SignInService.AccountLockedMessage);
        }

        // AC-04.2: proving the current password first means a borrowed, unattended session cannot be used to take
        // the account over. This is the slow hash check, outside the write transaction (decision #70).
        if (!IsCurrentPassword(user, currentPassword))
        {
            // AccessFailedAsync adds one to the count and, at the fifth failure, sets the 15-minute LockoutEnd. It saves
            // the row by itself, outside any write transaction, exactly as sign-in does (architecture 5.2).
            WarnIfFailed(await userManager.AccessFailedAsync(user), "record a wrong current password", user.Id);
            throw FieldError("currentPassword", CurrentPasswordIncorrectMessage, CurrentPasswordIncorrectCode);
        }

        // Like a successful sign-in: the failures so far no longer count towards a lock ("5 in a row"), even if the
        // new password then turns out to be too weak.
        WarnIfFailed(await userManager.ResetAccessFailedCountAsync(user), "reset the failed password count", user.Id);
        return user;
    }

    /// <summary>
    /// <c>PUT /users/{id}/password</c> (AC-03.7): a Manager sets a new password for a user, for example one who forgot
    /// theirs. The policy applies; the old password is not needed. That user's existing tokens stop working, and any
    /// lock-out ends (decision #72).
    /// </summary>
    /// <param name="id">The user whose password is set.</param>
    /// <param name="request">The new password.</param>
    /// <param name="cancellationToken">Cancels the reads, the save and the commit.</param>
    public async Task SetPasswordAsync(string id, SetPasswordRequest request, CancellationToken cancellationToken)
    {
        // Design 2.2 order: the record in the URL (404) before the input (400).
        var snapshot = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id, cancellationToken)
            ?? throw new NotFoundException(UserService.UserNotFoundMessage);

        if (string.IsNullOrEmpty(request.NewPassword))
        {
            throw new ValidationException("newPassword", NewPasswordRequiredMessage);
        }

        await ThrowIfWeakAsync(userManager, snapshot, request.NewPassword, "newPassword");

        // Decision #70: the slow hash is made before the transaction. No check depends on the old password, so unlike
        // change-password there is nothing to re-check: if the user changed their own password meanwhile, the
        // Manager's reset, which comes later, simply replaces it.
        var newHash = userManager.PasswordHasher.HashPassword(snapshot, request.NewPassword);

        await db.InWriteTransactionAsync(async () =>
        {
            // Users are never deleted (AC-03.6), so the user found above is still there.
            var user = await userManager.FindByIdAsync(id) ?? throw new NotFoundException(UserService.UserNotFoundMessage);

            // Decision #72: no lock-out end time and no failures counted, so the user can sign in at once with the new
            // password. These are plain fields on the user row; SaveNewHashAsync saves them in the same write as the
            // new hash, so the reset and the unlock happen together or not at all.
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            await SaveNewHashAsync(user, newHash);
            logger.LogInformation("Password of user {UserId} set by a Manager.", user.Id);
        }, cancellationToken);
    }

    /// <summary>
    /// PRD section 5 password policy (the options in <see cref="AuthSetup"/>): throws <c>400</c> <c>weak-password</c>
    /// naming <paramref name="path"/> when <paramref name="password"/> fails it. Runs Identity's own password
    /// validators, the same ones Identity's <c>CreateAsync</c> and <c>AddPasswordAsync</c> use, so the rule lives in
    /// one place. They only look at the text, so they are quick and need no transaction.
    /// </summary>
    public static async Task ThrowIfWeakAsync(UserManager<AppUser> userManager, AppUser user, string password, string path)
    {
        foreach (var validator in userManager.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(userManager, user, password)).Succeeded)
            {
                throw FieldError(path, WeakPasswordMessage, WeakPasswordCode);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="password"/> matches the user's stored hash. Calls the hasher directly instead of
    /// <c>UserManager.CheckPasswordAsync</c>, because that method may also save an upgraded hash on its own, outside
    /// the write transaction. Here the hash is replaced straight afterwards anyway.
    /// </summary>
    private bool IsCurrentPassword(AppUser user, string password) =>
        user.PasswordHash is not null &&
        userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password)
            is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;

    /// <summary>
    /// Stores a hash made earlier (outside the transaction) on the tracked <paramref name="user"/>, and gives the user a
    /// new security stamp so their existing tokens stop working (class remarks). <c>UpdateSecurityStampAsync</c> saves
    /// the whole row, so the hash and the stamp are saved in one write, inside the caller's transaction. This is what
    /// Identity's own <c>AddPasswordAsync</c> does, minus the hashing.
    /// </summary>
    private async Task SaveNewHashAsync(AppUser user, string newHash)
    {
        user.PasswordHash = newHash;
        UserService.ThrowIfFailed(await userManager.UpdateSecurityStampAsync(user), "save the new password");
    }

    /// <summary>
    /// Identity reports problems as a result instead of throwing. With the account lock, counting should never fail;
    /// if it does (for example a Manager changed the user at the same moment), the answer to the caller is still
    /// right, but the count may be off by one, so it is logged at Warning for someone to look at, as in sign-in.
    /// </summary>
    private void WarnIfFailed(IdentityResult result, string action, string userId)
    {
        if (!result.Succeeded)
        {
            logger.LogWarning("Could not {Action} for user {UserId}: {Errors}", action, userId,
                string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }

    /// <summary>
    /// The same rule as the per-request token check (<see cref="TokenUserCheck"/>, architecture 4.1 item 6), applied
    /// again later in the request: the user must still be active and still have the security stamp the token was
    /// issued with. Otherwise <c>401</c> <c>not-authenticated</c>.
    /// </summary>
    private static void ThrowIfSessionEnded(AppUser user, string tokenStamp)
    {
        if (!user.IsActive || user.SecurityStamp != tokenStamp)
        {
            throw NotAuthenticated();
        }
    }

    /// <summary>
    /// Design 7.1 <c>401</c> <c>not-authenticated</c>: the same answer the token check gives a deactivated user's token.
    /// </summary>
    private static AuthenticationFailedException NotAuthenticated() =>
        new(UnauthorizedResponse.NotAuthenticatedCode, UnauthorizedResponse.NotAuthenticatedMessage);

    /// <summary>A <c>400</c> with its own code that names the one field at fault (design 6).</summary>
    private static ValidationException FieldError(string path, string message, string code) =>
        new(message, new Dictionary<string, string[]> { [path] = [message] }, code);
}
