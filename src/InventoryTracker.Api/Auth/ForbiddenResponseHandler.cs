using InventoryTracker.Api.Common.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Writes the <c>403</c> ProblemDetails body that names the roles an action needs (design 7.1, AC-05.1,
/// architecture 4.1 item 7).
/// </summary>
/// <remarks>
/// <para>
/// The authorization middleware (<c>app.UseAuthorization()</c>) runs after authentication has worked out who the
/// user is. It collects the endpoint's rules (the fallback "signed-in" policy, or the roles in
/// <c>[Authorize(Roles = ...)]</c>) into a <em>policy</em> and evaluates it. The result is one of three: allowed
/// (the request goes on to the controller), <em>challenged</em> (nobody is signed in: <c>401</c>) or
/// <em>forbidden</em> (signed in, but the rules say no: <c>403</c>). This class is ASP.NET Core's hook for turning
/// that result into a response.
/// </para>
/// <para>
/// The framework's default for "forbidden" is an empty <c>403</c>. Here it is replaced by the design's body. The
/// roles come from the failed <see cref="RolesAuthorizationRequirement"/>, which is what
/// <c>[Authorize(Roles = Roles.WarehouseOrManager)]</c> turns into. Every other result is passed to the
/// framework's default handler; for "challenged" that calls JwtBearer, which answers with the <c>401</c> body
/// (<see cref="UnauthorizedResponse.WriteChallengeAsync"/>). Design 2.2: the <c>401</c> check comes first, because
/// a request with no valid user is challenged, never forbidden.
/// </para>
/// </remarks>
public sealed class ForbiddenResponseHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>Design 7.1: the code for a signed-in user whose roles do not allow the action.</summary>
    public const string ForbiddenCode = "forbidden";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <summary>Writes the <c>403</c> body for a forbidden result; leaves every other result to the framework.</summary>
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var allowedRoles = authorizeResult.AuthorizationFailure?.FailedRequirements
            .OfType<RolesAuthorizationRequirement>()
            .SelectMany(requirement => requirement.AllowedRoles)
            .Distinct()
            .ToList() ?? [];

        await ProblemResponse.WriteAsync(context,
            ProblemResponse.Create(StatusCodes.Status403Forbidden, ForbiddenCode, Message(allowedRoles)));
    }

    /// <summary>
    /// Design 7.1: "Your roles do not allow this action. It needs the Manager role." With two roles:
    /// "It needs the Warehouse or Manager role."
    /// </summary>
    public static string Message(IReadOnlyList<string> allowedRoles)
    {
        const string start = "Your roles do not allow this action.";
        return allowedRoles.Count switch
        {
            // Only a non-role rule could fail without naming roles; no endpoint uses one in v1.
            0 => start,
            1 => $"{start} It needs the {allowedRoles[0]} role.",
            _ => $"{start} It needs the {string.Join(", ", allowedRoles.Take(allowedRoles.Count - 1))} or {allowedRoles[^1]} role.",
        };
    }
}
