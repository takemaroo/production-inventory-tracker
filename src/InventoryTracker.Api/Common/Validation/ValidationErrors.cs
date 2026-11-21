using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;

namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Collects field validation messages so a service can report every mistake in a request at once,
/// then throw them as one <see cref="ValidationException"/> (architecture 6, Validation).
/// </summary>
/// <remarks>
/// Typical use in a service:
/// <code>
/// var errors = new ValidationErrors();
/// if (request.Quantity &lt;= 0) errors.Add("quantity", "Quantity must be greater than 0.");
/// if (Text.Clean(request.Reason) is null) errors.Add("reason", "Reason is required.");
/// errors.ThrowIfAny();
/// </code>
/// Paths are camelCase JSON paths as the client sent them, for example <c>lines[0].quantity</c>
/// (design section 6). Messages keep the order in which they were added, both in <c>errors</c> and in
/// <c>detail</c>.
/// <para>
/// <b>At most <see cref="MaxMessages"/> messages are kept</b> (security review SEC-01). Without a limit, a body with a
/// million bad lines produced two million messages, each written twice in the response (once in <c>detail</c>, once in
/// <c>errors</c>), and the answer grew to hundreds of megabytes. Messages after the first hundred are only counted, and
/// <c>detail</c> ends with one more sentence saying how many were left out ("1,900 more problems are not shown."). The
/// client still learns that the request is wrong and sees the first problems to fix.
/// </para>
/// </remarks>
public sealed class ValidationErrors
{
    /// <summary>
    /// The most messages one response lists (SEC-01). 100 is far more than a person fixes in one go, and keeps the
    /// largest possible error response to a few tens of kilobytes.
    /// </summary>
    public const int MaxMessages = 100;

    private readonly Dictionary<string, List<string>> _byPath = [];
    private readonly List<string> _allMessages = [];
    private int _notShown;

    /// <summary>True when at least one message has been added.</summary>
    public bool HasErrors => _allMessages.Count > 0;

    /// <summary>
    /// Design 6: <c>detail</c> holds the complete readable message; several messages are joined with a space. When
    /// messages were left out (more than <see cref="MaxMessages"/>), a last sentence says how many.
    /// </summary>
    public string Detail => _notShown == 0
        ? string.Join(" ", _allMessages)
        : string.Join(" ", _allMessages) + " " + NotShownMessage(_notShown);

    /// <summary>
    /// The sentence that ends <c>detail</c> when <paramref name="count"/> messages were left out: "1 more problem is not
    /// shown." or "1,900 more problems are not shown." (design 7.2, decision #122; the number is formatted as in
    /// design 6.1).
    /// </summary>
    public static string NotShownMessage(int count) => count == 1
        ? "1 more problem is not shown."
        : $"{MessageFormat.Number(count)} more problems are not shown.";

    /// <summary>
    /// Records <paramref name="message"/> against the field at <paramref name="path"/>. Once <see cref="MaxMessages"/>
    /// messages are held, later ones are only counted.
    /// </summary>
    public void Add(string path, string message)
    {
        if (_allMessages.Count >= MaxMessages)
        {
            _notShown++;
            return;
        }

        if (!_byPath.TryGetValue(path, out var messages))
        {
            _byPath[path] = messages = [];
        }

        messages.Add(message);
        _allMessages.Add(message);
    }

    /// <summary>
    /// The messages per path, in the shape of the response's <c>errors</c> field. Only the messages that were kept; the
    /// "not shown" sentence is in <see cref="Detail"/> only, because it belongs to no single field.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _byPath.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

    /// <summary>
    /// Throws a <c>400</c> <see cref="ValidationException"/> with every collected message, or does nothing when
    /// there are none.
    /// </summary>
    /// <param name="code">
    /// The response <c>code</c>. Plain field checks use the default <c>validation</c>; a rule with its own code
    /// that still lists fields passes it, for example <c>send-out-of-range</c> (AC-15.6).
    /// </param>
    public void ThrowIfAny(string code = ErrorCodes.Validation)
    {
        if (HasErrors)
        {
            throw new ValidationException(Detail, ToDictionary(), code);
        }
    }
}
