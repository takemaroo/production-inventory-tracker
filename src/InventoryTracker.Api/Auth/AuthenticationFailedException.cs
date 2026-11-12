using InventoryTracker.Api.Common.Errors;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// <c>401</c> "Not signed in", thrown by a service: a sign-in was refused (design 7.1, <c>invalid-credentials</c> or
/// <c>account-locked</c>), or a change-password was refused because the account is locked (<c>account-locked</c>,
/// decision #71) or the caller's token stopped being valid during the request (<c>not-authenticated</c>).
/// </summary>
/// <remarks>
/// A <c>401</c> for a missing or bad token is not thrown: the JwtBearer middleware produces it before any controller
/// runs (see <see cref="UnauthorizedResponse"/>). The <c>WWW-Authenticate: Bearer</c> header that every <c>401</c>
/// carries is added by <see cref="UnauthorizedResponse.UseBearerChallengeHeader"/>.
/// </remarks>
public sealed class AuthenticationFailedException : ApiException
{
    /// <summary>Creates the exception with the design 7.1 code and message.</summary>
    public AuthenticationFailedException(string code, string detail)
        : base(StatusCodes.Status401Unauthorized, code, detail)
    {
    }
}
