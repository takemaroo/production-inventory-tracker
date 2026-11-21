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
public class UnknownPathAcceptanceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-01.5, design 2.2: without a token, each of these answers <c>401</c> <c>not-authenticated</c> with
    /// <c>WWW-Authenticate: Bearer</c>. <c>/openapi/v1.json</c> is mapped in Development only (decision #33), so in the
    /// test environment it is an unknown path like the others.
    /// </summary>
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/nope")]
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
    [InlineData("GET", "/nope", HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.")]
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
}
