using InventoryTracker.Api.Common.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

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
/// The request rules and cache headers are those of docs/03-architecture.md section 13 ("How the API serves the front
/// end"), decision #227. In short: a GET or HEAD outside <c>/api</c>, <c>/health</c> and <c>/openapi</c> that no API
/// endpoint matched gets the file it names, the page when it names a screen, or a <c>404</c> when it names a file that
/// does not exist. Everything else, and everything while the front end is not built, answers exactly as before v1.2.
/// </para>
/// </remarks>
public static class FrontEndSetup
{
    /// <summary>The front end's one HTML page. Every screen address returns it; the front end then draws that screen.</summary>
    public const string PageFile = "index.html";

    /// <summary>
    /// Architecture 13: the one start-up Warning when <c>wwwroot/index.html</c> is missing, word for word, so the owner
    /// knows why the browser shows no page and how to fix it.
    /// </summary>
    public const string NotBuiltWarning =
        @"The web front end is not built (no wwwroot/index.html), so only the API is served. Build it with scripts\build.ps1 or scripts\publish.ps1.";

    /// <summary>
    /// Architecture 13: <c>Cache-Control</c> for the page and for every web-root file outside <c>/assets/</c>.
    /// "no-cache" does not mean "do not store": the browser may keep a copy but must ask the server whether it is still
    /// current before using it (with the ETag), so a new build is picked up at the next load.
    /// </summary>
    public const string PageCacheControl = "no-cache";

    /// <summary>
    /// Architecture 13: <c>Cache-Control</c> for the built files under <c>/assets/</c>. Vite puts a hash of each file's
    /// content in its name (<c>index-BNfstYFL.js</c>), so a changed file always gets a new name and the old name's
    /// content never changes. The browser may therefore keep it for a year (31,536,000 seconds) and, being
    /// "immutable", never even ask again.
    /// </summary>
    public const string AssetCacheControl = "public, max-age=31536000, immutable";

    /// <summary>The folder, under the web root, where Vite writes the hashed files (its <c>assetsDir</c>).</summary>
    private static readonly PathString AssetsFolder = new("/assets");

    /// <summary>The page's own path, which a screen address is rewritten to (see <see cref="RewriteScreenAddressToPage"/>).</summary>
    private static readonly PathString PagePath = new("/" + PageFile);

    /// <summary>
    /// Adds the front-end steps to the middleware pipeline. Call it before <c>UseAuthentication</c>: the front end's
    /// files are public (the sign-in screen must load without a token), while every API path keeps its token check.
    /// </summary>
    public static WebApplication UseFrontEnd(this WebApplication app)
    {
        // Architecture 13, last row, and decision #251: with no web root folder at all there can be no front end, so
        // no front-end step is added. That also keeps ASP.NET Core's static-file step from being built, and with it its
        // own start-up warning ("The WebRootPath was not found"), so the owner sees exactly one warning: ours.
        // The folder itself is checked, not the kind of file provider: in Development the host wraps the provider in a
        // composite one for its "static web assets" (files it maps from the project folder), so a missing folder
        // there would not show up as an empty provider.
        if (!WebRootFolderExists(app.Environment))
        {
            return app;
        }

        var webRoot = app.Environment.WebRootFileProvider;

        // UseWhen adds a side branch to the pipeline: requests for which the condition is true go through the branch's
        // steps, all others skip it and carry on down the main pipeline (to the token check and the API endpoints).
        // The cheap checks come first (method, matched endpoint, path), so an API request never touches the disk; only
        // a front-end request costs one look at the disk, to see whether the front end is built at all (architecture
        // 13, last row: when it is not, every answer stays as before v1.2).
        app.UseWhen(
            context => IsFrontEndRequest(context) && webRoot.GetFileInfo(PageFile).Exists,
            frontEnd =>
            {
                frontEnd.Use(RewriteScreenAddressToPage);
                frontEnd.UseStaticFiles(CreateStaticFileOptions(webRoot));

                // Run adds a final step that never calls the next one, so nothing in this branch reaches the token
                // check: a front-end request gets a file, the page or this 404, never a 401.
                frontEnd.Run(AnswerMissingFileAsync);
            });

        return app;
    }

    /// <summary>
    /// Architecture 13, last row: logs one Warning at start-up when the front end has not been built (no
    /// <c>wwwroot/index.html</c>). The API still starts and serves everything as before v1.2.
    /// </summary>
    /// <remarks>
    /// This is the only warning about it: when the <c>wwwroot</c> folder is missing altogether, <see cref="UseFrontEnd"/>
    /// adds no static-file step, so ASP.NET Core's own "The WebRootPath was not found" warning never appears (decision
    /// #251). Whether there is a web root folder is decided once, at start-up. If <c>wwwroot</c> exists but has no page,
    /// a front end built while the app runs is served from the next request. If the folder did not exist at all, the
    /// front end is served only after a restart.
    /// </remarks>
    public static void WarnIfFrontEndNotBuilt(this WebApplication app)
    {
        if (app.Environment.WebRootFileProvider.GetFileInfo(PageFile).Exists)
        {
            return;
        }

        // A logger named after this class (InventoryTracker.Api.Common.FrontEnd.FrontEndSetup), so the log line says
        // where it comes from. The factory is taken from dependency injection because a static class cannot receive an
        // ILogger<T> through a constructor.
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FrontEndSetup));
        logger.LogWarning(NotBuiltWarning);
    }

    /// <summary>
    /// True when the web root folder exists on disk. <c>WebRootPath</c> is the folder's full path (by default
    /// <c>wwwroot</c> under the app's folder); the host fills it in even when the folder is missing.
    /// </summary>
    private static bool WebRootFolderExists(IWebHostEnvironment environment) =>
        !string.IsNullOrEmpty(environment.WebRootPath) && Directory.Exists(environment.WebRootPath);

    /// <summary>
    /// True for a request the front end may answer: a GET or HEAD that no API endpoint matched, outside <c>/api</c>,
    /// <c>/health</c> and <c>/openapi</c> (PRD row 88: no screen address starts with those). Any other request (a POST,
    /// an API path) is left to the token check and the endpoints, exactly as before v1.2.
    /// </summary>
    private static bool IsFrontEndRequest(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // GetEndpoint() is the API route that routing matched for this request, or null when none did. (WebApplication
        // runs routing at the very start of the pipeline, so the match is already known here.)
        if (context.GetEndpoint() is not null)
        {
            return false;
        }

        // StartsWithSegments compares whole segments and ignores letter case: /api and /API/v1 are API paths, while
        // /apiary and /api.txt are not (PRD row 88: "whole path segments").
        var path = request.Path;
        return !path.StartsWithSegments("/api")
            && !path.StartsWithSegments("/health")
            && !path.StartsWithSegments("/openapi");
    }

    /// <summary>
    /// Step 1 of the branch, the "SPA fallback". The front end is a single-page application: one page, with the screen
    /// chosen by the address (<c>/production-orders/12</c>). Opening or reloading such an address asks the server for
    /// that path, and no file has it, so the server answers with the page instead (AC-36.3: every screen has its own
    /// address; AC-34.1: any address shows sign-in first).
    /// </summary>
    /// <remarks>
    /// Path rewriting: changing <c>Request.Path</c> before the next step makes that step see the new path as if the
    /// browser had asked for it, while the browser's address bar keeps the original one (it is not a redirect). Here the
    /// next step is the static-file step, which then serves <c>/index.html</c> with everything a real file gets: the
    /// Content-Length, an ETag and Last-Modified for "has it changed?" checks, HEAD and range support, and the cache
    /// header (decision #250). The query string (<c>?return=...</c>) is left as it is; the front end reads it.
    /// Once the page has been sent, the original path is put back, so the steps the response passes back through, and
    /// the server's "Request finished" log line, see the address the browser asked for.
    /// </remarks>
    private static async Task RewriteScreenAddressToPage(HttpContext context, RequestDelegate next)
    {
        if (NamesAFile(context.Request.Path))
        {
            await next(context);
            return;
        }

        var askedFor = context.Request.Path;
        context.Request.Path = PagePath;
        try
        {
            await next(context);
        }
        finally
        {
            // finally runs whether the next step finished normally or threw, so the path is always restored.
            context.Request.Path = askedFor;
        }
    }

    /// <summary>
    /// True when the last segment of the path has a dot (<c>/assets/app.js</c>, <c>/favicon.ico</c>): such a path names
    /// a file rather than a screen (architecture 13), so it never gets the page.
    /// </summary>
    private static bool NamesAFile(PathString path)
    {
        var value = path.Value ?? "/";
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return lastSegment.Contains('.');
    }

    /// <summary>
    /// Step 2 of the branch: the settings of ASP.NET Core's static-file step, which answers a GET or HEAD with the file of
    /// that path in the web root, when one exists and its type is known, and otherwise passes the request on.
    /// </summary>
    private static StaticFileOptions CreateStaticFileOptions(IFileProvider webRoot)
    {
        // The type sent in Content-Type comes from the file extension. The built-in table says "text/html" for .html;
        // architecture 13 asks for the character set too, so the browser never has to guess the page's encoding.
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".html"] = "text/html; charset=utf-8";

        return new StaticFileOptions
        {
            FileProvider = webRoot,
            ContentTypeProvider = contentTypes,

            // OnPrepareResponse is a hook the static-file step calls for each file it is about to send, after it has set
            // its own headers and before the response starts, so extra headers can be added here. It also runs for a
            // 304 "not modified" answer, which therefore carries the same cache header.
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = CacheControlFor(file.Context.Request.Path),
        };
    }

    /// <summary>
    /// Architecture 13, cache headers: a year and immutable under <c>/assets/</c>, whose file names carry a content hash;
    /// <c>no-cache</c> for the page (at any screen address, because the path has been rewritten to <c>/index.html</c>
    /// by then) and for every other file. API responses never come here; their <c>no-store</c> is T-037's.
    /// </summary>
    private static string CacheControlFor(PathString path) =>
        path.StartsWithSegments(AssetsFolder) ? AssetCacheControl : PageCacheControl;

    /// <summary>
    /// Step 3 of the branch, reached only when the static-file step found no file: architecture 13, third row. A path
    /// that names a file which does not exist (<c>/assets/missing.js</c>, <c>/favicon.ico</c>) gets the usual <c>404</c>
    /// ProblemDetails <c>not-found</c> "No endpoint matches this path.", with or without a token, and never the page: a
    /// missing script answered with HTML would fail in the browser with a confusing error instead of a plain 404.
    /// </summary>
    private static Task AnswerMissingFileAsync(HttpContext context) =>
        // ProblemResponse fills in the code, the detail, the title and the trace id for a bare 404 (design 6).
        ProblemResponse.WriteAsync(context, new ProblemDetails { Status = StatusCodes.Status404NotFound });
}
