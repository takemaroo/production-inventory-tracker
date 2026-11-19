namespace InventoryTracker.Api.Common.Quantities;

/// <summary>
/// The rounding, decimal-place and maximum rules for quantities (PRD 4.1, design 2.4, decision #80).
/// </summary>
/// <remarks>
/// <para>
/// Quantities are always C# <c>decimal</c>, never <c>double</c>: decimal stores base-10 digits exactly, so
/// 0.1 + 0.2 is exactly 0.3 and 12.5 kg stays 12.5 kg. Floating-point types would introduce tiny binary
/// errors into stock sums and variance figures.
/// </para>
/// <para>
/// This class is the one place the input maximums live (architecture 3.1 "Input maximums"). Every service checks a
/// client's quantity with <see cref="IsWithinMax"/> or <see cref="IsWithinMaxAmountPerUnit"/> and reports it with
/// <see cref="AboveMaxMessage"/> or <see cref="AmountPerUnitAboveMaxMessage"/>, so the limit is never typed twice.
/// </para>
/// </remarks>
public static class Quantity
{
    /// <summary>PRD 4.1: entered quantities have at most 3 decimal places.</summary>
    public const int MaxQuantityDecimals = 3;

    /// <summary>PRD 4.1: recipe amounts per unit have at most 6 decimal places.</summary>
    public const int MaxAmountPerUnitDecimals = 6;

    /// <summary>
    /// Decision #80: the largest quantity a client may send (receipts, ordered and produced quantities, sends,
    /// complementary orders, leftover entered, adjustments, returns): 999,999,999.999, just under one thousand million.
    /// </summary>
    /// <remarks>
    /// Why this number: a quantity column is <c>decimal(18,3)</c>, so it holds 15 digits before the point, up to
    /// 999,999,999,999,999.999 (just under 1e15). The largest number the system computes from input is an amount per
    /// unit times a quantity (the planned amount, AC-13.1, and the required amount, D-9). With both inputs at their
    /// maximum that product is 999,999.999999 × 999,999,999.999 = 999,999,999,998,000.000000001, which is still below
    /// 1e15, so every planned and required amount fits its column. The product has about 24 significant digits, and C#
    /// <c>decimal</c> keeps 28, so the multiplication itself is exact and cannot overflow either.
    /// This is the client limit only. <c>StockLedger.MaxQuantity</c> is the column's full capacity, a separate guard for
    /// computed movements, which can be larger than any single input (decision #81).
    /// </remarks>
    public const decimal MaxQuantity = 999_999_999.999m;

    /// <summary>
    /// Decision #80: the largest amount per unit a recipe line may have (AC-08.2): 999,999.999999, just under one
    /// million. Together with <see cref="MaxQuantity"/> it keeps every planned amount inside <c>decimal(18,3)</c>; see
    /// the remarks there.
    /// </summary>
    public const decimal MaxAmountPerUnit = 999_999.999999m;

    /// <summary>
    /// Design 7.2: the message for a recipe amount above <see cref="MaxAmountPerUnit"/>, under
    /// <c>lines[i].amountPerUnit</c>.
    /// </summary>
    /// <remarks>
    /// The limit is written as literal text rather than through <see cref="MessageFormat.Number"/>: that helper keeps 3
    /// decimal places, so it would print 999,999.999999 as "1,000,000", which is a different (and wrong) limit.
    /// </remarks>
    public const string AmountPerUnitAboveMaxMessage = "Amount per unit must be 999,999.999999 or less.";

    /// <summary>
    /// Decision #80: true when the size of <paramref name="value"/> is at most <see cref="MaxQuantity"/>.
    /// </summary>
    /// <remarks>
    /// It compares the absolute value, so the same check serves signed adjustments (US-12), where -999,999,999.999 is
    /// the lowest accepted value. For quantities that must be above 0 the sign rule runs first, so only the upper limit
    /// matters there.
    /// </remarks>
    public static bool IsWithinMax(decimal value) => Math.Abs(value) <= MaxQuantity;

    /// <summary>
    /// Decision #80: true when the size of a recipe amount per unit is at most <see cref="MaxAmountPerUnit"/>.
    /// </summary>
    /// <remarks>
    /// Amounts must be above 0, a rule checked before this one (decision #75), so in practice only the upper limit is
    /// tested. The absolute value keeps it the same shape as <see cref="IsWithinMax"/>.
    /// </remarks>
    public static bool IsWithinMaxAmountPerUnit(decimal value) => Math.Abs(value) <= MaxAmountPerUnit;

    /// <summary>
    /// Design 7.2: the message for a quantity above <see cref="MaxQuantity"/>, "{fieldLabel} must be 999,999,999.999
    /// or less.", for example <c>AboveMaxMessage("Ordered quantity")</c>.
    /// </summary>
    /// <param name="fieldLabel">
    /// The name the field's other messages already use: "Quantity", "Ordered quantity", "Produced quantity",
    /// "Leftover" or "Requested amount" (design 7.2).
    /// </param>
    /// <remarks>
    /// Literal text for the limit, for the same reason as <see cref="AmountPerUnitAboveMaxMessage"/>: one wording
    /// everywhere, independent of how <see cref="MessageFormat.Number"/> rounds.
    /// </remarks>
    public static string AboveMaxMessage(string fieldLabel) => $"{fieldLabel} must be 999,999,999.999 or less.";

    /// <summary>
    /// Design 7.2: the message for a <em>signed</em> quantity below -<see cref="MaxQuantity"/>, "{fieldLabel} must be
    /// between -999,999,999.999 and 999,999,999.999.". Only adjustments (US-12) accept negative quantities, so only they
    /// use it, for example <c>BelowMinMessage("Quantity")</c>.
    /// </summary>
    /// <param name="fieldLabel">The name the field's other messages already use ("Quantity" for adjustments).</param>
    /// <remarks>
    /// <see cref="IsWithinMax"/> answers for both ends, because it compares the size. The two ends get different
    /// sentences: a value above the maximum reads naturally as "must be 999,999,999.999 or less"
    /// (<see cref="AboveMaxMessage"/>), but saying that to someone who sent -1,000,000,000 would be wrong (their number
    /// <em>is</em> less), so the low end names the whole range instead. Literal text, as for the other messages.
    /// </remarks>
    public static string BelowMinMessage(string fieldLabel) =>
        $"{fieldLabel} must be between -999,999,999.999 and 999,999,999.999.";

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
    /// Architecture 3.1, decisions #58 and #99: the percentage to <em>store</em> is the unrounded value rounded to 6
    /// decimal places, half away from zero. A null percentage (decision #49: the required amount rounded to 0) stays null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the code rounds, not the database.</b> The percentage columns are <c>decimal(28,6)</c> (decision #82), but
    /// SQLite ignores a column's precision: it would store every digit the division produced, for example
    /// 1.498501498501498501498501498 for 15 ÷ 1,001 × 100 (worked example A). Rounding here stores 1.498501, the value a
    /// PostgreSQL <c>numeric(28,6)</c> column would keep too, so both databases hold the same figure.
    /// </para>
    /// <para>
    /// <b>Why it is shared.</b> <see cref="DisplayPercent(decimal)"/> starts with exactly this step, and a completion stores
    /// with it. Written once, the stored value and the shown value can never follow different rules (decision #99).
    /// Whether a usage variance is above the tolerance is still decided from the unrounded value (AC-20.4), never from
    /// this one.
    /// </para>
    /// <para>
    /// Examples: 2.0049996 → 2.005000 (the 7th place, 6, rounds the 6th up); 2.0049994 → 2.004999; −2.0049995 →
    /// −2.005000 (half away from zero, so a negative half goes further from zero, not towards it).
    /// </para>
    /// </remarks>
    /// <param name="percent">The unrounded percentage, or null.</param>
    public static decimal? StoredPercent(decimal? percent)
        => percent is { } value ? StoredPercent(value) : null;

    /// <summary>
    /// The same rule as <see cref="StoredPercent(decimal?)"/>, for a percentage that always has a value (the output
    /// variance, the tolerance at completion). The rule itself is written only here.
    /// </summary>
    /// <param name="percent">The unrounded percentage.</param>
    public static decimal StoredPercent(decimal percent) => Math.Round(percent, 6, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Decision #58, design 2.4 and 6.1: the percentage to <em>show</em> (in an API response, a notification message or
    /// its data) is round(round(value, 6), 2), both steps half away from zero. A null percentage (decision #49: the
    /// required amount rounded to 0, so there is nothing to divide by) stays null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why two steps, not one.</b> Percentages are stored rounded to 6 places (architecture 3.1), and an order response
    /// can only show that stored value rounded to 2. A notification is composed from the unrounded value at completion.
    /// If it rounded straight to 2 places, the two could disagree by 0.01: 2.0049996 is stored as 2.005000, which the
    /// order shows as 2.01, while a direct rounding gives 2.00. Taking the same two steps everywhere means a notification
    /// and the order it is about always show the same figure. For a value that is already stored with 6 places the first
    /// step changes nothing, so the rule gives the same answer whichever of the two it is given.
    /// </para>
    /// <para>
    /// <c>MidpointRounding.AwayFromZero</c> is spelled out because <c>Math.Round</c> on its own rounds a half to the even
    /// neighbour (2.005 would become 2.00); with it, 2.005 becomes 2.01 and -2.005 becomes -2.01 (PRD 4.1).
    /// </para>
    /// <para>
    /// A small negative value such as -0.001 rounds to zero, but a C# <c>decimal</c> keeps the minus sign on that zero
    /// (<c>decimal.IsNegative</c> is true). System.Text.Json and <c>MessageFormat.SignedPercent</c> happen to print it
    /// without the sign today, but the value itself would still be a "negative zero". A zero result is therefore
    /// returned as a plain <c>0.00</c>, so no client can ever receive "-0.00" whichever formatter writes it (the same
    /// aim as decision #42 for messages), and a zero is always written the same way, <c>0.00</c>. Other values keep the
    /// scale rounding gives them (2.1 is written <c>2.1</c>, 2 as <c>2</c>); clients must not rely on trailing zeros
    /// (design 2.4).
    /// </para>
    /// <para>
    /// This is display only. Whether a usage variance is above the tolerance is decided from the unrounded value
    /// (AC-20.4), never from this one.
    /// </para>
    /// </remarks>
    /// <param name="percent">The unrounded or stored percentage, or null.</param>
    public static decimal? DisplayPercent(decimal? percent)
        => percent is { } value ? DisplayPercent(value) : null;

    /// <summary>
    /// The same rule as <see cref="DisplayPercent(decimal?)"/>, for a percentage that always has a value (the output
    /// variance: the ordered quantity is never 0). Having both forms lets the caller keep a plain <c>decimal</c> without
    /// a null check that could never fail. The rule itself is written only here.
    /// </summary>
    /// <param name="percent">The unrounded or stored percentage.</param>
    public static decimal DisplayPercent(decimal percent)
    {
        // Step 1 is exactly the storage rule (decision #99), so a shown value always comes from the value that is stored.
        var stored = StoredPercent(percent);
        var shown = Math.Round(stored, 2, MidpointRounding.AwayFromZero);

        // -0.001 rounds to a zero that still carries the minus sign; return a plain 0.00 instead (see remarks above).
        return shown == 0m ? 0.00m : shown;
    }

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
