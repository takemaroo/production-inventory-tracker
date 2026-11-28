using System.Net;
using System.Net.Http.Headers;
using System.Text;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.FrontEnd;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.2) Proves how the API serves the web front end (PRD A-1, architecture 13, decision #227), row by row of the
/// architecture's table, with and without a token: the page at every screen address (AC-34.1, AC-36.3), the built
/// files, a <c>404</c> for a missing file, the cache headers, and every <c>/api</c>, <c>/health</c> and <c>/openapi</c>
/// path answering exactly as before. Also that an app without a built front end answers everything as before v1.2,
/// with one start-up warning.
/// </summary>
/// <remarks>
/// Two test apps are shared by the tests of this class (xUnit class fixtures): <see cref="FrontEndApiFactory"/>, whose
/// web root holds a tiny stand-in for the built front end, and the default <see cref="TestApiFactory"/>, whose web root
/// holds nothing (the host creates the configured folder empty), as on a checkout where nobody has run the front-end
/// build. The two start-up warning tests start apps of their own, so their logs hold only start-up entries.
/// </remarks>
public class FrontEndTests(FrontEndTests.FrontEndApiFactory factory, TestApiFactory withoutFrontEnd)
    : IClassFixture<FrontEndTests.FrontEndApiFactory>, IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Architecture 13, typed out here rather than taken from FrontEndSetup, so a change in the API fails these tests.
    private const string PageCacheControl = "no-cache";
    private const string AssetCacheControl = "public, max-age=31536000, immutable";
    private const string NotBuiltWarning =
        @"The web front end is not built (no wwwroot/index.html), so only the API is served. Build it with scripts\build.ps1 or scripts\publish.ps1.";

    /// <summary>A test app whose web root holds a tiny stand-in for the built front end.</summary>
    public sealed class FrontEndApiFactory : TestApiFactory
    {
        /// <summary>The stand-in page. Its title is text only this page contains, so a test can tell it from any other answer.</summary>
        public const string Page = "<!doctype html><html lang=\"en\"><title>front-end-test-page</title></html>";

        /// <summary>The address of the stand-in script, named like Vite's hashed files.</summary>
        public const string AssetPath = "/assets/app-1a2b3c.js";

        /// <summary>The stand-in script's content.</summary>
        public const string Asset = "export const frontEnd = true;";

        /// <summary>A web-root file outside <c>/assets/</c>, which gets <c>no-cache</c> like the page.</summary>
        public const string OtherFilePath = "/robots.txt";

        /// <summary>Writes the stand-in page, script and other file before the app starts.</summary>
        public FrontEndApiFactory()
        {
            WriteFrontEndFile("index.html", Page);
            WriteFrontEndFile("assets/app-1a2b3c.js", Asset);
            WriteFrontEndFile("robots.txt", "User-agent: *");
        }
    }

    /// <summary>
    /// Architecture 13, row 2 (screen addresses), AC-34.1 and AC-36.3: the root, a deep screen address with a query
    /// string and the sign-in screen all answer the page, <c>200</c> <c>text/html; charset=utf-8</c>, with or without a
    /// token. The page is served by the static-file step (decision #250), so it carries a Content-Length and an ETag, and
    /// its cache header is <c>no-cache</c>.
    /// </summary>
    [Theory]
    [InlineData("/", false)]
    [InlineData("/", true)]
    [InlineData("/stock", false)]
    [InlineData("/production-orders/12?tab=lines&page=2", false)]
    [InlineData("/production-orders/12?tab=lines&page=2", true)]
    [InlineData("/sign-in", false)]
    [InlineData("/sign-in?return=%2Fproduction-orders%2F12", false)]
    [InlineData("/sign-in", true)]
    [InlineData("/assets", false)]
    public async Task Screen_addresses_answer_the_page(string path, bool withToken)
    {
        using var client = await ClientAsync(withToken);

        var response = await client.GetAsync(path, Cancel);

        await AssertIsThePageAsync(response);
    }

    /// <summary>Architecture 13, row 1: <c>/index.html</c> itself is a file of the web root and answers the page, with <c>no-cache</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Index_html_answers_the_page(bool withToken)
    {
        using var client = await ClientAsync(withToken);

        var response = await client.GetAsync("/index.html", Cancel);

        await AssertIsThePageAsync(response);
    }

    /// <summary>
    /// Architecture 13, rows 1 and 2, HEAD: a HEAD of the page, at a screen address or the root, gets the same status and
    /// headers as a GET (type, length, cache header) and no body, with or without a token.
    /// </summary>
    [Theory]
    [InlineData("/", false)]
    [InlineData("/", true)]
    [InlineData("/production-orders/12", false)]
    [InlineData("/production-orders/12", true)]
    public async Task Head_of_the_page_answers_its_headers_only(string path, bool withToken)
    {
        using var client = await ClientAsync(withToken);
        using var request = new HttpRequestMessage(HttpMethod.Head, path);

        var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", RawContentHeader(response, "Content-Type"));
        Assert.Equal(Encoding.UTF8.GetByteCount(FrontEndApiFactory.Page), response.Content.Headers.ContentLength);
        Assert.Equal(PageCacheControl, RawHeader(response, "Cache-Control"));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    /// <summary>
    /// Architecture 13, row 1 and cache headers: a built file under <c>/assets/</c> is served as itself, without a token
    /// too, with <c>Cache-Control: public, max-age=31536000, immutable</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asset_is_served_with_the_one_year_immutable_cache_header(bool withToken)
    {
        using var client = await ClientAsync(withToken);

        var response = await client.GetAsync(FrontEndApiFactory.AssetPath, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FrontEndApiFactory.Asset, await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(AssetCacheControl, RawHeader(response, "Cache-Control"));
    }

    /// <summary>Architecture 13, cache headers: any other web-root file (outside <c>/assets/</c>) gets <c>no-cache</c>.</summary>
    [Fact]
    public async Task Other_web_root_file_gets_no_cache()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(FrontEndApiFactory.OtherFilePath, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("User-agent: *", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(PageCacheControl, RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// Architecture 13, cache headers with the ETag (decision #250): a browser that already holds the page asks "has it
    /// changed?" with If-None-Match, from any screen address, and gets <c>304</c> with the same <c>no-cache</c>; the
    /// same for an asset, with its immutable header.
    /// </summary>
    [Theory]
    [InlineData("/", "/production-orders/12", PageCacheControl)]
    [InlineData(FrontEndApiFactory.AssetPath, FrontEndApiFactory.AssetPath, AssetCacheControl)]
    public async Task Unchanged_file_answers_304_with_its_cache_header(string firstPath, string secondPath, string cacheControl)
    {
        using var anonymous = factory.CreateClient();
        var first = await anonymous.GetAsync(firstPath, Cancel);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var again = new HttpRequestMessage(HttpMethod.Get, secondPath);
        again.Headers.IfNoneMatch.Add(etag);
        var second = await anonymous.SendAsync(again, Cancel);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(etag, second.Headers.ETag);
        Assert.Equal(cacheControl, RawHeader(second, "Cache-Control"));
    }

    /// <summary>
    /// Architecture 13, row 3: a GET or HEAD outside the API prefixes whose last segment has a dot and no such file
    /// (<c>/assets/missing.js</c>, <c>/favicon.ico</c>) answers the <c>404</c> ProblemDetails <c>not-found</c>, with or
    /// without a token (no <c>401</c>, no <c>WWW-Authenticate</c>), and never the page.
    /// </summary>
    [Theory]
    [InlineData("/assets/missing.js", false)]
    [InlineData("/assets/missing.js", true)]
    [InlineData("/favicon.ico", false)]
    [InlineData("/favicon.ico", true)]
    [InlineData("/production-orders/12/report.csv", false)]
    [InlineData("/api.txt", false)]
    public async Task Missing_file_answers_404_not_the_page(string path, bool withToken)
    {
        using var client = await ClientAsync(withToken);

        var response = await client.GetAsync(path, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.");
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Null(response.Headers.CacheControl);
    }

    /// <summary>Architecture 13, row 3, HEAD: a HEAD of a missing file is a <c>404</c> too, without a token.</summary>
    [Fact]
    public async Task Head_of_a_missing_file_is_404()
    {
        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/assets/missing.js");

        var response = await anonymous.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    /// <summary>
    /// Architecture 13, row 4: any other method on a front-end path (<c>POST /stock</c>, a <c>DELETE</c> of a built
    /// file) answers as before v1.2: <c>401</c> without a token (AC-01.5), <c>404</c> with one.
    /// </summary>
    [Theory]
    [InlineData("POST", "/stock")]
    [InlineData("POST", "/")]
    [InlineData("DELETE", FrontEndApiFactory.AssetPath)]
    public async Task Other_methods_answer_as_before(string method, string path)
    {
        using var anonymous = factory.CreateClient();
        using var anonymousRequest = new HttpRequestMessage(new HttpMethod(method), path);
        await ApiAssert.NotAuthenticatedAsync(await anonymous.SendAsync(anonymousRequest, Cancel));

        using var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await warehouse.SendAsync(request, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.");
    }

    /// <summary>
    /// Architecture 13, row 5: paths under <c>/api</c>, <c>/health</c> and <c>/openapi</c> answer exactly as before v1.2
    /// although the front end is built: <c>401</c> first without a token (AC-01.5), even when the last segment has a
    /// dot, and the <c>404</c> ProblemDetails for an unknown path with one (architecture 4.1 item 8). <c>/openapi/v1.json</c>
    /// is mapped in Development only (decision #33), so in the test environment it is an unknown path.
    /// </summary>
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/api/missing.js")]
    [InlineData("/health/details")]
    [InlineData("/openapi/v1.json")]
    public async Task Api_health_and_openapi_paths_answer_as_before(string path)
    {
        using var anonymous = factory.CreateClient();
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync(path, Cancel));

        using var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var response = await warehouse.GetAsync(path, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.");
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Architecture 13, row 5: a real API endpoint still answers <c>401</c> without a token and its JSON with one.</summary>
    [Fact]
    public async Task Api_endpoint_answers_as_before()
    {
        using var anonymous = factory.CreateClient();
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync("/api/v1/units", Cancel));

        using var warehouse = await factory.CreateClientAsAsync(Roles.Warehouse);
        var response = await warehouse.GetAsync("/api/v1/units", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Architecture 13, row 5: <c>/health</c> answers "Healthy" as before (decision #28), with or without a token, never the page.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_answers_as_before(bool withToken)
    {
        using var client = await ClientAsync(withToken);

        var response = await client.GetAsync("/health", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>Architecture 13, last row: with the front end built, start-up logs no Warning at all.</summary>
    [Fact]
    public async Task No_warning_when_the_front_end_is_built()
    {
        // A fresh app, so its log holds only start-up entries.
        await using var app = new FrontEndApiFactory();
        _ = app.Services; // starts the app

        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// Architecture 13, last row (front end not built): with the default test app, whose web root is empty, every
    /// one of these answers exactly as before v1.2, <c>401</c> without a token, because nothing but the token check and
    /// the endpoints sees them.
    /// </summary>
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/stock")]
    [InlineData("GET", "/production-orders/12?tab=lines")]
    [InlineData("GET", "/sign-in")]
    [InlineData("GET", "/index.html")]
    [InlineData("GET", "/assets/missing.js")]
    [InlineData("GET", "/favicon.ico")]
    [InlineData("HEAD", "/")]
    [InlineData("POST", "/stock")]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/api/v1/does-not-exist")]
    [InlineData("GET", "/health/details")]
    [InlineData("GET", "/openapi/v1.json")]
    public async Task Not_built_answers_as_before_without_a_token(string method, string path)
    {
        using var anonymous = withoutFrontEnd.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await anonymous.SendAsync(request, Cancel);

        if (method == "HEAD")
        {
            // A HEAD answer has no body to check, only the status and the challenge header.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(new AuthenticationHeaderValue("Bearer").ToString(), response.Headers.WwwAuthenticate.ToString());
            return;
        }

        await ApiAssert.NotAuthenticatedAsync(response);
    }

    /// <summary>
    /// Architecture 13, last row (front end not built), with a token: the same paths answer the <c>404</c> ProblemDetails
    /// as before v1.2, never a page.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/production-orders/12?tab=lines")]
    [InlineData("/index.html")]
    [InlineData("/assets/missing.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/api/v1/does-not-exist")]
    public async Task Not_built_answers_as_before_with_a_token(string path)
    {
        using var warehouse = await withoutFrontEnd.CreateClientAsAsync(Roles.Warehouse);

        var response = await warehouse.GetAsync(path, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.");
    }

    /// <summary>
    /// Architecture 13, last row, the start-up warning: the default test app (no front end) logs our Warning exactly
    /// once, with the architecture's wording, from <c>FrontEndSetup</c>, and it is the only Warning at start-up.
    /// </summary>
    /// <remarks>
    /// A fresh app, so its log holds only start-up entries. The host creates a web root folder that is configured
    /// explicitly, as <see cref="TestApiFactory"/> does, so this app has an empty web root rather than none. The case of
    /// no folder at all is <see cref="No_web_root_folder_logs_only_our_warning"/>.
    /// </remarks>
    [Fact]
    public async Task Not_built_logs_one_warning_at_start_up()
    {
        await using var app = new TestApiFactory();
        _ = app.Services; // starts the app

        var warnings = app.Logs.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

        var ours = Assert.Single(warnings);
        Assert.Equal(NotBuiltWarning, ours.Message);
        Assert.Equal(typeof(FrontEndSetup).FullName, ours.Category);
    }

    /// <summary>
    /// Architecture 13, last row: a web root that has files but no <c>index.html</c> (a broken or half-finished build)
    /// also counts as "not built": its files are not served, everything answers as before v1.2, and our warning is the
    /// one start-up Warning.
    /// </summary>
    [Fact]
    public async Task Web_root_without_the_page_counts_as_not_built()
    {
        await using var app = new TestApiFactory();
        app.WriteFrontEndFile("assets/app-1a2b3c.js", FrontEndApiFactory.Asset);
        using var anonymous = app.CreateClient();

        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync("/", Cancel));
        await ApiAssert.NotAuthenticatedAsync(await anonymous.GetAsync(FrontEndApiFactory.AssetPath, Cancel));

        var warnings = app.Logs.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(NotBuiltWarning, Assert.Single(warnings).Message);
    }

    /// <summary>
    /// Architecture 13, last row, and decision #251: with no web root folder at all (a fresh checkout before any
    /// front-end build), start-up logs exactly one Warning, ours, and not ASP.NET Core's "The WebRootPath was not found"
    /// beside it, because no static-file step is added. Every request goes on to the rest of the pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare app holding only the front-end steps and a stand-in for the rest of the pipeline, because a
    /// <see cref="TestApiFactory"/> app always has a web root folder (the host creates one that is set explicitly).
    /// </para>
    /// <para>
    /// Two shapes of file provider. Without <paramref name="developmentProvider"/>: the empty provider the host gives a
    /// missing folder (Production, and Development without static web assets). Without the early return in
    /// <c>UseFrontEnd</c> this case logs the framework's warning too, and the test fails. With it: the composite
    /// provider Development wraps around it for static web assets. The framework stays quiet for that shape either way,
    /// so this row proves only that our check still holds and the requests pass through.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_web_root_folder_logs_only_our_warning(bool developmentProvider)
    {
        var contentRoot = NewTempFolder(); // no wwwroot inside
        try
        {
            var logs = new CapturingLoggerProvider();
            await using var app = BuildBareApp(contentRoot, logs);
            Assert.False(Directory.Exists(Path.Combine(contentRoot, "wwwroot")));
            if (developmentProvider)
            {
                // What the Development host does for static web assets: wrap the web root's provider in a composite one.
                app.Environment.WebRootFileProvider = new CompositeFileProvider(app.Environment.WebRootFileProvider);
            }

            app.UseFrontEnd();
            app.WarnIfFrontEndNotBuilt();
            app.Run(RestOfThePipeline);
            await app.StartAsync(Cancel);
            using var client = app.GetTestClient();

            Assert.Equal(RestOfThePipelineStatus, (await client.GetAsync("/", Cancel)).StatusCode);
            Assert.Equal(RestOfThePipelineStatus, (await client.GetAsync("/favicon.ico", Cancel)).StatusCode);

            var warnings = logs.Entries.Where(entry => entry.Level >= LogLevel.Warning).ToList();
            var ours = Assert.Single(warnings);
            Assert.Equal(NotBuiltWarning, ours.Message);
            Assert.Equal(typeof(FrontEndSetup).FullName, ours.Category);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    /// <summary>
    /// Architecture 13, row 2, and decision #250: the page is served by rewriting a screen address to
    /// <c>/index.html</c>, and the original path is put back afterwards, so a step the response passes back through
    /// (here a recorder placed before the front end, as the server's "Request finished" log is) sees the address the
    /// browser asked for.
    /// </summary>
    [Fact]
    public async Task Rewrite_puts_the_screen_address_back_after_serving_the_page()
    {
        var contentRoot = NewTempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot"));
            File.WriteAllText(Path.Combine(contentRoot, "wwwroot", "index.html"), FrontEndApiFactory.Page);
            var logs = new CapturingLoggerProvider();
            await using var app = BuildBareApp(contentRoot, logs);
            var pathsSeenOnTheWayBack = new List<string>();
            app.Use(async (context, next) =>
            {
                await next(context);
                pathsSeenOnTheWayBack.Add(context.Request.Path.Value!);
            });
            app.UseFrontEnd();
            app.Run(RestOfThePipeline);
            await app.StartAsync(Cancel);
            using var client = app.GetTestClient();

            var response = await client.GetAsync("/production-orders/12?tab=lines", Cancel);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(FrontEndApiFactory.Page, await response.Content.ReadAsStringAsync(Cancel));
            Assert.Equal(["/production-orders/12"], pathsSeenOnTheWayBack);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    /// <summary>The status the bare apps' stand-in for the rest of the pipeline answers, so a test sees the request got there.</summary>
    private const HttpStatusCode RestOfThePipelineStatus = HttpStatusCode.Accepted;

    /// <summary>Stand-in for the token check and the API endpoints in the bare apps.</summary>
    private static Task RestOfThePipeline(HttpContext context)
    {
        context.Response.StatusCode = (int)RestOfThePipelineStatus;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A bare app for testing <see cref="FrontEndSetup"/> on its own: the given folder as its content root (so its web
    /// root is <c>wwwroot</c> inside it, present or not), the in-memory test server instead of a network port, and only
    /// the capturing logger. The caller adds the steps and starts it.
    /// </summary>
    private static WebApplication BuildBareApp(string contentRoot, CapturingLoggerProvider logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        return builder.Build();
    }

    /// <summary>A new, empty folder in the temp folder; the test deletes it.</summary>
    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"inventory-tests-contentroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>An anonymous client, or one with a Warehouse user's token.</summary>
    private async Task<HttpClient> ClientAsync(bool withToken) =>
        withToken ? await factory.CreateClientAsAsync(Roles.Warehouse) : factory.CreateClient();

    /// <summary>Checks that the response is the stand-in page with the headers of architecture 13.</summary>
    private static async Task AssertIsThePageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", RawContentHeader(response, "Content-Type"));
        Assert.Equal(FrontEndApiFactory.Page, await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(Encoding.UTF8.GetByteCount(FrontEndApiFactory.Page), response.Content.Headers.ContentLength);
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal(PageCacheControl, RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// A response header exactly as the server sent it. HttpClient's typed headers would re-format the value (and could
    /// reorder the parts of a Cache-Control), which would hide a wrong value.
    /// </summary>
    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;

    /// <summary>A content header (Content-Type) exactly as the server sent it.</summary>
    private static string? RawContentHeader(HttpResponseMessage response, string name) =>
        response.Content.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}
