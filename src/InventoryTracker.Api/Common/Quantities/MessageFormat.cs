using System.Globalization;

namespace InventoryTracker.Api.Common.Quantities;

/// <summary>
/// Writes numbers inside error and notification messages exactly as design 6.1 specifies, so
/// messages such as "send must be between 975 and 1,015 kg" read the same everywhere.
/// </summary>
/// <remarks>
/// Formatting always uses <see cref="CultureInfo.InvariantCulture"/>. Without it, .NET formats numbers with
/// the server's regional settings, and on a Spanish-language Windows 1015.5 would come out as "1.015,5".
/// Response fields are JSON numbers and are not formatted here; this class is only for message text.
/// </remarks>
public static class MessageFormat
{
    // "#,0.###": thousands separator, at least one integer digit, up to 3 decimals with trailing zeros dropped.
    private const string QuantityPattern = "#,0.###";

    // "#,0.00": percentages always show exactly 2 decimals.
    private const string PercentPattern = "#,0.00";

    /// <summary>
    /// A quantity: comma thousands separator, up to 3 decimals, trailing zeros removed.
    /// 1015 gives "1,015", 974.999 gives "974.999", 0.6 gives "0.6", 0 gives "0".
    /// </summary>
    public static string Number(decimal value)
    {
        var rounded = Quantity.Round3(value);

        // A decimal can hold "negative zero" (for example -0.0001 rounded); it must still read "0".
        return rounded == 0 ? "0" : rounded.ToString(QuantityPattern, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A signed difference (design 6.1): "+29", "-25", with the ASCII hyphen-minus. Zero has no sign: "0".
    /// </summary>
    public static string SignedNumber(decimal value)
    {
        var rounded = Quantity.Round3(value);
        return Sign(rounded) + Number(Math.Abs(rounded));
    }

    /// <summary>
    /// A signed percentage with exactly 2 decimals and the percent sign (design 6.1): 2.9 gives "+2.90%",
    /// -2.5 gives "-2.50%". The value is a percent value, not a fraction (design 2.4: 1.5 means 1.50%).
    /// </summary>
    /// <remarks>
    /// Rounded to 2 places half away from zero (PRD 4.1). The sign follows the rounded value, so the text never
    /// shows "-0.00%"; an exact or rounded zero is written "0.00%". Threshold decisions (AC-20.4) must use the
    /// unrounded value and never this text.
    /// </remarks>
    public static string SignedPercent(decimal percent)
    {
        var rounded = Math.Round(percent, 2, MidpointRounding.AwayFromZero);
        return Sign(rounded) + Math.Abs(rounded).ToString(PercentPattern, CultureInfo.InvariantCulture) + "%";
    }

    private static string Sign(decimal rounded) => rounded switch
    {
        > 0 => "+",
        < 0 => "-",
        _ => "",
    };
}
