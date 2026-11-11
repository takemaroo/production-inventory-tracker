using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>
/// Makes every <see cref="DateTime"/> read from the database come back marked as UTC (architecture 3.1, decision #27).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DateTime"/> carries a <see cref="DateTimeKind"/>: Utc, Local or Unspecified. SQLite stores the
/// value as text without that flag, so EF Core would read every time back as Unspecified. JSON would then write
/// it without the <c>Z</c> suffix, and clients could not tell it is UTC (design 2.3).
/// </para>
/// <para>
/// This value converter is applied to every <see cref="DateTime"/> property in <c>AppDbContext.ConfigureConventions</c>.
/// Reading: the stored value is UTC by rule, so it is only labelled as such. Writing: a Utc or Unspecified value
/// is stored as it is; a Local value is converted to UTC first, so a mistake cannot store local time silently.
/// Services get times from <c>Clock</c> (T-001), which is already UTC.
/// </para>
/// </remarks>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    /// <summary>Creates the converter. EF Core calls this; application code does not.</summary>
    public UtcDateTimeConverter()
        : base(
            toDatabase => toDatabase.Kind == DateTimeKind.Local ? toDatabase.ToUniversalTime() : toDatabase,
            fromDatabase => DateTime.SpecifyKind(fromDatabase, DateTimeKind.Utc))
    {
    }
}
