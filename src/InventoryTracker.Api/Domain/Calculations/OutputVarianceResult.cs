namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// The D-9 output variance of a completed order, calculated by <see cref="ProductionCalculations.OutputVariance"/>.
/// </summary>
/// <param name="Variance">Produced quantity − ordered quantity, in the finished product's unit.</param>
/// <param name="Percent">
/// Variance ÷ ordered quantity × 100, not rounded. The completion service rounds it the same way as the usage
/// percentage (6 places for storage, 2 for responses; see <see cref="LineCompletion.UsageVariancePercent"/>).
/// </param>
public sealed record OutputVarianceResult(decimal Variance, decimal Percent)
{
    /// <summary>
    /// D-9, AC-20.2: an OutputVariance notification is raised for any difference at all. Unlike usage, output
    /// has no tolerance (D-1), so even +1 bag on 1,000 notifies.
    /// </summary>
    public bool RaisesNotification => Variance != 0m;
}
