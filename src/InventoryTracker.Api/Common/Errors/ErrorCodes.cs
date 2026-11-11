namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// The <c>code</c> values used by the shared error handling itself (design section 6 and 7.2).
/// </summary>
/// <remarks>
/// <c>code</c> is the stable, kebab-case identifier that client programs and tests match on, while
/// <c>detail</c> is the human-readable message. Feature-specific codes such as <c>already-sent</c> are
/// written next to the rule that raises them in each feature's service; only the codes that this
/// foundation code produces live here.
/// </remarks>
public static class ErrorCodes
{
    /// <summary>Design 6: every plain field check (missing field, too many decimals, bad page size...).</summary>
    public const string Validation = "validation";

    /// <summary>Design 7.2: the record named in the URL does not exist, or no endpoint matches the path.</summary>
    public const string NotFound = "not-found";

    /// <summary>Architecture 4.1 item 8: the path exists but not for this HTTP method.</summary>
    public const string MethodNotAllowed = "method-not-allowed";

    /// <summary>The request body is not JSON at all (wrong or missing <c>Content-Type</c>).</summary>
    public const string UnsupportedMediaType = "unsupported-media-type";

    /// <summary>Architecture 4.1 items 8 and 11: a database constraint stopped a write that the checks missed.</summary>
    public const string ConcurrentChange = "concurrent-change";

    /// <summary>Architecture 4.1 item 8: an exception nobody expected. Details go only to the log.</summary>
    public const string InternalError = "internal-error";
}
