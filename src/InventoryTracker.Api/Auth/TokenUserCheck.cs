using System.Security.Claims;
using InventoryTracker.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// The per-request user check (architecture 4.1 item 6): a token is accepted only while its user exists, is active,
/// and still has the security stamp the token was issued with.
/// </summary>
/// <remarks>
/// A signature check alone cannot notice that a user was deactivated after signing in: the token would stay valid
/// for the rest of its 8 hours. So after JwtBearer has checked the signature, issuer, audience and expiry, its
/// <c>OnTokenValidated</c> event calls <see cref="ValidateAsync"/>, which reads the user's current row. Deactivating
/// a user (AC-03.6) or changing their password changes the stamp, so their old tokens fail from the next request on.
/// The cost is one small primary-key query per request.
/// </remarks>
public static class TokenUserCheck
{
    /// <summary>Fails the token when its user is missing or inactive, or the stamp differs.</summary>
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userId = principal?.FindFirstValue(JwtTokenService.UserIdClaim);
        var stamp = principal?.FindFirstValue(JwtTokenService.SecurityStampClaim);
        if (userId is null || stamp is null)
        {
            context.Fail("The token has no user id or security stamp.");
            return;
        }

        // RequestServices is the DI scope of this request, so this is the request's own DbContext.
        // AsNoTracking: the row is only read, so EF Core need not keep a copy to detect changes.
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.IsActive, candidate.SecurityStamp })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null || !user.IsActive || user.SecurityStamp != stamp)
        {
            // Fail() turns the request into an anonymous one, so an endpoint that needs a user answers with the
            // not-authenticated 401 (UnauthorizedResponse.WriteChallengeAsync).
            context.Fail("The token's user is unknown or inactive, or the security stamp has changed.");
        }
    }
}
