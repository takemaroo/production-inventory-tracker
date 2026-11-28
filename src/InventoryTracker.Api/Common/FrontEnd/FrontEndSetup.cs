namespace InventoryTracker.Api.Common.FrontEnd;

/// <summary>
/// Serves the web front end (version 1.2) from the API's own address and port (PRD A-1, owner decision #190), so the
/// owner starts one process and the browser talks to one origin, which is why no CORS policy is needed (A-3).
/// </summary>
/// <remarks>
/// <para>
/// The front end is built by <c>npm run build</c> in <c>src/InventoryTracker.Web</c> into this project's web root,
/// <c>wwwroot</c> (git-ignored), and <c>dotnet publish</c> copies that folder into the published app. The web root is
/// the folder ASP.NET Core serves files from; <c>IWebHostEnvironment.WebRootFileProvider</c> reads it.
/// </para>
/// <para>
/// Skeleton version (tech lead, phase 3). T-036 completes A-1: cache headers, a <c>404</c> for a missing file such as
/// <c>/assets/missing.js</c>, a start-up warning when the front end has not been built, and the full tests
/// (docs/03-architecture.md, part v1.2, "How the API serves the front end").
/// </para>
/// </remarks>
public static class FrontEndSetup
{
    /// <summary>The front end's one HTML page. Every screen address returns it; the front end then draws that screen.</summary>
    public const string PageFile = "index.html";

    /// <summary>
    /// Adds the two front-end steps to the middleware pipeline. Call it before <c>UseAuthentication</c>: the front end's
    /// files are public (the sign-in screen must load without a token), while every API path keeps its token check.
    /// </summary>
    public static WebApplication UseFrontEnd(this WebApplication app)
    {
        // Step 1, the files themselves (index.html, assets/index-1a2b3c.js and so on). UseStaticFiles answers a request
        // only when a file with exactly that path exists in wwwroot and no endpoint (an API route) matched the path;
        // otherwise it passes the request on unchanged, so every /api/ path answers exactly as before.
        app.UseStaticFiles();

        // Step 2, the "SPA fallback". The front end is a single-page application: one page, with the screen chosen by
        // the address (/production-orders/12). Opening or reloading such an address asks the server for that path, and
        // no file has it, so the server answers with index.html instead (AC-36.3: every screen has its own address).
        app.Use(async (context, next) =>
        {
            var page = app.Environment.WebRootFileProvider.GetFileInfo(PageFile);
            if (IsScreenAddress(context) && page.Exists)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(page, context.RequestAborted);
                return;
            }

            await next(context);
        });

        return app;
    }

    /// <summary>
    /// True for a request that should get the front end's page: a GET or HEAD that no API endpoint matched, outside
    /// <c>/api</c>, <c>/health</c> and <c>/openapi</c> (PRD row 88: no screen address starts with those), and whose last
    /// path segment has no dot, because a path such as <c>/assets/app.js</c> names a file rather than a screen.
    /// </summary>
    public static bool IsScreenAddress(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // GetEndpoint() is the API route that routing matched for this request, or null when none did.
        if (context.GetEndpoint() is not null)
        {
            return false;
        }

        var path = request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi"))
        {
            return false;
        }

        var value = path.Value ?? "/";
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return !lastSegment.Contains('.');
    }
}
