using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// Builds and writes the error body of design section 6, so every error path (exceptions, unreadable
/// bodies, bodiless 404/405 responses) produces exactly the same shape.
/// </summary>
/// <remarks>
/// ProblemDetails is the RFC 9457 standard JSON error object (<c>type</c>, <c>title</c>, <c>status</c>,
/// <c>detail</c>), sent with <c>Content-Type: application/problem+json</c>. ASP.NET Core writes it through
/// its <see cref="IProblemDetailsService"/>, which fills in <c>type</c> and calls
/// <see cref="ApplyConventions"/> (registered in <see cref="ErrorHandlingSetup"/>) just before writing. That hook
/// adds what this API puts on top of the standard: the fixed <c>title</c> per status, the stable <c>code</c>,
/// and <c>traceId</c> (the id that also appears in the log line for this request, so a reported error can be
/// found in the log).
/// </remarks>
public static class ProblemResponse
{
    /// <summary>Name of the extension field holding the stable error code.</summary>
    public const string CodeField = "code";

    /// <summary>Name of the extension field holding the request's trace id.</summary>
    public const string TraceIdField = "traceId";

    /// <summary>Name of the field holding the messages per JSON path (design 6, validation errors).</summary>
    public const string ErrorsField = "errors";

    // Design 6: "title is fixed per status". Statuses not listed keep the framework's default title.
    // 408 and 413 are new with SEC-02 (bodies the web server refuses); their titles are routed to the ux-designer.
    private static readonly Dictionary<int, string> FixedTitles = new()
    {
        [StatusCodes.Status400BadRequest] = "Invalid request",
        [StatusCodes.Status401Unauthorized] = "Not signed in",
        [StatusCodes.Status403Forbidden] = "Not allowed",
        [StatusCodes.Status404NotFound] = "Not found",
        [StatusCodes.Status408RequestTimeout] = "Request timeout",
        [StatusCodes.Status409Conflict] = "Conflict with current state",
        [StatusCodes.Status413PayloadTooLarge] = "Request too large",
    };

    // Responses the framework produces without our code (no endpoint, wrong method, a bare status result).
    // Architecture 4.1 item 8 gives the 404, 405 and 500 wording.
    // Because of these defaults, controllers and services must throw an ApiException (NotFoundException,
    // ValidationException...) and never return a bare NotFound() or BadRequest(): a bare 404 would reach the
    // client as "No endpoint matches this path." with no record named, which is misleading.
    private static readonly Dictionary<int, (string Code, string? Detail)> FrameworkDefaults = new()
    {
        [StatusCodes.Status400BadRequest] = (ErrorCodes.Validation, null),
        [StatusCodes.Status404NotFound] = (ErrorCodes.NotFound, "No endpoint matches this path."),
        [StatusCodes.Status405MethodNotAllowed] = (ErrorCodes.MethodNotAllowed, "This method is not allowed on this path."),
        [StatusCodes.Status415UnsupportedMediaType] =
            (ErrorCodes.UnsupportedMediaType, "The request body must be JSON, sent with Content-Type: application/json."),
        [StatusCodes.Status500InternalServerError] = (ErrorCodes.InternalError, "An unexpected error occurred."),
    };

    /// <summary>
    /// Creates the body for one error. With <paramref name="errors"/> the body carries the <c>errors</c> field;
    /// without, the field is left out.
    /// </summary>
    public static ProblemDetails Create(int status, string code, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails { Status = status, Detail = detail };
        problem.Extensions[CodeField] = code;

        // "errors" is stored as an extension rather than by using the framework's HttpValidationProblemDetails
        // class. On an [ApiController] endpoint, MVC's writer rebuilds the body as a plain ProblemDetails and
        // copies only the extensions, so the class's own Errors property would silently disappear. The JSON a
        // client receives is the same either way: { ..., "errors": { "quantity": ["..."] } }.
        if (errors is not null)
        {
            problem.Extensions[ErrorsField] = errors.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        return problem;
    }

    /// <summary>
    /// Writes <paramref name="problem"/> as the response: sets the status code, then lets
    /// <see cref="IProblemDetailsService"/> serialize it as <c>application/problem+json</c>.
    /// </summary>
    public static async Task WriteAsync(HttpContext httpContext, ProblemDetails problem)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        var written = await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });

        // The service declines only when the client's Accept header rules out JSON. This API has no other
        // format to offer, so it answers in JSON anyway rather than with an empty body. The service would
        // normally fill in "type", so it is filled in here the same way.
        if (!written)
        {
            ApplyConventions(problem, httpContext);
            problem.Type ??= DefaultType(httpContext, problem.Status!.Value);
            await httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
        }
    }

    /// <summary>
    /// The framework's default <c>type</c> link for a status, for example
    /// <c>https://tools.ietf.org/html/rfc9110#section-15.5.5</c> for 404 (design 6: "type is left at the framework
    /// default"). The table is MVC's <c>ApiBehaviorOptions.ClientErrorMapping</c>, read through DI.
    /// </summary>
    private static string? DefaultType(HttpContext httpContext, int status)
    {
        var mapping = httpContext.RequestServices.GetService<IOptions<ApiBehaviorOptions>>()?.Value.ClientErrorMapping;
        return mapping is not null && mapping.TryGetValue(status, out var data) ? data.Link : null;
    }

    /// <summary>
    /// Wraps <paramref name="problem"/> in an MVC action result, for the places where MVC expects one
    /// (the invalid-body response factory). It writes through <see cref="WriteAsync"/> like everything else.
    /// </summary>
    public static IActionResult AsActionResult(ProblemDetails problem) => new ProblemActionResult(problem);

    /// <summary>
    /// Adds this API's conventions to any ProblemDetails just before it is written: the fixed title for the
    /// status, a default <c>code</c> and <c>detail</c> for framework-generated errors, and the trace id.
    /// </summary>
    /// <remarks>Values already set by our own code (<c>code</c>, <c>detail</c>) are never overwritten.</remarks>
    public static void ApplyConventions(ProblemDetails problem, HttpContext httpContext)
    {
        var status = problem.Status ?? httpContext.Response.StatusCode;
        problem.Status = status;

        if (FixedTitles.TryGetValue(status, out var title))
        {
            problem.Title = title;
        }

        if (!problem.Extensions.ContainsKey(CodeField) && FrameworkDefaults.TryGetValue(status, out var defaults))
        {
            problem.Extensions[CodeField] = defaults.Code;
            problem.Detail ??= defaults.Detail;
        }

        // Activity.Current is the distributed-tracing context of this request (W3C trace id); it is what the
        // framework itself would use, so the id matches the request's log scope.
        problem.Extensions.TryAdd(TraceIdField, Activity.Current?.Id ?? httpContext.TraceIdentifier);
    }

    /// <summary>MVC action result that writes a ProblemDetails body through <see cref="WriteAsync"/>.</summary>
    private sealed class ProblemActionResult(ProblemDetails problem) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => WriteAsync(context.HttpContext, problem);
    }
}
