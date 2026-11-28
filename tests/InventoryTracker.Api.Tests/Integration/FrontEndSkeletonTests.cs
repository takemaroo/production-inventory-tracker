using System.Net;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.2) Proves the front-end skeleton is served by the API at the same address (PRD A-1, Q104): the page at the root,
/// the same page for a screen address opened directly (AC-36.3), the built files themselves, and every <c>/api/</c> path
/// answering exactly as before. T-036 completes A-1 and widens these tests (architecture, part v1.2).
/// </summary>
public class FrontEndSkeletonTests(FrontEndSkeletonTests.FrontEndApiFactory factory)
    : IClassFixture<FrontEndSkeletonTests.FrontEndApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A test app whose web root holds a tiny stand-in for the built front end.</summary>
    public sealed class FrontEndApiFactory : TestApiFactory
    {
        /// <summary>Text only the stand-in page contains, so a test can tell it from any other answer.</summary>
        public const string PageMarker = "front-end-skeleton-page";

        /// <summary>Writes the stand-in page and one script file before the app starts.</summary>
        public FrontEndApiFactory()
        {
            WriteFrontEndFile("index.html", $"<!doctype html><html lang=\"en\"><title>{PageMarker}</title></html>");
            WriteFrontEndFile("assets/app-1a2b3c.js", "export const skeleton = true;");
        }
    }

    /// <summary>A-1: the front end's page answers at the root of the API's address, without a token (US-34).</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/stock")]
    [InlineData("/production-orders/12")]
    [InlineData("/sign-in")]
    public async Task A_1_screen_addresses_return_the_front_end_page(string path)
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(FrontEndApiFactory.PageMarker, await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>A-1: a built file is served as itself, without a token.</summary>
    [Fact]
    public async Task A_1_built_files_are_served()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync("/assets/app-1a2b3c.js", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("export const skeleton = true;", await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>
    /// A-1: every path under <c>/api/</c> answers as today: <c>401</c> without a token, and the <c>404</c> ProblemDetails
    /// for an unknown path with one (architecture 4.1 item 8), never the front end's page.
    /// </summary>
    [Fact]
    public async Task A_1_api_paths_are_unchanged()
    {
        using var anonymous = factory.CreateClient();
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync("/api/v1/units", Cancel));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync("/api/v1/does-not-exist", Cancel));

        using var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var unknown = await warehouse.GetAsync("/api/v1/does-not-exist", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }
}
