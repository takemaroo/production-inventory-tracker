using InventoryTracker.Api.Common.Errors;

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
/// </remarks>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _byPath = [];
    private readonly List<string> _allMessages = [];

    /// <summary>True when at least one message has been added.</summary>
    public bool HasErrors => _allMessages.Count > 0;

    /// <summary>
    /// Design 6: <c>detail</c> holds the complete readable message; several messages are joined with a space.
    /// </summary>
    public string Detail => string.Join(" ", _allMessages);

    /// <summary>Records <paramref name="message"/> against the field at <paramref name="path"/>.</summary>
    public void Add(string path, string message)
    {
        if (!_byPath.TryGetValue(path, out var messages))
        {
            _byPath[path] = messages = [];
        }

        messages.Add(message);
        _allMessages.Add(message);
    }

    /// <summary>The messages per path, in the shape of the response's <c>errors</c> field.</summary>
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
