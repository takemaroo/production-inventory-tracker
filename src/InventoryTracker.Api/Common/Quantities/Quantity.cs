namespace InventoryTracker.Api.Common.Quantities;

/// <summary>
/// The rounding and decimal-place rules for quantities (PRD 4.1, design 2.4).
/// </summary>
/// <remarks>
/// Quantities are always C# <c>decimal</c>, never <c>double</c>: decimal stores base-10 digits exactly, so
/// 0.1 + 0.2 is exactly 0.3 and 12.5 kg stays 12.5 kg. Floating-point types would introduce tiny binary
/// errors into stock sums and variance figures.
/// </remarks>
public static class Quantity
{
    /// <summary>PRD 4.1: entered quantities have at most 3 decimal places.</summary>
    public const int MaxQuantityDecimals = 3;

    /// <summary>PRD 4.1: recipe amounts per unit have at most 6 decimal places.</summary>
    public const int MaxAmountPerUnitDecimals = 6;

    /// <summary>
    /// PRD 4.1: computed quantities (planned amount, range limits, required amount) are rounded to 3 decimal
    /// places, half away from zero. So 1.0005 becomes 1.001 and -1.0005 becomes -1.001.
    /// </summary>
    /// <remarks>
    /// <c>Math.Round</c> on its own uses "banker's rounding" (half to even: 1.0005 would become 1.000), which is
    /// why the midpoint rule is spelled out.
    /// </remarks>
    public static decimal Round3(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    /// <summary>
    /// PRD 4.1, design 2.4: true when <paramref name="value"/> has no more than <paramref name="places"/>
    /// significant decimal places. Trailing zeros do not count, so 12.5000 passes with 3 places, and
    /// 0.0001 fails.
    /// </summary>
    /// <remarks>
    /// A decimal remembers how many digits were written (12.5000 is stored with scale 4), so counting digits
    /// would wrongly reject 12.5000. Rounding to the limit and comparing instead asks the real question:
    /// "would rounding change the value?". Decimal equality ignores trailing zeros (12.5000 == 12.5).
    /// </remarks>
    public static bool HasAtMostDecimals(decimal value, int places) => Math.Round(value, places) == value;
}
