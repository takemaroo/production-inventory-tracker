using InventoryTracker.Api.Common.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// Turns every exception that escapes a request into the ProblemDetails body of design section 6.
/// </summary>
/// <remarks>
/// <c>IExceptionHandler</c> is ASP.NET Core's hook for central error handling. <c>app.UseExceptionHandler()</c>
/// in Program.cs adds a middleware step that wraps the rest of the pipeline in a try/catch; when something
/// throws, it clears the half-written response and calls this class. That is why services can simply
/// <c>throw</c> and never build an error response themselves.
/// <list type="bullet">
/// <item><see cref="ApiException"/>: an expected refusal. Its status, code, detail and errors are used as they are.
/// Not logged as an error, because nothing went wrong in the application.</item>
/// <item>A <see cref="DbUpdateException"/> caused by a unique index or primary key: <c>409</c>
/// <c>concurrent-change</c> (architecture 4.1 item 11 and section 6), logged as a warning. Other database
/// failures (NOT NULL, CHECK, FOREIGN KEY, a busy database) are bugs or outages, not races a retry would fix,
/// so they are treated like any other unexpected exception.</item>
/// <item>A <see cref="BadHttpRequestException"/>: the web server could not accept the request body (SEC-02). It is
/// over the size limit (<c>413</c> <c>request-too-large</c>), it arrived too slowly (<c>408</c> <c>request-timeout</c>),
/// or it is broken, for example a malformed chunked body or a connection that ended early (<c>400</c>
/// <c>validation</c>). This is the client's fault, not a bug, so it is logged as one Information line without the stack
/// trace: anyone can send such requests without signing in, and they must not fill the log with errors.</item>
/// <item>Anything else: <c>500</c> <c>internal-error</c> "An unexpected error occurred." The exception, with its
/// stack trace, goes only to the log; the body never contains it (design 6).</item>
/// </list>
/// </remarks>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Design 7.2, "Lists and general": the concurrency fallback message.</summary>
    public const string ConcurrentChangeMessage =
        "The data changed while your request was being processed. Read it again and retry.";

    /// <summary>SEC-02: a body that arrived too slowly (new wording, routed to the ux-designer).</summary>
    public const string RequestTimeoutMessage = "The request body arrived too slowly. Send it again.";

    /// <summary>SEC-02: a body the web server could not read, such as a malformed chunked body (new wording).</summary>
    public const string UnreadableBodyMessage = "The request body could not be read.";

    /// <summary>Architecture 4.1 item 8: the only text a client ever sees for an unexpected error.</summary>
    public const string InternalErrorMessage = "An unexpected error occurred.";

    // SQLite "extended" result codes. The primary code 19 (SQLITE_CONSTRAINT) covers every kind of constraint;
    // the extended code says which one. Only these two mean "another request wrote the same key first".
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>Writes the error response for <paramref name="exception"/>. Always handles it (returns true).</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ApiException api => ProblemResponse.Create(api.StatusCode, api.Code, api.Detail, api.Errors),
            DbUpdateException update when IsUniquenessViolation(update) => ConcurrentChange(httpContext, update),
            BadHttpRequestException badRequest => RefusedBody(httpContext, badRequest),
            _ => Unexpected(httpContext, exception),
        };

        await ProblemResponse.WriteAsync(httpContext, problem);
        return true;
    }

    private ProblemDetails ConcurrentChange(HttpContext httpContext, DbUpdateException exception)
    {
        // Architecture 6, Logging: concurrent-change is a Warning. With the write-transaction rule (5.2) it
        // should never happen, so each occurrence is worth a look.
        logger.LogWarning(exception, "A unique index rejected a write: {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);
        return ProblemResponse.Create(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentChange, ConcurrentChangeMessage);
    }

    /// <summary>
    /// SEC-02: Kestrel (the web server inside ASP.NET Core) throws <see cref="BadHttpRequestException"/> when our code
    /// starts reading a body it will not deliver. Model binding reads the body before the action runs, so this happens
    /// before any service, and even on sign-in, which needs no token. The exception's own <c>StatusCode</c> says why.
    /// </summary>
    private ProblemDetails RefusedBody(HttpContext httpContext, BadHttpRequestException exception)
    {
        var (status, code, detail) = exception.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge =>
                (StatusCodes.Status413PayloadTooLarge, ErrorCodes.RequestTooLarge, RequestLimits.BodyTooLargeMessage),
            StatusCodes.Status408RequestTimeout =>
                (StatusCodes.Status408RequestTimeout, ErrorCodes.RequestTimeout, RequestTimeoutMessage),

            // Every other refusal of a body is a 400 from Kestrel (bad chunk, unexpected end of the body). Design 6
            // words a body that cannot be read as a whole-body validation error with no "errors" field (decision #41).
            _ => (StatusCodes.Status400BadRequest, ErrorCodes.Validation, UnreadableBodyMessage),
        };

        // Information, and the exception's message only: no stack trace, which made each such request about 27 log
        // lines at Error level (SEC-02). Kestrel's message names no user data, for example "Request body too large.
        // The max request body size is 1000000 bytes."
        logger.LogInformation("Request body refused with {Status}: {Method} {Path}. {Reason}",
            status, httpContext.Request.Method, httpContext.Request.Path, exception.Message);
        return ProblemResponse.Create(status, code, detail);
    }

    private ProblemDetails Unexpected(HttpContext httpContext, Exception exception)
    {
        // Only the method and path are logged next to the exception, never the body (it may hold passwords).
        logger.LogError(exception, "Unhandled exception: {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);
        return ProblemResponse.Create(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, InternalErrorMessage);
    }

    /// <summary>
    /// True when the database refused the write because a unique index or primary key already holds the value,
    /// for example the unique index that allows only one InProduction order (AC-15.10 backstop). That can only
    /// happen when another request got there first, so "read it again and retry" is the right advice.
    /// </summary>
    /// <remarks>
    /// EF Core wraps the provider's own exception, so the provider exception is in <c>InnerException</c>.
    /// A move to PostgreSQL (PRD "Later") would add a check for <c>PostgresException</c> with
    /// <c>SqlState</c> "23505" (unique_violation) here.
    /// </remarks>
    private static bool IsUniquenessViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        };
}
