using System.Net;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance tests (phase 5) for the routed item from T-021: without a token, any unknown path answers <c>401</c>,
/// not <c>404</c> or <c>405</c>, because the token check comes first (AC-01.5, design 2.2). ErrorHandlingTests proves
/// it for one unknown <c>/api/v1</c> path and one wrong method; these widen it to paths outside the API, other API
/// versions, malformed and out-of-range numbers, and every write method, and show that the same paths with a valid
/// token get the design 7.2 <c>404</c> or <c>405</c>.
/// </summary>
/// <remarks>
/// (v1.2, T-036) PRD A-1 changed two answers on purpose (architecture 13, "Changed on purpose, for QA"; risk 23.5): with
/// the web front end built, <c>GET /</c> and <c>GET /nope</c> without a token answer the front end's page (<c>200</c>)
/// instead of <c>401</c>, because they are screen addresses. Those two rows moved from the <c>401</c> list to
/// <see cref="A_1_screen_addresses_answer_the_page_without_a_token"/>, and the "with a token" contrast uses an unknown
/// <c>/api/</c> path instead of <c>/nope</c>. Every other row is unchanged.
/// </remarks>
public class UnknownPathAcceptanceTests(TestApiFactory factory, UnknownPathAcceptanceTests.FrontEndBuiltFactory withFrontEnd)
    : IClassFixture<TestApiFactory>, IClassFixture<UnknownPathAcceptanceTests.FrontEndBuiltFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>(v1.2) A test app with a built front end: a one-line stand-in page in its web root.</summary>
    public sealed class FrontEndBuiltFactory : TestApiFactory
    {
        /// <summary>The stand-in page; its title tells it apart from any other answer.</summary>
        public const string Page = "<!doctype html><html lang=\"en\"><title>acceptance-front-end-page</title></html>";

        /// <summary>Writes the stand-in page before the app starts.</summary>
        public FrontEndBuiltFactory() => WriteFrontEndFile("index.html", Page);
    }

    /// <summary>
    /// AC-01.5, design 2.2: without a token, each of these answers <c>401</c> <c>not-authenticated</c> with
    /// <c>WWW-Authenticate: Bearer</c>. <c>/openapi/v1.json</c> is mapped in Development only (decision #33), so in the
    /// test environment it is an unknown path like the others.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/api/v1")]
    [InlineData("GET", "/api/v2/items")]
    [InlineData("GET", "/health/details")]
    [InlineData("GET", "/openapi/v1.json")]
    [InlineData("GET", "/api/v1/production-orders/abc")]
    [InlineData("GET", "/api/v1/production-orders/99999999999")]
    [InlineData("POST", "/api/v1/auth/sign-in-now")]
    [InlineData("POST", "/api/v1/nothing-here")]
    [InlineData("PUT", "/api/v1/movements/1")]
    [InlineData("PATCH", "/api/v1/items/1")]
    [InlineData("DELETE", "/api/v1/notifications/1")]
    public async Task AC_01_5_unknown_path_or_method_without_a_token_is_401(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await anonymous.SendAsync(request, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// Design 2.2 and 7.2, the contrast: with a valid token the same kind of path gets <c>404</c> "No endpoint matches
    /// this path." and a known path with the wrong method gets <c>405</c>, so the <c>401</c> above really comes from the
    /// token check and not from the path.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/nope", HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.")]
    [InlineData("GET", "/api/v2/items", HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.")]
    [InlineData("PATCH", "/api/v1/items/1", HttpStatusCode.MethodNotAllowed, "method-not-allowed", "This method is not allowed on this path.")]
    public async Task Same_paths_with_a_token_get_404_or_405(string method, string path, HttpStatusCode status, string code, string detail)
    {
        using var manager = await factory.CreateClientAsAsync(Roles.Manager);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await manager.SendAsync(request, Cancel);

        Assert.Equal(status, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// (v1.2) PRD A-1, AC-34.1, architecture 13 (the two rows moved from the <c>401</c> list above): with the web front
    /// end built, <c>GET /</c> and <c>GET /nope</c> without a token are screen addresses and answer the front end's page,
    /// <c>200</c> HTML. The front end then shows sign-in, or its "not found" screen for an address it does not know.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/nope")]
    public async Task A_1_screen_addresses_answer_the_page_without_a_token(string path)
    {
        using var anonymous = withFrontEnd.CreateClient();

        var response = await anonymous.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FrontEndBuiltFactory.Page, await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>
    /// (v1.2) AC-01.5 still holds with the front end built (architecture 13, "Changed on purpose, for QA"): the paths
    /// under <c>/api</c>, <c>/health</c> and <c>/openapi</c> keep their <c>401</c> without a token.
    /// </summary>
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1/nope")]
    [InlineData("/health/details")]
    [InlineData("/openapi/v1.json")]
    public async Task A_1_api_paths_keep_their_401_with_the_front_end_built(string path)
    {
        using var anonymous = withFrontEnd.CreateClient();

        var response = await anonymous.GetAsync(path, Cancel);

        await ApiAssert.NotAuthenticatedAsync(response);
    }
}
