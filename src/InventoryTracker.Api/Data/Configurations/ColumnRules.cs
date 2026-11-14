using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>
/// The column sizes and number formats shared by every table (architecture 3.1), named once so the
/// configuration classes read as rules ("this is a quantity") rather than as repeated numbers.
/// </summary>
public static class ColumnRules
{
    /// <summary>Unit names: 1 to 50 characters (AC-06.1).</summary>
    public const int UnitNameLength = 50;

    /// <summary>Item names: 1 to 100 characters (design 2.6).</summary>
    public const int ItemNameLength = 100;

    /// <summary>Reasons, notes and item descriptions: up to 500 characters (PRD, design 2.6).</summary>
    public const int TextLength = 500;

    /// <summary>Automatic cancellation notes such as "Parent order completed".</summary>
    public const int CancellationNoteLength = 100;

    // There is deliberately no length for notification messages (decision #63, architecture 3.1 "Text lengths"): a
    // message lists every material of an order, so it has no natural upper bound, and it is never truncated.

    /// <summary>Enum columns hold the enum name; the longest name today is 29 characters.</summary>
    public const int EnumNameLength = 30;

    /// <summary>
    /// Stores a quantity with 3 decimal places (PRD 4.1). <c>decimal(18,3)</c> holds up to 999,999,999,999,999.999.
    /// </summary>
    /// <remarks>
    /// Decimals, never <c>double</c>: a double cannot hold 0.1 exactly, and stock sums would drift. SQLite stores
    /// decimals as exact text; PostgreSQL will use <c>numeric(18,3)</c>.
    /// </remarks>
    public static PropertyBuilder<TDecimal> IsQuantity<TDecimal>(this PropertyBuilder<TDecimal> property) =>
        property.HasPrecision(18, 3);

    /// <summary>
    /// Stores a recipe amount per unit: 6 decimal places (PRD 4.1), at most 999,999.999999 (decision #80), so
    /// <c>decimal(18,6)</c> has room to spare.
    /// </summary>
    public static PropertyBuilder<TDecimal> IsFineDecimal<TDecimal>(this PropertyBuilder<TDecimal> property) =>
        property.HasPrecision(18, 6);

    /// <summary>
    /// Stores a percentage (output and usage variance, the tolerance at completion) kept to 6 decimal places, far finer
    /// than the 2 places shown (architecture 3.1, decision #58), in a <c>decimal(28,6)</c> column (decision #82).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why 28 digits and not 18.</b> A percentage divides a difference by an ordered or required amount, and the
    /// smallest of those is 0.001. Dividing by 0.001 multiplies by 1,000, and the "× 100" of a percentage adds two more
    /// zeros, so a small order can give a huge percentage. Example: ordered 0.001 bag, produced 999,999,999.999 bags
    /// (both allowed by decision #80): output variance 999,999,999.998, percentage 99,999,999,999,800 %, about 1e14.
    /// <c>decimal(18,6)</c> keeps only 12 digits before the point (18 − 6), so PostgreSQL would refuse that value with an
    /// error; <c>decimal(28,6)</c> keeps 22, which covers every value the D-9 formulas can give from capped inputs and is
    /// still within what a C# <c>decimal</c> holds (28 to 29 significant digits).
    /// </para>
    /// <para>
    /// EF Core's SQLite provider declares every decimal column as <c>TEXT</c>, and SQLite never enforces a precision, so
    /// on SQLite this changes nothing at run time. It matters for the model: the migration snapshot records <c>(28, 6)</c>, so a later
    /// PostgreSQL port creates columns wide enough. The code still rounds each value to 6 places before saving it,
    /// because SQLite would otherwise keep every digit the division produced.
    /// </para>
    /// </remarks>
    public static PropertyBuilder<TDecimal> IsPercentage<TDecimal>(this PropertyBuilder<TDecimal> property) =>
        property.HasPrecision(28, 6);

    /// <summary>
    /// Stores an enum as its name ("InProduction") instead of its number (1), so the database is readable and the
    /// enum values can be reordered without changing stored data (architecture 3.1).
    /// </summary>
    /// <remarks>
    /// <c>HasConversion&lt;string&gt;()</c> adds an EF Core <em>value converter</em>: a pair of functions that turn the
    /// C# value into the column value when saving and back again when reading.
    /// </remarks>
    public static PropertyBuilder<TEnum> IsStoredAsName<TEnum>(this PropertyBuilder<TEnum> property) =>
        property.HasConversion<string>().HasMaxLength(EnumNameLength);
}
