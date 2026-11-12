using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Shared assertions for the API's error responses. Every error is an RFC 9457 <em>ProblemDetails</em> body (design 6):
/// a JSON object with <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>, <c>code</c> and <c>traceId</c>, sent as
/// <c>application/problem+json</c>. These methods check all of it in one call, so every endpoint's tests hold the
/// errors to the same standard.
/// </summary>
public static class ApiAssert
{
    /// <summary>
    /// Pass this as the <c>title</c> of the <c>Problem</c> overload that takes a title, to skip the title check.
    /// Allowed only for a status whose title design 6 does not fix (such as <c>405</c> and <c>500</c>, which keep the
    /// framework's default title); for any other status the assertion fails.
    /// </summary>
    /// <remarks>
    /// A named value rather than <c>null</c>, so that skipping the check is always a visible, deliberate choice in the
    /// test and never the result of a missing argument. It is <c>const</c> so it also works in <c>[InlineData]</c>.
    /// </remarks>
    public const string NoTitleCheck = "<no title check>";

    /// <summary>
    /// Checks that <paramref name="response"/> is the design 6 error for <paramref name="status"/>, with the given
    /// <c>code</c> and <c>detail</c> and the fixed title for that status, and returns the body so a test can check
    /// more (for example the <c>errors</c> of a validation error).
    /// </summary>
    public static async Task<JsonElement> ProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string detail)
    {
        // The status first: when it is wrong, the failure names the status, not a JSON error from an empty body.
        Assert.Equal(status, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Problem(response, body, status, code, detail);
        return body;
    }

    /// <summary>
    /// Checks every field of a design 6 error body that a test has already read with <see cref="ApiJson.ReadAsync"/>,
    /// with the fixed title design 6 gives <paramref name="status"/> (<see cref="TitleFor"/>). Fails if the status has
    /// no fixed title: use the overload with a title and pass <see cref="NoTitleCheck"/> for those.
    /// </summary>
    public static void Problem(HttpResponseMessage response, JsonElement body,
        HttpStatusCode status, string code, string detail)
    {
        var title = TitleFor(status)
            ?? throw new ArgumentException(
                $"Design 6 fixes no title for {(int)status}; pass ApiAssert.NoTitleCheck to skip the title check.",
                nameof(status));
        Problem(response, body, status, title, code, detail);
    }

    /// <summary>
    /// Checks every field of a design 6 error body that a test has already read with <see cref="ApiJson.ReadAsync"/>,
    /// with an explicit expected <paramref name="title"/>.
    /// </summary>
    /// <param name="title">
    /// The expected <c>title</c>, or <see cref="NoTitleCheck"/> for a status whose title design 6 does not fix.
    /// </param>
    public static void Problem(HttpResponseMessage response, JsonElement body,
        HttpStatusCode status, string title, string code, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());

        // Design 6: "type" is left at the framework default, so only its presence is checked.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("type").GetString()));
        if (title == NoTitleCheck)
        {
            // Skipping is only for statuses without a fixed title; a 404 or 409 must always have its title checked.
            Assert.Null(TitleFor(status));
        }
        else
        {
            Assert.Equal(title, body.GetProperty("title").GetString());
        }

        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());

        // The trace id lets support match a client's error to the server log (architecture 6, Logging).
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    /// <summary>
    /// Checks a <c>400</c> <c>validation</c> error with exactly one message, <paramref name="message"/>, listed under
    /// the field <paramref name="path"/> and also given as the <c>detail</c>.
    /// </summary>
    public static async Task ValidationAsync(HttpResponseMessage response, string path, string message)
    {
        var body = await ProblemAsync(response, HttpStatusCode.BadRequest, "validation", message);
        Assert.Equal([message], ApiJson.Messages(body, path));
    }

    /// <summary>
    /// AC-01.5: checks the <c>401</c> <c>not-authenticated</c> error, including the <c>WWW-Authenticate: Bearer</c>
    /// header that tells a client which kind of credentials to send.
    /// </summary>
    public static async Task NotAuthenticatedAsync(HttpResponseMessage response)
    {
        await ProblemAsync(response, HttpStatusCode.Unauthorized, "not-authenticated", ExpectedMessages.NotAuthenticated);
        Assert.Equal(new AuthenticationHeaderValue("Bearer").ToString(), response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>
    /// The fixed <c>title</c> design 6 gives each status, or null for a status it gives none.
    /// </summary>
    public static string? TitleFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Invalid request",
        HttpStatusCode.Unauthorized => "Not signed in",
        HttpStatusCode.Forbidden => "Not allowed",
        HttpStatusCode.NotFound => "Not found",
        HttpStatusCode.Conflict => "Conflict with current state",
        _ => null,
    };
}
