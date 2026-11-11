using System.Globalization;

namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Reads the date-only query parameters <c>from</c> and <c>to</c> (design 2.5, architecture 4.1 item 3).
/// </summary>
/// <remarks>
/// Dates are exactly <c>yyyy-MM-dd</c> (for example <c>2026-11-01</c>) and are whole UTC days. Parsing uses the
/// invariant culture so the result never depends on the server's regional settings.
/// </remarks>
public static class DateParser
{
    /// <summary>The only accepted date format.</summary>
    public const string Format = "yyyy-MM-dd";

    /// <summary>Design 7.2: the message when <c>from</c> is later than <c>to</c>.</summary>
    public const string RangeMessage = "'from' must be on or before 'to'.";

    /// <summary>Converts <paramref name="value"/> when it is a real date written exactly as <c>yyyy-MM-dd</c>.</summary>
    public static bool TryParse(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// The message for a value that is not a date, for example "'from' must be a date in the format yyyy-MM-dd.".
    /// The design gives no exact wording for this case; this follows the quoting of <see cref="RangeMessage"/>.
    /// </summary>
    public static string InvalidDateMessage(string parameter) => $"'{parameter}' must be a date in the format {Format}.";

    /// <summary>
    /// Parses an optional date query parameter. Missing or empty returns null with no error; a value in the wrong
    /// format adds <see cref="InvalidDateMessage"/> under the parameter's name and returns null.
    /// </summary>
    public static DateOnly? ParseOptional(string? value, string parameter, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (TryParse(value, out var date))
        {
            return date;
        }

        errors.Add(parameter, InvalidDateMessage(parameter));
        return null;
    }

    /// <summary>
    /// Design 2.5: <c>from</c> later than <c>to</c> is <c>400</c>. Adds <see cref="RangeMessage"/> under
    /// <c>from</c> when both are given and out of order. Equal dates are a valid one-day range.
    /// </summary>
    public static void CheckRange(DateOnly? from, DateOnly? to, ValidationErrors errors)
    {
        if (from is not null && to is not null && from > to)
        {
            errors.Add("from", RangeMessage);
        }
    }
}
