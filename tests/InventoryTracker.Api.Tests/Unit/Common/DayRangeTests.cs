using System.Globalization;
using InventoryTracker.Api.Common.Validation;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// (v1.1) T-026, decision #161: the whole-UTC-day bounds of the <c>from</c>/<c>to</c> filters (design 2.5, AC-11.2),
/// moved out of <c>MovementService</c> with no change in behaviour. The movement date-filter tests in
/// <c>MovementsTests</c> prove the same rules end to end; these pin the helper itself, for the reports (T-030) too.
/// </summary>
public class DayRangeTests
{
    /// <summary>
    /// Design 2.5: <c>from</c> includes its whole day, so the bound is 00:00:00 UTC of that day, marked UTC like every
    /// stored time (architecture 3.1). Covers an ordinary day, the last day of a month and of a year, a leap day, and the
    /// first date .NET can hold.
    /// </summary>
    [Theory]
    [InlineData(2026, 11, 11)]
    [InlineData(2026, 11, 30)]
    [InlineData(2026, 12, 31)]
    [InlineData(2028, 2, 29)]
    [InlineData(1, 1, 1)]
    public void Start_is_midnight_utc_of_the_day(int year, int month, int day)
    {
        var start = DayRange.Start(new DateOnly(year, month, day));

        Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    /// <summary>
    /// Design 2.5: <c>to</c> includes its whole day, so the exclusive bound is 00:00:00 UTC of the next day, across a
    /// month end, a year end and a leap day. Marked UTC.
    /// </summary>
    [Theory]
    [InlineData("2026-11-11", "2026-11-12")]
    [InlineData("2026-11-30", "2026-12-01")]
    [InlineData("2026-12-31", "2027-01-01")]
    [InlineData("2028-02-28", "2028-02-29")]
    [InlineData("2028-02-29", "2028-03-01")]
    [InlineData("9999-12-30", "9999-12-31")]
    public void End_exclusive_is_midnight_utc_of_the_next_day(string day, string nextDay)
    {
        var end = DayRange.EndExclusive(Date(day));

        Assert.Equal(Date(nextDay).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), end);
        Assert.Equal(DateTimeKind.Utc, end!.Value.Kind);
    }

    /// <summary>
    /// Decision #89: 9999-12-31 has no next day (<c>AddDays(1)</c> would throw, a <c>500</c>), so it has no upper bound:
    /// <c>null</c>, and the caller adds no condition.
    /// </summary>
    [Fact]
    public void The_last_possible_day_has_no_upper_bound()
    {
        Assert.Null(DayRange.EndExclusive(DateOnly.MaxValue));
        Assert.Null(DayRange.EndExclusive(new DateOnly(9999, 12, 31)));
    }

    /// <summary>
    /// AC-11.2 "both included": the first and last instants of a one-day range fall inside it (00:00:00 and
    /// 23:59:59.9999999), and the next midnight does not. This is the reason for "before the next midnight" rather than
    /// "at or before 23:59:59".
    /// </summary>
    [Fact]
    public void A_one_day_range_holds_every_instant_of_that_day_and_nothing_else()
    {
        var day = new DateOnly(2026, 12, 31);
        var start = DayRange.Start(day);
        var end = DayRange.EndExclusive(day)!.Value;

        bool Inside(DateTime instant) => instant >= start && instant < end;

        Assert.True(Inside(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(Inside(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)));
        Assert.True(Inside(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1)));
        Assert.False(Inside(new DateTime(2026, 12, 30, 23, 59, 59, DateTimeKind.Utc)));
        Assert.False(Inside(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>Reads <c>yyyy-MM-dd</c> with the invariant culture, so the test never depends on the machine's settings.</summary>
    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, DateParser.Format, CultureInfo.InvariantCulture);
}
