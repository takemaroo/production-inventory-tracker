namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// The D-9 figures for one order line at completion, calculated by
/// <see cref="ProductionCalculations.CompleteLine"/>. The completion service copies them onto the
/// <see cref="ProductionOrderLine"/> (AC-20.3).
/// </summary>
/// <param name="RequiredForActualOutput">Amount per unit × produced quantity, rounded to 3 places.</param>
/// <param name="LeftoverRemaining">Leftover entered − carried leftover: what is left from this order's own material.</param>
/// <param name="ActualUsage">Leftover applied + warehouse send + complementary sends − leftover remaining.</param>
/// <param name="UsageVariance">Actual usage − required. Positive means more was used than the output needed.</param>
/// <param name="UsageVariancePercent">
/// Usage variance ÷ required × 100, <b>not rounded</b>, so the threshold check (AC-20.4) is never affected by
/// rounding. The completion service must round it itself: with
/// <c>Math.Round(value, 6, MidpointRounding.AwayFromZero)</c> before storing it (architecture 3.1), and to 2 places
/// for responses (PRD 4.1). The database does not do this: the SQLite provider ignores <c>HasPrecision</c> when
/// writing and would store all 28 digits.
/// <para>
/// Null only when RequiredForActualOutput rounds to 0 (decision #49): there is nothing to divide by, so the
/// percentage has no value. The <c>?</c> in <c>decimal?</c> makes the type nullable, so callers must handle that
/// case (store null, and show no percentage).
/// </para>
/// </param>
/// <param name="AboveTolerance">
/// AC-20.4: true when |usage variance %| is greater than the tolerance, judged on the unrounded value.
/// When the required amount rounds to 0 (decision #49), true when any material was used and false when none was.
/// True means a UsageVariance notification is raised for this line.
/// </param>
public sealed record LineCompletion(
    decimal RequiredForActualOutput,
    decimal LeftoverRemaining,
    decimal ActualUsage,
    decimal UsageVariance,
    decimal? UsageVariancePercent,
    bool AboveTolerance);
