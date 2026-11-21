using InventoryTracker.Api.Common.Validation;
using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// PRD section 5 and decision #118: a password must be at least 8 characters, counted as Unicode code points like every
/// other text limit (decision #116, <see cref="Text.Length"/>). Without this, "Aa1🧀🧀🧀" (6 characters) would pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a password validator is.</b> ASP.NET Core Identity checks a new password by running every registered
/// <see cref="IPasswordValidator{TUser}"/> in turn; the password is accepted only if all of them succeed. Identity
/// registers its own <c>PasswordValidator</c>, which applies the options set in <see cref="AuthSetup"/> (length, digit,
/// upper-case, lower-case). <see cref="AuthSetup"/> adds this class next to it with <c>AddPasswordValidator</c>.
/// Every place that checks a password goes through <c>UserManager.PasswordValidators</c> (creating a user, changing
/// one's own password, a Manager's reset, and the first Manager from <c>ADMIN_PASSWORD</c> at start-up), so this rule
/// applies to all of them with no change to those callers.
/// </para>
/// <para>
/// <b>Why add a validator instead of replacing Identity's.</b> Identity's length rule counts UTF-16 code units
/// (<see cref="string.Length"/>), where an emoji counts as two. A count of code points is never larger than the count of
/// code units, so any password that passes this check also passes Identity's. Keeping Identity's validator means its
/// other rules (digit, upper-case, lower-case) keep working unchanged; this class only closes the gap in the length.
/// </para>
/// <para>
/// The minimum is read from <c>IdentityOptions.Password.RequiredLength</c> (8, set in <see cref="AuthSetup"/>), so the
/// number lives in one place. A failure returns exactly the error Identity's own length rule gives
/// (<c>PasswordTooShort</c>, "Passwords must be at least 8 characters."); the API answers it with the design 7.1
/// <c>weak-password</c> message, as it does for every policy failure.
/// </para>
/// </remarks>
public sealed class PasswordLengthValidator : IPasswordValidator<AppUser>
{
    /// <summary>
    /// Succeeds when <paramref name="password"/> has at least <c>RequiredLength</c> code points; otherwise fails with
    /// Identity's <c>PasswordTooShort</c> error.
    /// </summary>
    /// <remarks>
    /// The interface method returns a <see cref="Task"/> because some validators need to look things up (a list of
    /// breached passwords, say). This one only counts, so it returns a finished task with
    /// <see cref="Task.FromResult{TResult}"/> rather than being <c>async</c>.
    /// </remarks>
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        ArgumentNullException.ThrowIfNull(manager);
        // Identity's own validator throws for a null password too; callers report a missing password before this runs.
        ArgumentNullException.ThrowIfNull(password);

        var requiredLength = manager.Options.Password.RequiredLength;

        // Decision #118 (PRD 5): at least 8 characters, counted in code points, so an emoji is one character.
        var result = Text.Length(password) < requiredLength
            ? IdentityResult.Failed(manager.ErrorDescriber.PasswordTooShort(requiredLength))
            : IdentityResult.Success;
        return Task.FromResult(result);
    }
}
