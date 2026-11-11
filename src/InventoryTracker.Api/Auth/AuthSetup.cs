using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Registers everything that answers "who is calling?" and "may they do this?": Identity for users and passwords,
/// JwtBearer for tokens, and authorization with a fallback "signed-in" policy (architecture 6, decision #8).
/// Called once from Program.cs; the middleware side is <c>app.UseAuthentication()</c> and
/// <c>app.UseAuthorization()</c>, also in Program.cs.
/// </summary>
/// <remarks>
/// How one request is checked, in pipeline order:
/// <list type="number">
/// <item><b>Authentication middleware</b> (<c>UseAuthentication</c>): JwtBearer reads the
/// <c>Authorization: Bearer ...</c> header, checks the token's signature, issuer, audience and expiry, then runs
/// <see cref="TokenUserCheck"/>. If all pass, it sets <c>HttpContext.User</c> to a <c>ClaimsPrincipal</c> built from
/// the token's claims (id, email, roles). If there is no token or it fails, the request simply continues as
/// anonymous; nothing is refused yet.</item>
/// <item><b>Authorization middleware</b> (<c>UseAuthorization</c>): looks at the endpoint the request is going to,
/// and its <c>[Authorize]</c> / <c>[AllowAnonymous]</c> attributes. Anonymous request to a protected endpoint:
/// <c>401</c>. Signed in but without a required role: <c>403</c> (<see cref="ForbiddenResponseHandler"/>).
/// Otherwise the controller runs.</item>
/// </list>
/// </remarks>
public static class AuthSetup
{
    /// <summary>Adds Identity, JWT bearer authentication, authorization and the sign-in services.</summary>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Token settings: the Jwt section of appsettings.json plus the secret JWT_SIGNING_KEY (decision #32).
        // The lambda runs when the options are first used, so it sees the final configuration, including
        // environment variables and the values tests set.
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Configure(options => options.SigningKey = configuration[JwtOptions.SigningKeyVariable] ?? "");

        AddIdentity(services);
        AddJwtBearer(services);
        AddAuthorizationRules(services);

        // Singleton: the token service holds no per-request state. SignInService uses UserManager, which uses the
        // request's DbContext, so it is scoped (one per request) like them.
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<SignInService>();

        // Singleton on purpose: the per-account sign-in locks must be shared by every request (decision #61).
        services.AddSingleton<AccountLocks>();
        return services;
    }

    /// <summary>
    /// ASP.NET Core Identity, the API-only way: users, roles, password hashing and lock-out, with no cookies and no
    /// sign-in pages (<c>AddIdentityCore</c> instead of <c>AddIdentity</c>).
    /// </summary>
    private static void AddIdentity(IServiceCollection services)
    {
        services.AddIdentityCore<AppUser>(options =>
            {
                // PRD section 5 password policy: at least 8 characters with an upper-case letter, a lower-case letter
                // and a digit. Symbols are allowed but not required.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                // AC-01.4: 5 failed sign-ins in a row lock the account for 15 minutes. AllowedForNewUsers turns
                // lock-out on for every account as it is created.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                // AC-03.2: one account per email, ignoring letter case (Identity compares NormalizedEmail).
                options.User.RequireUniqueEmail = true;

                // The user name is always a copy of the email, which Identity already checks with its email
                // validator. Identity's default user-name character list would refuse valid emails such as
                // o'brien@example.com, so it is switched off (an empty list means "any character").
                options.User.AllowedUserNameCharacters = "";
            })
            // Roles live in AspNetRoles / AspNetUserRoles; a user can hold several (decision #8).
            .AddRoles<IdentityRole>()
            // Identity's EF Core stores: users and roles are read and written through AppDbContext.
            .AddEntityFrameworkStores<AppDbContext>()
            // UserManager<AppUser> is our subclass, so lock-out follows the app's clock (see AppUserManager).
            .AddUserManager<AppUserManager>();
    }

    /// <summary>
    /// JWT bearer authentication: how a token on a request is checked (architecture 6, Tokens).
    /// </summary>
    private static void AddJwtBearer(IServiceCollection services)
    {
        // "Bearer" becomes the default scheme, so UseAuthentication runs JwtBearer on every request.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // The options are filled in here rather than inside AddJwtBearer(...) so they can use other services: the
        // token settings and the app's TimeProvider. That provider is the real clock in production and a fake one in
        // tests, which is how the tests check that a token stops working after 8 hours (AC-01.2).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwtOptions, timeProvider) =>
            {
                var jwt = jwtOptions.Value;

                // Keep claim names exactly as in the token ("sub", "email", "role"). Without this, the handler renames
                // them to long .NET URIs such as http://schemas.microsoft.com/ws/2008/06/identity/claims/role.
                bearer.MapInboundClaims = false;
                bearer.TimeProvider = timeProvider;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    // Signature: the token must be signed with our key, by HS256 and nothing else. A token signed with
                    // another key, or a forged unsigned token (alg "none"), is refused (AC-01.5).
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = JwtTokenService.CreateSigningKey(jwt.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

                    // Issued by this API, for this API.
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,

                    // Architecture 4.1 item 6 and AC-01.2: a token lives exactly 8 hours by the app's clock, with no
                    // grace period, and a token without an expiry time is refused. The library's own lifetime check
                    // reads the computer's clock, so LifetimeValidator replaces it with IsWithinLifetime, which reads
                    // the app's TimeProvider. Once a LifetimeValidator is set, the library calls only that delegate:
                    // ClockSkew, ValidateLifetime and RequireExpirationTime below are then ignored, and the delegate
                    // alone enforces "no grace period" and "expiry required". They are still set to the strict values
                    // as a backstop, so that removing the delegate later cannot quietly bring back the library's
                    // default 5-minute ClockSkew (a token living 8 h 5 min).
                    LifetimeValidator = (notBefore, expires, _, _) => IsWithinLifetime(notBefore, expires, timeProvider),
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.Zero,

                    // Which claims mean "name" and "role": User.Identity.Name is the email, and
                    // [Authorize(Roles = ...)] / User.IsInRole look at the "role" claims.
                    NameClaimType = JwtTokenService.EmailClaim,
                    RoleClaimType = JwtTokenService.RoleClaim,
                };

                bearer.Events = new JwtBearerEvents
                {
                    // After the token itself checks out: is the user still active, with the same security stamp?
                    OnTokenValidated = TokenUserCheck.ValidateAsync,
                    // When a protected endpoint gets no valid token: the design 7.1 401 body.
                    OnChallenge = UnauthorizedResponse.WriteChallengeAsync,
                };
            });
    }

    /// <summary>
    /// AC-01.2: a token works from its "not before" time until, but not including, its expiry time, by the app's
    /// clock. A token without an expiry is never accepted.
    /// </summary>
    private static bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return expires is not null && now < expires.Value && (notBefore is null || now >= notBefore.Value);
    }

    /// <summary>The authorization rules: every endpoint needs a signed-in user unless it says otherwise.</summary>
    private static void AddAuthorizationRules(IServiceCollection services)
    {
        // A policy is a set of requirements a request must meet. The fallback policy is used only where there are no
        // authorization rules at all: an endpoint with no [Authorize] or [AllowAnonymous], and a request that matches
        // no endpoint. Making it "a signed-in user is required" means a new endpoint is protected even if someone
        // forgets the attribute (AC-01.5, PRD section 5).
        //
        // On an endpoint with [Authorize(Roles = ...)] the fallback is not used: that endpoint's own rule, "holds one of
        // these roles", replaces it (AC-05.1). An anonymous caller still gets 401 there, not 403, for a different
        // reason: when a rule fails and nobody is signed in, the framework "challenges" (asks the caller to sign in,
        // 401) instead of "forbidding" (403), because the problem may only be the missing sign-in (design 2.2: 401
        // first). Only sign-in, /health and the Development-only OpenAPI document opt out with [AllowAnonymous]
        // (decisions #28, #33).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Replaces the framework's empty 403 with the design 7.1 body naming the roles.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ForbiddenResponseHandler>();
    }
}
