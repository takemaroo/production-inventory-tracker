using System.Security.Claims;
using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Users;

/// <summary>
/// The signed-in user's own account: <c>POST /api/v1/auth/change-password</c> (US-04, design 5.1). Sign-in itself is
/// in <see cref="AuthController"/>; both share the <c>/api/v1/auth</c> prefix.
/// </summary>
/// <remarks>
/// There is no <c>[Authorize(Roles = ...)]</c> here on purpose: every role may change its own password (PRD 4.2). The
/// fallback policy (see <see cref="AuthSetup"/>) still requires a signed-in user, so a call without a valid token
/// gets <c>401</c>.
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public sealed class AccountController(PasswordService passwords) : ControllerBase
{
    /// <summary>
    /// Changes the caller's own password (AC-04.1): <c>204</c>. A wrong current password is <c>400</c>
    /// <c>current-password-incorrect</c> (AC-04.2); a new password that fails the policy is <c>400</c>
    /// <c>weak-password</c>. Afterwards the caller's tokens no longer work, so they sign in again with the new password.
    /// Wrong current passwords count towards the sign-in lock-out; while the account is locked the answer is <c>401</c>
    /// <c>account-locked</c>, as for sign-in (decision #71, AC-01.4).
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        // The user id comes from the token's "sub" claim, never from the body, so a user can only change their own
        // password. User is the ClaimsPrincipal the authentication middleware built from the checked token.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;

        // The stamp the token was issued with: the service checks it again after its slow password work, so a token
        // ended meanwhile (deactivation, reset) cannot still change the password. TokenUserCheck ensures it is present.
        var tokenStamp = User.FindFirstValue(JwtTokenService.SecurityStampClaim)!;
        await passwords.ChangeOwnPasswordAsync(userId, tokenStamp, request, cancellationToken);
        return NoContent();
    }
}
