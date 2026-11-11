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
/// <item>Anything else: <c>500</c> <c>internal-error</c> "An unexpected error occurred." The exception, with its
/// stack trace, goes only to the log; the body never contains it (design 6).</item>
/// </list>
/// </remarks>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Design 7.2, "Lists and general": the concurrency fallback message.</summary>
    public const string ConcurrentChangeMessage =
        "The data changed while your request was being processed. Read it again and retry.";

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
