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

    /// <summary>Notification messages (design 7.3).</summary>
    public const int MessageLength = 1000;

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
    /// Stores a recipe amount per unit (6 decimal places, PRD 4.1) or a percentage kept to 6 places, which is far
    /// finer than the 2 places shown (architecture 3.1).
    /// </summary>
    public static PropertyBuilder<TDecimal> IsFineDecimal<TDecimal>(this PropertyBuilder<TDecimal> property) =>
        property.HasPrecision(18, 6);

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
