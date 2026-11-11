namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// The result of <see cref="LeftoverAllocator.Allocate"/>: how much free leftover of each raw material is set
/// aside for each Created order (D-5).
/// </summary>
/// <remarks>
/// Services use it in two places: to show the estimate on a Created order (AC-13.2), and, at the moment of
/// sending, to fix the leftover applied on the order being sent (D-6).
/// </remarks>
public sealed class LeftoverAllocation
{
    private readonly IReadOnlyDictionary<(int OrderNumber, int RawMaterialId), decimal> _amounts;

    /// <summary>Wraps the amounts calculated by the allocator.</summary>
    /// <param name="amounts">Allocated quantity per (order number, raw material id). Missing pairs mean 0.</param>
    public LeftoverAllocation(IReadOnlyDictionary<(int OrderNumber, int RawMaterialId), decimal> amounts)
    {
        _amounts = amounts;
    }

    /// <summary>
    /// The leftover of <paramref name="rawMaterialId"/> allocated to order <paramref name="orderNumber"/>, or 0
    /// when the order received none (no free leftover was left for it, or it was not a Created order).
    /// </summary>
    /// <param name="orderNumber">The production order number.</param>
    /// <param name="rawMaterialId">The raw material on one of the order's lines.</param>
    public decimal For(int orderNumber, int rawMaterialId)
        => _amounts.TryGetValue((orderNumber, rawMaterialId), out var amount) ? amount : 0m;
}
