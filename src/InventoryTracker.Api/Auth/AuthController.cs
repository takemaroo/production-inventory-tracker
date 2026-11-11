using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Sign-in (US-01). <c>POST /api/v1/auth/change-password</c> is added here by T-014.
/// </summary>
/// <remarks>
/// <c>[ApiController]</c> turns on MVC's API conventions: the body is read from JSON automatically, and a body that
/// cannot be read is refused with the design's <c>400</c> before the action runs (architecture 4.1 item 2).
/// <c>[Route]</c> sets the URL prefix for every action in the class.
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(SignInService signIn) : ControllerBase
{
    /// <summary>
    /// Signs in with email and password and returns a bearer token valid for 8 hours (AC-01.1, AC-01.2).
    /// Wrong credentials give <c>401</c> <c>invalid-credentials</c>; five failures in a row lock the account for
    /// 15 minutes (<c>401</c> <c>account-locked</c>, AC-01.3, AC-01.4).
    /// </summary>
    /// <remarks>
    /// <c>[AllowAnonymous]</c> lifts the fallback "signed-in" policy (see <see cref="AuthSetup"/>) for this one
    /// action: it is the only API endpoint that works without a token (AC-01.5, architecture 6, Authorization).
    /// </remarks>
    [HttpPost("sign-in")]
    [AllowAnonymous]
    public Task<SignInResponse> SignIn(SignInRequest request) => signIn.SignInAsync(request);
}
