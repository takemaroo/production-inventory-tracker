using InventoryTracker.Api.Common.Validation;
using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Checks an email and password and, when they are right, issues a token (US-01, architecture 4.1 item 5).
/// </summary>
/// <remarks>
/// <para>
/// Passwords are never stored. When a user is created, Identity's password hasher turns the password into a
/// <em>salted hash</em>: a random "salt" is mixed with the password and run through PBKDF2 (HMAC-SHA512, 100,000
/// rounds), and only the salt and the result are kept in <c>AspNetUsers.PasswordHash</c>. A hash cannot be turned
/// back into the password; the salt means two users with the same password get different hashes; the many rounds
/// make guessing slow. <c>CheckPasswordAsync</c> repeats the same calculation on the typed password and compares
/// the results (PRD section 5: "Passwords are stored only as salted hashes").
/// </para>
/// <para>
/// Concurrency (AC-01.4, decision #61): Identity updates the failure count by reading the row, changing it and
/// writing it back, and refuses a write based on an out-of-date copy. On its own that is not enough: simultaneous
/// wrong passwords would all read the same count, and all but one update would be refused, so the account would never
/// lock. So every attempt for one account runs inside that account's lock (<see cref="AccountLocks"/>), and each one
/// reads the count the previous one wrote. Sign-in does not use <c>InWriteTransactionAsync</c>: that takes the
/// database's single write lock, which would make every sign-in in the company wait for every other one during the
/// slow password check.
/// </para>
/// </remarks>
public sealed class SignInService(
    UserManager<AppUser> userManager, AccountLocks accountLocks, JwtTokenService tokens, ILogger<SignInService> logger)
{
    /// <summary>Design 7.1 and AC-01.3: the one message for wrong password, unknown email and deactivated user.</summary>
    public const string InvalidCredentialsMessage = "Invalid email or password.";

    /// <summary>Design 7.1 and AC-01.4.</summary>
    public const string AccountLockedMessage = "Account temporarily locked. Try again later.";

    /// <summary>Design 5.1: the only token type this API issues.</summary>
    public const string TokenType = "Bearer";

    /// <summary>
    /// Decision #61: a real hash of a random password, made once when the app first signs someone in. Checking a typed
    /// password against it costs the same time as checking a real one, and it never matches.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));

    /// <summary>Signs the user in, or throws the design 7.1 <c>401</c> (or a <c>400</c> when a field is missing).</summary>
    public async Task<SignInResponse> SignInAsync(SignInRequest request)
    {
        // Design 2.2 step 4: the input shape first. Design 2.6: an email of only spaces counts as missing. The
        // password is not trimmed, because spaces may be part of it.
        var email = Text.Clean(request.Email);
        var errors = new ValidationErrors();
        if (email is null)
        {
            errors.Add("email", "Email is required.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors.Add("password", "Password is required.");
        }

        errors.ThrowIfAny();

        // Decision #61: one attempt at a time for this account. The key is the email as Identity normalizes it, so
        // "Ana@example.com" and "ana@EXAMPLE.COM" share a lock. The `using` block releases the lock as soon as the
        // outcome is known, even if something throws.
        Outcome outcome;
        using (await accountLocks.AcquireAsync(userManager.NormalizeEmail(email!)))
        {
            outcome = await DecideAsync(email!, request.Password!);
        }

        // Decision #62: the dummy password check runs only now, after the lock is released. It changes nothing in the
        // database, so it does not need the lock, and running it inside would make a flood of attempts on one account
        // queue behind about 45 ms of hashing each.
        if (outcome.Refusal is not null)
        {
            if (outcome.SpendPasswordCheckTime)
            {
                SpendPasswordCheckTime(request.Password!);
            }

            throw outcome.Refusal;
        }

        return outcome.Response!;
    }

    /// <summary>
    /// Architecture 4.1 item 5, in this exact order: works out whether the sign-in succeeds and, if not, why. Runs while
    /// holding the account's lock, because it reads and updates the failure count.
    /// </summary>
    private async Task<Outcome> DecideAsync(string email, string password)
    {
        // 1. Unknown email or deactivated user (AC-01.3, AC-03.6): the same message as a wrong password, so the
        //    response never reveals which emails have accounts. No failure is counted: there may be no account.
        //    FindByEmailAsync compares Identity's upper-cased NormalizedEmail, so letter case does not matter (design 5.1).
        //    Decision #61: no password is checked on this path, so without a dummy check this refusal would come back
        //    in about 3 ms and a wrong password for a real account in about 45 ms (the check is slow on purpose),
        //    which would tell a caller which emails exist. SignInAsync runs the dummy check after releasing the lock.
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || !user.IsActive)
        {
            return Outcome.Refused(InvalidCredentials(), spendPasswordCheckTime: true);
        }

        // 2. AC-01.4: while the account is locked, even the right password is refused, and nothing is counted.
        //    Decision #62: this refusal gets the dummy check too, so that every refusal costs one password check and
        //    all refusals take about the same time; there is then less to reason about. (It is not about hiding the
        //    account: the account-locked message already shows that it exists.)
        if (await userManager.IsLockedOutAsync(user))
        {
            return Outcome.Refused(new AuthenticationFailedException("account-locked", AccountLockedMessage),
                spendPasswordCheckTime: true);
        }

        // 3. Wrong password: count the failure. The fifth in a row starts the 15-minute lock (AppUserManager), but
        //    this response still says "Invalid email or password." (architecture 4.1 item 5). This real password
        //    check must run inside the lock, because the failure count depends on its result. It is this refusal's
        //    one password check, so no dummy check follows.
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            WarnIfFailed(await userManager.AccessFailedAsync(user), "record a failed sign-in", user.Id);
            return Outcome.Refused(InvalidCredentials(), spendPasswordCheckTime: false);
        }

        // 4. Right password: the failures so far no longer count towards a lock ("5 failed sign-ins in a row").
        WarnIfFailed(await userManager.ResetAccessFailedCountAsync(user), "reset the failed sign-in count", user.Id);

        // AC-01.1: the token carries the roles as they are now; a later role change applies from the next sign-in
        // (AC-03.5, PRD assumption 15).
        var roles = Roles.InStandardOrder(await userManager.GetRolesAsync(user));
        var (accessToken, expiresAt) = tokens.CreateToken(user, roles);

        // Architecture 6, Logging: the user id only, never the password or the token.
        logger.LogInformation("User {UserId} signed in.", user.Id);
        return new Outcome(new SignInResponse(accessToken, TokenType, expiresAt, user.Email!, roles), null, false);
    }

    /// <summary>
    /// Runs a real password check against <see cref="DummyPasswordHash"/>, only to take as long as checking a real
    /// account's password. The result is thrown away: it never matches.
    /// </summary>
    private void SpendPasswordCheckTime(string password) =>
        userManager.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, password);

    /// <summary>
    /// Identity reports problems as a result instead of throwing. With the account lock this should never fail; if it
    /// does (for example another part of the app changed the user at the same moment), the sign-in answer is still
    /// right, but the counter may be off by one, so it is logged for someone to look at.
    /// </summary>
    private void WarnIfFailed(IdentityResult result, string action, string userId)
    {
        if (!result.Succeeded)
        {
            logger.LogWarning("Could not {Action} for user {UserId}: {Errors}", action, userId,
                string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }

    private static AuthenticationFailedException InvalidCredentials() =>
        new("invalid-credentials", InvalidCredentialsMessage);

    /// <summary>
    /// What <see cref="DecideAsync"/> found: either the response for a successful sign-in, or the refusal to throw and
    /// whether a dummy password check is still owed before throwing it.
    /// </summary>
    private sealed record Outcome(SignInResponse? Response, AuthenticationFailedException? Refusal, bool SpendPasswordCheckTime)
    {
        public static Outcome Refused(AuthenticationFailedException refusal, bool spendPasswordCheckTime) =>
            new(null, refusal, spendPasswordCheckTime);
    }
}
