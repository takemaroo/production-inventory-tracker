namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// Base class for every expected, client-facing error: a request the API refuses on purpose.
/// </summary>
/// <remarks>
/// Services do not build HTTP responses themselves. When a rule is broken they throw one of the
/// subclasses (<see cref="NotFoundException"/>, <see cref="ConflictException"/>,
/// <see cref="BadRequestException"/>, <see cref="ValidationException"/>), and
/// <see cref="ApiExceptionHandler"/> turns it into the ProblemDetails body of design section 6.
/// Throwing also stops the work at once, and because every change runs inside one write transaction
/// (architecture 5.2), nothing is saved: this is what PRD 4.1 calls "rejected".
/// <para>
/// The <see cref="Exception.Message"/> is the same text as <see cref="Detail"/>, so it is readable in the log
/// and in a debugger too.
/// </para>
/// </remarks>
public abstract class ApiException : Exception
{
    /// <summary>Creates the exception with everything the response body needs.</summary>
    /// <param name="statusCode">HTTP status, for example 404.</param>
    /// <param name="code">Stable kebab-case identifier for programs, for example <c>already-sent</c>.</param>
    /// <param name="detail">The complete readable message (design 6: a client can show it alone).</param>
    /// <param name="errors">Messages per field, keyed by camelCase JSON path, or null when no single field is at fault.</param>
    protected ApiException(int statusCode, string code, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
        Errors = errors;
    }

    /// <summary>HTTP status code of the response.</summary>
    public int StatusCode { get; }

    /// <summary>The <c>code</c> field of the response body.</summary>
    public string Code { get; }

    /// <summary>The <c>detail</c> field of the response body.</summary>
    public string Detail { get; }

    /// <summary>The <c>errors</c> field of the response body (field path to messages), or null to leave it out.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
}
