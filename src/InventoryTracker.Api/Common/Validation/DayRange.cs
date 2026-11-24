namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Turns the whole UTC days of a <c>from</c>/<c>to</c> filter into the two instants a query compares with (design 2.5,
/// AC-11.2): the start of the first day, and the start of the day after the last one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "before the next midnight" and not "at or before 23:59:59".</b> Both days are included (design 2.5), so a
/// filter <c>to=2026-11-30</c> must take in every instant of 30 November. Comparing <c>time &lt; 2026-12-01 00:00:00</c>
/// does that exactly, whatever the precision of the stored times; <c>time &lt;= 23:59:59</c> would quietly lose
/// 23:59:59.5 if a time with a fraction of a second ever reached the table.
/// </para>
/// <para>
/// <b>Why it lives here</b> (decision #161). The movement history (<c>MovementService</c>) wrote this first; (v1.1) the
/// shipped-totals reports filter shipment times by the same whole days (D-13, architecture 3.5). One copy means a
/// movement date filter and a report date filter can never disagree about which day a shipment fell on (design 5.6,
/// AC-32.4).
/// </para>
/// <para>
/// Usage in a query, where <c>from</c> and <c>to</c> are the parsed <see cref="DateOnly"/> values:
/// </para>
/// <code>
/// if (from is { } firstDay) { var start = DayRange.Start(firstDay); query = query.Where(m =&gt; m.OccurredAt &gt;= start); }
/// if (to is { } lastDay &amp;&amp; DayRange.EndExclusive(lastDay) is { } end) { query = query.Where(m =&gt; m.OccurredAt &lt; end); }
/// </code>
/// </remarks>
public static class DayRange
{
    /// <summary>
    /// 00:00:00 UTC at the start of <paramref name="day"/>: the first instant a <c>from</c> filter includes. Marked as
    /// UTC, like every stored time (architecture 3.1), so EF Core compares like with like.
    /// </summary>
    public static DateTime Start(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// 00:00:00 UTC at the start of the day after <paramref name="day"/>: the first instant a <c>to</c> filter excludes.
    /// <c>null</c> for 9999-12-31, which means "no upper bound".
    /// </summary>
    /// <remarks>
    /// Decision #89: 9999-12-31 is the last date .NET can hold, so it has no next day; <c>AddDays(1)</c> would throw,
    /// and the request would end in a <c>500</c>. Nothing can happen after that day either, so leaving the upper bound
    /// out gives exactly the right answer. Callers skip the condition when this returns <c>null</c>.
    /// </remarks>
    public static DateTime? EndExclusive(DateOnly day) => day == DateOnly.MaxValue ? null : Start(day.AddDays(1));
}
