using InventoryTracker.Api.Common.Errors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Net.Http.Headers;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// The <c>401</c> responses: the ProblemDetails body when a token is missing or not accepted, and the
/// <c>WWW-Authenticate: Bearer</c> header on every <c>401</c> (design 7.1, architecture 4.1 item 7).
/// </summary>
public static class UnauthorizedResponse
{
    /// <summary>Design 7.1: the code for no token, a bad or expired token, or the token of a deactivated user.</summary>
    public const string NotAuthenticatedCode = "not-authenticated";

    /// <summary>Design 7.1: the message for <see cref="NotAuthenticatedCode"/>.</summary>
    public const string NotAuthenticatedMessage = "Your sign-in is missing or has expired. Sign in again.";

    /// <summary>
    /// JwtBearer's <c>OnChallenge</c> event: writes the design 7.1 body instead of the framework's empty <c>401</c>.
    /// </summary>
    /// <remarks>
    /// A "challenge" is what ASP.NET Core does when an endpoint needs a signed-in user and the request has none: it
    /// asks the authentication handler (here JwtBearer) to tell the client how to sign in. JwtBearer's own answer is
    /// an empty <c>401</c> whose <c>WWW-Authenticate</c> header can describe why the token failed
    /// (<c>error_description="The token expired at ..."</c>). <c>HandleResponse()</c> tells JwtBearer that this code
    /// has written the response itself, so its default is skipped. Every case gets the same message on purpose:
    /// the client's next step is always "sign in again", and the reason a token failed is not the client's business.
    /// </remarks>
    public static Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        return ProblemResponse.WriteAsync(context.HttpContext,
            ProblemResponse.Create(StatusCodes.Status401Unauthorized, NotAuthenticatedCode, NotAuthenticatedMessage));
    }

    /// <summary>
    /// Adds a middleware step that puts <c>WWW-Authenticate: Bearer</c> on every <c>401</c> response, whoever wrote it:
    /// the JwtBearer challenge above, or a refused sign-in thrown as <see cref="AuthenticationFailedException"/>.
    /// </summary>
    /// <remarks>
    /// HTTP requires a <c>401</c> to name the sign-in scheme in this header. A step registered here, near the start of
    /// the pipeline, cannot simply set the header now, because the status is not known yet. Instead it registers a
    /// callback with <c>Response.OnStarting</c>, which ASP.NET Core runs just before the response headers are sent.
    /// The exception handler clears the headers of a failed request before writing its error body, but it does not
    /// remove these callbacks, so the header survives that path too.
    /// </remarks>
    public static IApplicationBuilder UseBearerChallengeHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status401Unauthorized &&
                    !context.Response.Headers.ContainsKey(HeaderNames.WWWAuthenticate))
                {
                    context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
}
