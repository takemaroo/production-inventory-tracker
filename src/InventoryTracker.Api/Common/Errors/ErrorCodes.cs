namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// The <c>code</c> values used by the shared error handling itself (design section 6 and 7.2).
/// </summary>
/// <remarks>
/// <c>code</c> is the stable, kebab-case identifier that client programs and tests match on, while
/// <c>detail</c> is the human-readable message. Feature-specific codes such as <c>already-sent</c> are
/// written next to the rule that raises them in each feature's service; only the codes that this
/// foundation code produces live here, plus the few that several features raise with the same meaning
/// (decision #100), so one spelling serves them all.
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

    /// <summary>
    /// SEC-02: the web server refused the request body because it is over the size limit
    /// (<see cref="Validation.RequestLimits.MaxBodyBytes"/>). Status <c>413</c>.
    /// </summary>
    public const string RequestTooLarge = "request-too-large";

    /// <summary>SEC-02: the request body arrived too slowly, and the web server stopped waiting for it. Status <c>408</c>.</summary>
    public const string RequestTimeout = "request-timeout";

    /// <summary>
    /// SEC-01: a body has more lines than <see cref="Validation.RequestLimits.MaxLines"/>. Status <c>400</c>, raised by
    /// recipes, sends, completions, complementary requests and complementary sends.
    /// </summary>
    public const string TooManyLines = "too-many-lines";

    /// <summary>Architecture 4.1 items 8 and 11: a database constraint stopped a write that the checks missed.</summary>
    public const string ConcurrentChange = "concurrent-change";

    /// <summary>
    /// Design 7.2, AC-15.9, AC-19.5, AC-21.5: an action on a Cancelled production order ("This order is cancelled.").
    /// Raised by the send, the completion and the cancellation (decision #100).
    /// </summary>
    public const string OrderCancelled = "order-cancelled";

    /// <summary>
    /// Design 7.2, AC-12.5, AC-22.3, decision #11: a material on the order that is currently InProduction cannot be touched
    /// in production storage. Raised by a production-storage adjustment and by a return instruction, each with its own
    /// sentence (decision #100: one spelling for a code several features raise).
    /// </summary>
    public const string MaterialInUse = "material-in-use";

    /// <summary>Architecture 4.1 item 8: an exception nobody expected. Details go only to the log.</summary>
    public const string InternalError = "internal-error";
}
