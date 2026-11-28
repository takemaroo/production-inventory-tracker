namespace InventoryTracker.Api.Common.Security;

/// <summary>
/// (v1.2) The security headers that every response carries, pages, files, API answers, errors and <c>/health</c> alike
/// (PRD A-2, AC-37.5, AC-37.6; architecture 14, decision #228). They tell the browser what the page may load and do, so a
/// mistake in the front end, or a hostile name stored in the database, cannot turn into script running in the page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-Security-Policy (CSP)</b> is a list of rules the browser enforces for a page: from where it may load
/// scripts, styles, images, fonts and data, and what it may not do at all. With <c>script-src 'self'</c> the browser runs
/// only script files fetched from this server; an inline <c>&lt;script&gt;</c> block, an <c>onclick="..."</c> attribute
/// or <c>eval</c> is refused. So even if a stored text such as <c>&lt;img src=x onerror=alert(1)&gt;</c> ever reached
/// the page as HTML, its script would not run (AC-37.1, AC-37.5).
/// </para>
/// <para>
/// <b>Trusted Types</b> are the CSP's last two rules. <c>require-trusted-types-for 'script'</c> makes the browser refuse
/// any plain string assigned to an HTML "sink" (<c>innerHTML</c>, <c>outerHTML</c>, <c>insertAdjacentHTML</c>,
/// <c>document.write</c> and similar); only a "trusted" object made by a named policy would be accepted, and
/// <c>trusted-types 'none'</c> allows no policy at all. So raw-HTML insertion is not only forbidden by review (AC-37.2), the
/// browser itself throws an error when it is tried. React renders text without these sinks, which is why the built front
/// end still works under this policy (architecture 14: verified in Edge).
/// </para>
/// <para>
/// <b>No CORS</b> (A-3, AC-37.7, decision #229). A browser lets a page read a response from another origin (another
/// scheme, host or port) only when that response says so with <c>Access-Control-Allow-Origin</c>; that permission is
/// what "CORS" (cross-origin resource sharing) configures. The front end is served from the API's own address (Q104), so
/// it never needs that permission, and the API grants it to nobody: there is deliberately no CORS code anywhere, so a page
/// on any other site cannot read API answers. <c>SecurityHeadersTests</c> pins this.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>
    /// Architecture 14, word for word: the one CSP for every response (decision #228). Each directive, in order:
    /// everything defaults to this server only; scripts, styles, images, fonts and API calls (<c>connect-src</c>) come from
    /// this server only; no plug-ins (<c>object-src</c>); no <c>&lt;base&gt;</c> element that could redirect relative
    /// addresses; no other site may show the page in a frame (against click-jacking); forms submit only here; and Trusted
    /// Types with no policy, so HTML sinks refuse strings. There is no <c>'unsafe-inline'</c> or <c>'unsafe-eval'</c>
    /// anywhere: the front end has no inline script or style (design 17, the source guard).
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "require-trusted-types-for 'script'; trusted-types 'none'";

    /// <summary>
    /// AC-37.6, SEC-11: <c>X-Content-Type-Options: nosniff</c> stops the browser from guessing a response's type from its
    /// content. Without it, a JSON answer holding text that looks like HTML or script could be treated as such.
    /// </summary>
    public const string NoSniff = "nosniff";

    /// <summary>
    /// AC-37.6: <c>Referrer-Policy: no-referrer</c>. The browser normally tells the next site which address the user came
    /// from; screen addresses hold record numbers, so they are never sent anywhere.
    /// </summary>
    public const string NoReferrer = "no-referrer";

    /// <summary>
    /// AC-37.6, SEC-11: <c>Cache-Control</c> for every response under <c>/api</c>. "no-store" tells the browser and any
    /// proxy never to keep a copy, so stock figures, names and tokens are not left on disk after the session ends.
    /// </summary>
    public const string ApiCacheControl = "no-store";

    /// <summary>The API's path prefix, compared by whole segments (as in <c>FrontEndSetup</c>, PRD row 88).</summary>
    private static readonly PathString ApiPath = new("/api");

    /// <summary>
    /// Adds the middleware step that puts the headers on every response. Program.cs calls it first, before the exception
    /// handler and the front end, so that no response, not even an error or a front-end file, can leave without them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why first: the front end's branch (<c>UseFrontEnd</c>) answers its page, files and <c>404</c> itself and never
    /// calls the later steps, so a step placed after it would miss them (architecture 13, decision #227).
    /// </para>
    /// <para>
    /// <c>Response.OnStarting</c> registers a callback that ASP.NET Core runs at the last moment, just before the status
    /// line and headers go out to the client. Setting the headers there, rather than now, means they are set after every
    /// later step has done its work: the exception handler clears the headers of a failed request before writing its error
    /// body, but it keeps these callbacks, so a <c>500</c> gets the headers too. Callbacks run in the reverse order of
    /// registration, and this one is registered first, so it runs last and its values win (for example our
    /// <c>no-store</c> over the exception handler's own cache header).
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Decided now, from the path the client asked for: a later step may rewrite Request.Path (the front end
            // rewrites a screen address to /index.html), and the rule is about the address, not the file served.
            var isApi = context.Request.Path.StartsWithSegments(ApiPath);

            context.Response.OnStarting(() =>
            {
                SetHeaders(context.Response, isApi);
                return Task.CompletedTask;
            });

            await next(context);
        });

    /// <summary>
    /// Sets the headers of architecture 14, replacing any value an earlier step gave them. Outside <c>/api</c> the
    /// <c>Cache-Control</c> header is left alone, so the front end keeps T-036's <c>no-cache</c> on the page and the
    /// year-long cache on its hashed files (architecture 13).
    /// </summary>
    private static void SetHeaders(HttpResponse response, bool isApi)
    {
        var headers = response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = NoSniff;
        headers["Referrer-Policy"] = NoReferrer;

        if (isApi)
        {
            headers.CacheControl = ApiCacheControl;
        }
    }
}
