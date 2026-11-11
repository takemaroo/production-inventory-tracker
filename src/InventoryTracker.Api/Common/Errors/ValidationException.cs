namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// <c>400</c> that names the fields at fault in <c>errors</c>, keyed by camelCase JSON path
/// (design section 6, for example <c>lines[0].quantity</c>).
/// </summary>
/// <remarks>
/// Normally thrown by <see cref="Validation.ValidationErrors.ThrowIfAny"/>, which collects every message first
/// so the client sees all its mistakes in one response. The code is <c>validation</c> for plain field checks;
/// a rule with its own code that still names fields, such as <c>send-out-of-range</c> (AC-15.6), passes that
/// code instead.
/// </remarks>
public sealed class ValidationException : ApiException
{
    /// <summary>Creates the exception from already collected messages.</summary>
    /// <param name="detail">All messages joined with a space (design 6).</param>
    /// <param name="errors">Messages per JSON path.</param>
    /// <param name="code">The response code; <c>validation</c> unless the rule has its own.</param>
    public ValidationException(string detail, IReadOnlyDictionary<string, string[]> errors, string code = ErrorCodes.Validation)
        : base(StatusCodes.Status400BadRequest, code, detail, errors)
    {
    }

    /// <summary>Shortcut for a single field with a single message, for example <c>("page", "page must be 1 or greater.")</c>.</summary>
    public ValidationException(string path, string message)
        : this(message, new Dictionary<string, string[]> { [path] = [message] })
    {
    }
}
