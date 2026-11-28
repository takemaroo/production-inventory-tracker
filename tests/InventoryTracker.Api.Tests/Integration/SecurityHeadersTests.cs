using System.Net;
using System.Net.Http.Headers;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.2) Proves the security headers of PRD A-2 and the absence of CORS of A-3 (architecture 14, decisions #228 and
/// #229): the exact Content-Security-Policy, <c>nosniff</c> and <c>no-referrer</c> on every kind of response (AC-37.5,
/// AC-37.6), <c>Cache-Control: no-store</c> under <c>/api</c> only, no <c>Server</c> header on the real web server, and no
/// <c>Access-Control-*</c> header for another origin (AC-37.7).
/// </summary>
/// <remarks>
/// Most tests use <see cref="SecurityHeadersApiFactory"/>: the in-memory test server with a stand-in front end and the
/// error probes, so one app can show the page, a file, the front end's own <c>404</c>, API answers and a <c>500</c>. The
/// in-memory server never adds a <c>Server</c> header itself, so that header is checked on Kestrel, the real web server, in
/// <see cref="Real_server_sends_no_Server_header_and_the_same_headers"/>, the way <c>RequestBodyLimitTests</c> does.
/// </remarks>
public class SecurityHeadersTests(SecurityHeadersTests.SecurityHeadersApiFactory factory)
    : IClassFixture<SecurityHeadersTests.SecurityHeadersApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Architecture 14, typed out here rather than taken from SecurityHeaders, so a change in the API fails these tests.
    private const string ExpectedPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "require-trusted-types-for 'script'; trusted-types 'none'";

    // Architecture 13 (T-036): the front end's cache headers, which the security headers must leave as they are.
    private const string PageCacheControl = "no-cache";
    private const string AssetCacheControl = "public, max-age=31536000, immutable";

    /// <summary>
    /// A test app with a stand-in front end (a page and one hashed script) and the probe controllers, among them the
    /// error probe that throws an unexpected exception (<see cref="ErrorProbeController"/>).
    /// </summary>
    public sealed class SecurityHeadersApiFactory : ProbeApiFactory
    {
        /// <summary>The address of the stand-in script, named like Vite's hashed files.</summary>
        public const string AssetPath = "/assets/app-5e6f7a.js";

        /// <summary>Writes the stand-in page and script before the app starts.</summary>
        public SecurityHeadersApiFactory()
        {
            WriteFrontEndFile("index.html", StandInPage);
            WriteFrontEndFile("assets/app-5e6f7a.js", "export const frontEnd = true;");
        }
    }

    /// <summary>The stand-in page: like the real one, no inline script or style, just a module script from this server.</summary>
    public const string StandInPage =
        "<!doctype html><html lang=\"en\"><title>t</title><div id=\"root\"></div>" +
        "<script type=\"module\" src=\"/assets/app-5e6f7a.js\"></script></html>";

    /// <summary>
    /// AC-37.5, AC-37.6: the front end's page (at the root, at a deep screen address, and for a HEAD), its hashed file and
    /// its own <c>404</c> for a missing file all carry the exact CSP, <c>nosniff</c> and <c>no-referrer</c>. These come
    /// from the front-end branch, which never reaches the later pipeline steps, so this also proves the headers step runs
    /// first. Their <c>Cache-Control</c> stays T-036's (architecture 13): <c>no-store</c> is for <c>/api</c> only, and the
    /// front end's <c>404</c> has none.
    /// </summary>
    [Theory]
    [InlineData("GET", "/", HttpStatusCode.OK, PageCacheControl)]
    [InlineData("GET", "/production-orders/12?tab=lines", HttpStatusCode.OK, PageCacheControl)]
    [InlineData("HEAD", "/", HttpStatusCode.OK, PageCacheControl)]
    [InlineData("GET", SecurityHeadersApiFactory.AssetPath, HttpStatusCode.OK, AssetCacheControl)]
    [InlineData("GET", "/assets/missing.js", HttpStatusCode.NotFound, null)]
    public async Task Front_end_responses_carry_the_headers_and_keep_their_cache_header(
        string method, string path, HttpStatusCode status, string? cacheControl)
    {
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Cancel);

        Assert.Equal(status, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.Equal(cacheControl, RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// AC-37.6: a <c>304 Not Modified</c> answer (the browser asking "has the page changed?" with the ETag it kept) has no
    /// body but still carries the headers, and keeps the page's <c>no-cache</c>.
    /// </summary>
    [Fact]
    public async Task Not_modified_page_carries_the_headers()
    {
        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/", Cancel);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await client.SendAsync(conditional, Cancel);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.Equal(PageCacheControl, RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// AC-37.6, SEC-11: API answers, a <c>200</c> with a token, a <c>401</c> without one (still with its
    /// <c>WWW-Authenticate</c>), the bare <c>/api</c>, an unknown path under <c>/api/</c> (<c>404</c> ProblemDetails) and
    /// an upper-case <c>/API/</c> address (routing ignores letter case, so must the rule), carry the headers and exactly
    /// <c>Cache-Control: no-store</c>.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/units", true, HttpStatusCode.OK)]
    [InlineData("/api/v1/units", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/does-not-exist", true, HttpStatusCode.NotFound)]
    [InlineData("/API/v1/units", true, HttpStatusCode.OK)]
    public async Task Api_responses_carry_the_headers_and_no_store(string path, bool withToken, HttpStatusCode status)
    {
        using var client = withToken ? await factory.CreateClientAsAsync(Roles.Warehouse) : factory.CreateClient();

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(status, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.Equal("no-store", RawHeader(response, "Cache-Control"));
        if (status == HttpStatusCode.Unauthorized)
        {
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        }
    }

    /// <summary>
    /// AC-37.6: a <c>400</c> ProblemDetails answer (a sign-in whose body is not JSON, design 6) carries the headers and
    /// <c>no-store</c>.
    /// </summary>
    [Fact]
    public async Task Bad_request_problem_details_carries_the_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/auth/sign-in", ApiJson.Content("{"), Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "validation", "The request body is not valid JSON.");
        AssertSecurityHeaders(response);
        Assert.Equal("no-store", RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// AC-37.6: a <c>500</c> from the error probe (an exception nobody expected, outside <c>/api</c>) carries the headers,
    /// although the exception handler clears the failed response's headers before it writes the error body.
    /// </summary>
    [Fact]
    public async Task Unhandled_error_500_carries_the_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/test-probe/unhandled", Cancel);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    /// <summary>
    /// AC-37.6, SEC-11: a <c>500</c> under <c>/api/</c> (the units list fails inside its database read) carries the
    /// headers and exactly <c>no-store</c>, replacing the exception handler's own cache header (<c>no-cache,no-store</c>).
    /// </summary>
    [Fact]
    public async Task Unhandled_error_500_under_api_is_no_store()
    {
        var failingRead = new AfterFirstReadInterceptor(
            command => command.CommandText.Contains("FROM \"Units\"", StringComparison.Ordinal),
            () => throw new InvalidOperationException("Test failure inside the units read."));
        await using var failing = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(failingRead)),
        };
        using var client = await failing.CreateClientAsAsync(Roles.Warehouse);
        failingRead.Arm();

        using var response = await client.GetAsync("/api/v1/units", Cancel);

        Assert.True(failingRead.HasRun);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.Equal("no-store", RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// AC-37.6: <c>/health</c> carries the headers. It is outside <c>/api</c>, so its <c>Cache-Control</c> is the health
    /// check's own and not replaced.
    /// </summary>
    [Fact]
    public async Task Health_carries_the_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.NotEqual("no-store", RawHeader(response, "Cache-Control"));
    }

    /// <summary>
    /// AC-37.5, rule by rule, on the CSP the page actually receives (not on the constant): every required directive with
    /// its value; no <c>*</c>, <c>http:</c>, <c>https:</c> or <c>data:</c> as a script source (in <c>script-src</c>, nor in
    /// <c>default-src</c>, which stands in for any directive left out); no <c>'unsafe-inline'</c> or
    /// <c>'unsafe-eval'</c> anywhere; each directive once. Also AC-37.2's browser rule: Trusted Types with no policy.
    /// </summary>
    [Fact]
    public async Task Page_policy_meets_every_AC_37_5_rule()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/", Cancel);

        var directives = ParsePolicy(RawHeader(response, "Content-Security-Policy")!);

        AssertDirective(directives, "default-src", "'self'");
        AssertDirective(directives, "script-src", "'self'");
        AssertDirective(directives, "object-src", "'none'");
        AssertDirective(directives, "base-uri", "'none'");
        AssertDirective(directives, "frame-ancestors", "'none'");
        AssertDirective(directives, "form-action", "'self'");
        AssertDirective(directives, "connect-src", "'self'");
        AssertDirective(directives, "require-trusted-types-for", "'script'");
        AssertDirective(directives, "trusted-types", "'none'");

        foreach (var name in new[] { "script-src", "default-src" })
        {
            foreach (var source in directives[name])
            {
                Assert.DoesNotContain("*", source, StringComparison.Ordinal);
                Assert.DoesNotContain(source.ToLowerInvariant(), new[] { "http:", "https:", "data:" });
            }
        }

        foreach (var sources in directives.Values)
        {
            Assert.DoesNotContain(sources, source =>
                source.Equals("'unsafe-inline'", StringComparison.OrdinalIgnoreCase)
                || source.Equals("'unsafe-eval'", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// AC-37.7, SEC-10: a request from a page of another origin (<c>Origin: http://localhost:5999</c>), with a token, gets
    /// its normal answer but no <c>Access-Control-*</c> header, so that page's browser will not let it read the answer.
    /// </summary>
    [Fact]
    public async Task Request_from_another_origin_gets_no_CORS_header()
    {
        using var client = await factory.CreateClientAsAsync(Roles.Warehouse);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/units");
        request.Headers.Add("Origin", "http://localhost:5999");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCorsHeaders(response);
    }

    /// <summary>
    /// AC-37.7: a browser's preflight from another origin (<c>OPTIONS</c> with <c>Origin</c> and
    /// <c>Access-Control-Request-Method: POST</c>, which asks "may I send this?") gets no <c>Access-Control-*</c> header,
    /// so the browser never sends the real request. A preflight carries no token, so this one has none either.
    /// </summary>
    [Fact]
    public async Task Preflight_from_another_origin_gets_no_CORS_header()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/units");
        request.Headers.Add("Origin", "http://localhost:5999");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");

        using var response = await client.SendAsync(request, Cancel);

        Assert.False(response.IsSuccessStatusCode, $"A preflight must not succeed; it got {(int)response.StatusCode}.");
        AssertNoCorsHeaders(response);
        AssertSecurityHeaders(response);
    }

    /// <summary>
    /// AC-37.7, decisions #229 and #253: no CORS policy exists anywhere, so nothing can grant another origin access.
    /// Adding one in any of the usual ways fails this test, so the decision has to be revisited on purpose:
    /// <c>AddCors(options =&gt; ...)</c> or <c>Configure&lt;CorsOptions&gt;</c> (both register an options callback), a default
    /// policy, or <c>[EnableCors]</c>/<c>[DisableCors]</c> on an endpoint.
    /// </summary>
    /// <remarks>
    /// The architecture planned to check that <c>ICorsService</c> is not registered. That cannot hold: MVC's
    /// <c>AddControllers()</c> registers the CORS service itself, empty, so that <c>[EnableCors]</c> works if a controller
    /// ever uses it (<see cref="Only_AddControllers_registers_the_empty_CORS_service"/> shows it). The service does nothing
    /// without a policy and without the CORS middleware, so this test checks for the policy instead (decision #253).
    /// </remarks>
    [Fact]
    public async Task No_CORS_policy_is_configured()
    {
        var services = factory.Services;
        Assert.Empty(services.GetServices<IConfigureOptions<CorsOptions>>());
        Assert.Empty(services.GetServices<IPostConfigureOptions<CorsOptions>>());

        var options = services.GetRequiredService<IOptions<CorsOptions>>().Value;
        Assert.Null(options.GetPolicy(options.DefaultPolicyName));

        // The policy provider is what the CORS middleware would ask; with no name it looks up the default policy.
        var provider = services.GetRequiredService<ICorsPolicyProvider>();
        Assert.Null(await provider.GetPolicyAsync(new DefaultHttpContext(), policyName: null));

        // EndpointDataSource lists every endpoint the app maps (all controller actions, /health); none may carry CORS
        // metadata, which [EnableCors] and [DisableCors] add.
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.NotEmpty(endpoints);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Metadata.GetMetadata<ICorsMetadata>() is not null);
    }

    /// <summary>
    /// AC-37.7, decision #253: the CORS service in the app comes from MVC's <c>AddControllers()</c> alone, not from any
    /// code of ours: <c>AddControllers()</c> on its own adds it, with no options callback (so no policy). If a later
    /// framework version stops doing this, this test fails, and the stricter check the architecture planned
    /// ("<c>ICorsService</c> is not registered") can be used instead.
    /// </summary>
    [Fact]
    public void Only_AddControllers_registers_the_empty_CORS_service()
    {
        var withControllers = new ServiceCollection().AddLogging();
        withControllers.AddControllers();

        Assert.Contains(withControllers, service => service.ServiceType == typeof(ICorsService));
        Assert.DoesNotContain(withControllers, service => service.ServiceType == typeof(IConfigureOptions<CorsOptions>));
    }

    /// <summary>
    /// AC-37.7, decision #256: no code line in the API's source names CORS or an <c>Access-Control-*</c> header. This
    /// catches what the behaviour and configuration tests above cannot: an inline <c>app.UseCors(policy =&gt; ...)</c> for
    /// an origin other than the one tried, and a header written by hand in a middleware.
    /// </summary>
    /// <remarks>
    /// Lines whose trimmed text starts with <c>//</c> (comments and <c>///</c> doc comments) are skipped, so an
    /// explanation such as the one in <c>SecurityHeaders.cs</c> may name the header; every other line is checked whole,
    /// including a comment after code. The scan must include Program.cs and SecurityHeaders.cs, so a wrong folder cannot
    /// pass by scanning nothing.
    /// </remarks>
    [Fact]
    public void Api_source_has_no_CORS_code()
    {
        var apiFolder = Path.Combine(FindRepositoryRoot(), "src", "InventoryTracker.Api");
        var files = Directory.GetFiles(apiFolder, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(apiFolder, file))
            .Where(relative => !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToList();
        Assert.Contains("Program.cs", files);
        Assert.Contains(Path.Combine("Common", "Security", "SecurityHeaders.cs"), files);

        var offences = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(apiFolder, file));
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal) && NamesCors(line))
                {
                    offences.Add($"{file}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0,
            "PRD A-3 forbids CORS: the front end is served from the API's own address, so no origin is ever allowed. " +
            "Allowing one is a change to the PRD first (architecture 14). Found:" + Environment.NewLine +
            string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// Decision #256: true when the line holds one of the CORS names (ordinal, case-sensitive, as the framework spells
    /// them) or <c>Access-Control-</c> in any letter case, because HTTP header names ignore case.
    /// </summary>
    private static bool NamesCors(string line) =>
        ForbiddenCorsNames.Any(name => line.Contains(name, StringComparison.Ordinal))
        || line.Contains("Access-Control-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decision #256: names that only CORS code uses (<c>AccessControl</c> covers <c>HeaderNames.AccessControlAllowOrigin</c> and the like).</summary>
    private static readonly string[] ForbiddenCorsNames =
    [
        "AddCors", "UseCors", "RequireCors", "EnableCors", "DisableCors", "CorsPolicy", "CorsOptions", "CorsService",
        "Microsoft.AspNetCore.Cors", "AccessControl",
    ];

    /// <summary>
    /// Walks up from the test binaries to the folder holding the solution file (as <c>CalculationsPurityTests</c> does), so
    /// the test finds the source files on any machine.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InventoryTracker.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("InventoryTracker.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// AC-37.6 on Kestrel, the server the owner runs (<c>AddServerHeader = false</c> in Program.cs): no response has a
    /// <c>Server</c> header, and the page, a file, the front end's <c>404</c>, API answers and <c>/health</c> carry the same
    /// headers as on the test server.
    /// </summary>
    [Fact]
    public async Task Real_server_sends_no_Server_header_and_the_same_headers()
    {
        await using var app = new RequestBodyLimitTests.KestrelApiFactory();
        app.WriteFrontEndFile("index.html", StandInPage);
        app.WriteFrontEndFile("assets/app-5e6f7a.js", "export const frontEnd = true;");
        using var anonymous = new HttpClient { BaseAddress = app.ServerAddress };
        using var signedIn = new HttpClient { BaseAddress = app.ServerAddress };
        signedIn.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await SignInOverHttpAsync(app, anonymous));

        var cases = new (HttpClient Client, string Path, HttpStatusCode Status, string? CacheControl)[]
        {
            (anonymous, "/", HttpStatusCode.OK, PageCacheControl),
            (anonymous, SecurityHeadersApiFactory.AssetPath, HttpStatusCode.OK, AssetCacheControl),
            (anonymous, "/assets/missing.js", HttpStatusCode.NotFound, null),
            (anonymous, "/api/v1/units", HttpStatusCode.Unauthorized, "no-store"),
            (signedIn, "/api/v1/units", HttpStatusCode.OK, "no-store"),
            (signedIn, "/api/v1/does-not-exist", HttpStatusCode.NotFound, "no-store"),
        };

        foreach (var (client, path, status, cacheControl) in cases)
        {
            using var response = await client.GetAsync(path, Cancel);

            Assert.Equal(status, response.StatusCode);
            Assert.Null(RawHeader(response, "Server"));
            AssertSecurityHeaders(response);
            Assert.Equal(cacheControl, RawHeader(response, "Cache-Control"));
        }

        using var health = await anonymous.GetAsync("/health", Cancel);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Null(RawHeader(health, "Server"));
        AssertSecurityHeaders(health);
    }

    /// <summary>AC-37.5, AC-37.6: the exact CSP of architecture 14, <c>nosniff</c> and <c>no-referrer</c>, each sent once.</summary>
    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(ExpectedPolicy, RawHeader(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", RawHeader(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", RawHeader(response, "Referrer-Policy"));
    }

    /// <summary>AC-37.7: no header whose name starts with <c>Access-Control-</c>, among the response or content headers.</summary>
    private static void AssertNoCorsHeaders(HttpResponseMessage response)
    {
        var names = response.Headers.NonValidated.Select(header => header.Key)
            .Concat(response.Content.Headers.NonValidated.Select(header => header.Key));
        Assert.DoesNotContain(names, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a CSP into its directives (separated by <c>;</c>), each a name and its sources (separated by spaces), and
    /// fails if a directive appears twice: the browser would ignore the second one, so the policy would not say what it
    /// seems to.
    /// </summary>
    private static Dictionary<string, string[]> ParsePolicy(string policy)
    {
        var directives = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var directive in policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(directives.TryAdd(parts[0], parts[1..]), $"The directive {parts[0]} appears twice.");
        }

        return directives;
    }

    /// <summary>Checks that the directive is present and lists exactly the one expected source.</summary>
    private static void AssertDirective(Dictionary<string, string[]> directives, string name, string source)
    {
        Assert.True(directives.TryGetValue(name, out var sources), $"The CSP has no {name} directive.");
        Assert.Equal([source], sources);
    }

    /// <summary>Creates a Warehouse user and signs in over the real server, returning the token.</summary>
    private static async Task<string> SignInOverHttpAsync(TestApiFactory app, HttpClient client)
    {
        var email = Seed.UniqueEmail("headers");
        await app.CreateUserAsync(email, Roles.Warehouse);
        using var response = await client.PostAsync("/api/v1/auth/sign-in",
            ApiJson.Content($"{{\"email\":\"{email}\",\"password\":\"{TestApiFactory.UserPassword}\"}}"), Cancel);
        response.EnsureSuccessStatusCode();
        return (await ApiJson.ReadAsync(response)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>
    /// A response header exactly as the server sent it, or null when absent. Several values of one name come back joined
    /// with ", ", so a header sent twice fails an exact comparison.
    /// </summary>
    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}
