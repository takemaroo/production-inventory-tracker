namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Reads a true/false query filter that arrives as text, such as <c>?unreadOnly=true</c> on notifications or (v1.1)
/// <c>?isActive=false</c> on customers (architecture 4.1 item 19, section 6 Validation; decision #161).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a <c>bool</c> parameter?</b> If an action declared <c>bool? isActive</c>, model binding would convert the
/// text itself, and a wrong value such as <c>yes</c> would get the framework's generic "The value 'yes' is not valid."
/// The design wants its own sentence, in the style of its enum filter messages ("isActive must be true or false."), and
/// wants it reported together with any paging errors in one <c>400</c>. So the action takes the value as a
/// <c>string?</c> and the service calls this class with its <see cref="ValidationErrors"/> collector.
/// </para>
/// <para>
/// The rules were first written for <c>unreadOnly</c> (decision #102a) and now live here, in one place, so the two
/// filters can never drift apart: exactly <c>true</c> or <c>false</c>, in any letter case; missing, empty or blank means
/// "not set"; anything else, including surrounding spaces (<c>" true"</c>), numbers (<c>1</c>) and other words
/// (<c>yes</c>), is refused.
/// </para>
/// </remarks>
public static class BoolParser
{
    /// <summary>
    /// Design 7.2: the <c>400</c> message for a value that is not <c>true</c> or <c>false</c>, for example
    /// "isActive must be true or false.". <paramref name="parameter"/> is the query parameter's name as the client
    /// writes it.
    /// </summary>
    public static string Message(string parameter) => $"{parameter} must be true or false.";

    /// <summary>
    /// Parses an optional true/false filter. Returns <c>true</c> or <c>false</c> for those words in any letter case, and
    /// <c>null</c> ("not set") when the value is missing, empty or only spaces. Anything else adds
    /// <see cref="Message"/> to <paramref name="errors"/> under <paramref name="parameter"/> and returns <c>null</c>, so
    /// the caller can go on collecting other problems before it throws.
    /// </summary>
    /// <param name="value">The raw query value, as model binding passed it (it passes an all-space value as null).</param>
    /// <param name="parameter">The query parameter's name, used as the <c>errors</c> key and at the start of the message.</param>
    /// <param name="errors">The request's collector, shared with the paging checks.</param>
    public static bool? ParseOptional(string? value, string parameter, ValidationErrors errors)
    {
        // "Not set" is the caller's to interpret: notifications read it as false (show all), customers as "no filter".
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // An exact comparison that ignores only letter case. bool.TryParse is not used because it also accepts
        // surrounding spaces (" true "), which no other filter accepts (decision #102a).
        if (string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, bool.FalseString, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        errors.Add(parameter, Message(parameter));
        return null;
    }
}
