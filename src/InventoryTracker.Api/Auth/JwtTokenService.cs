using System.Security.Claims;
using System.Text;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Time;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Issues the bearer token a user receives when they sign in (AC-01.1, architecture 6, Tokens).
/// </summary>
/// <remarks>
/// <para>
/// A JWT (JSON Web Token) is three Base64 parts joined by dots: a header (<c>{"alg":"HS256"}</c>), a payload of
/// "claims" (facts about the user, such as their id, email and roles, plus the expiry time), and a signature. The
/// client sends it back on every request in the header <c>Authorization: Bearer &lt;token&gt;</c>.
/// </para>
/// <para>
/// The payload is only encoded, not encrypted: anyone holding the token can read it, so it never contains a
/// password. What makes it trustworthy is the signature. HS256 means HMAC-SHA256: a hash of the header and payload
/// mixed with the secret <c>JWT_SIGNING_KEY</c>. Only someone who knows the key can produce a matching signature,
/// so if a client changes one character of the payload (say, adds "Manager" to its roles), the signature no longer
/// matches and the JwtBearer middleware refuses the token. The same key signs here and checks in
/// <see cref="AuthSetup"/>; that is why it must stay secret and be long enough to resist guessing (32+ characters).
/// </para>
/// </remarks>
public sealed class JwtTokenService(IOptions<JwtOptions> options, Clock clock)
{
    /// <summary>Claim holding the user id ("subject"), a standard JWT claim name.</summary>
    public const string UserIdClaim = JwtRegisteredClaimNames.Sub;

    /// <summary>Claim holding the user's email; also the user's display name in the app (<c>User.Identity.Name</c>).</summary>
    public const string EmailClaim = JwtRegisteredClaimNames.Email;

    /// <summary>Claim type for roles: one claim per role the user holds at sign-in.</summary>
    public const string RoleClaim = "role";

    /// <summary>
    /// Architecture 4.1 item 6: the user's security stamp at sign-in. Identity changes the stamp when a password
    /// changes or the user is deactivated; a token whose stamp no longer matches is refused on the next request.
    /// </summary>
    public const string SecurityStampClaim = "stamp";

    /// <summary>
    /// Turns the secret text into the key object the signing and validation code needs. The key is the UTF-8 bytes
    /// of the configured text, used as it is (a Base64 key is not decoded first).
    /// </summary>
    public static SymmetricSecurityKey CreateSigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    /// <summary>Issues a signed token for <paramref name="user"/> holding <paramref name="roles"/>.</summary>
    /// <returns>The token text and the moment it expires (UTC, whole seconds).</returns>
    public (string AccessToken, DateTime ExpiresAt) CreateToken(AppUser user, IReadOnlyList<string> roles)
    {
        var settings = options.Value;
        var now = clock.UtcNow;

        // AC-01.2: exactly 8 hours (Jwt:TokenLifetimeHours); there is no refresh token.
        var expiresAt = now.AddHours(settings.TokenLifetimeHours);

        var claims = new List<Claim>
        {
            new(UserIdClaim, user.Id),
            new(EmailClaim, user.Email ?? ""),
            new(SecurityStampClaim, user.SecurityStamp ?? ""),
            // "jti" (JWT id) makes every token unique, even two issued in the same second for the same user.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(role => new Claim(RoleClaim, role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            // All three times come from the app's clock, so a test that moves the clock moves the token's lifetime too.
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(CreateSigningKey(settings.SigningKey), SecurityAlgorithms.HmacSha256),
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
