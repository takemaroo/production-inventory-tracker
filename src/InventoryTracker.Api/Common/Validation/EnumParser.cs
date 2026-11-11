namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Reads enum values (item type, location, status...) that arrive as text in a body or query string
/// (architecture 4.1 item 3).
/// </summary>
/// <remarks>
/// Why not let the JSON serializer or model binding convert them? Their error says only "could not be
/// converted"; the design wants its own message, for example "Type must be RawMaterial or FinishedProduct."
/// So request records declare these fields as <c>string?</c> and services call this class.
/// Rules (design 2.3): any letter case is accepted, only the names are valid, and numbers such as "1" are
/// refused even though .NET's own <c>Enum.TryParse</c> would accept them.
/// </remarks>
public static class EnumParser
{
    /// <summary>
    /// Converts <paramref name="value"/> to <typeparamref name="TEnum"/> when it is one of the names, ignoring case.
    /// </summary>
    /// <returns>False for null, empty, numeric or unknown text.</returns>
    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }

    /// <summary>
    /// The design's message for a missing or unknown value, listing the valid names, for example
    /// "Location must be Warehouse or ProductionStorage." or "status must be Created, InProduction, Completed or Cancelled.".
    /// </summary>
    /// <param name="field">The field as it should appear at the start of the message.</param>
    public static string AllowedValuesMessage<TEnum>(string field)
        where TEnum : struct, Enum
    {
        var names = Enum.GetNames<TEnum>();
        var list = names.Length == 1
            ? names[0]
            : string.Join(", ", names[..^1]) + " or " + names[^1];
        return $"{field} must be {list}.";
    }

    /// <summary>
    /// Parses a required value (a body field). When it is missing or unknown, adds
    /// <see cref="AllowedValuesMessage{TEnum}"/> to <paramref name="errors"/> under <paramref name="path"/> and returns null.
    /// </summary>
    public static TEnum? ParseRequired<TEnum>(string? value, string path, string field, ValidationErrors errors)
        where TEnum : struct, Enum
    {
        if (TryParse(value, out TEnum result))
        {
            return result;
        }

        errors.Add(path, AllowedValuesMessage<TEnum>(field));
        return null;
    }

    /// <summary>
    /// Parses an optional value (a list filter such as <c>?status=Created</c>). Missing or empty means "no
    /// filter" and returns null without an error; an unknown value adds the message (design 2.5: <c>status=Done</c> is <c>400</c>).
    /// </summary>
    public static TEnum? ParseOptional<TEnum>(string? value, string path, string field, ValidationErrors errors)
        where TEnum : struct, Enum
    {
        return string.IsNullOrEmpty(value) ? null : ParseRequired<TEnum>(value, path, field, errors);
    }
}
