using System.Linq.Expressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Turns a stored production order into the response shapes of design 5.7: the full detail and the list summary.
/// </summary>
/// <remarks>
/// <para>
/// One mapping for every status, so the send (T-010), completion (T-011) and cancellation (T-016) responses look exactly
/// like <c>GET /production-orders/{number}</c>. The mapping calculates nothing except two display rules: the status
/// history (built from the order's timestamp pairs) and the rounding of stored percentages, through the shared
/// <see cref="Quantity.DisplayPercent(decimal?)"/> (decision #58), so a percentage here is never recalculated from the quantities
/// and always equals the one the notification about the same order shows. The estimate block is calculated by <see cref="ProductionOrderService"/> and handed in, because it needs the database.
/// </para>
/// </remarks>
public static class ProductionOrderMapping
{
    /// <summary>
    /// The full order detail of design 5.7.
    /// </summary>
    /// <param name="order">
    /// The order with <c>FinishedProduct.Unit</c>, <c>CreatedBy</c>, <c>SentBy</c>, <c>CompletedBy</c>,
    /// <c>CancelledBy</c> and <c>Lines</c> with each <c>RawMaterial.Unit</c> loaded (see
    /// <see cref="ProductionOrderService"/>).
    /// </param>
    /// <param name="complementaryOrders">The order's complementary orders, oldest first.</param>
    /// <param name="estimateFor">
    /// AC-13.2: the estimate for one line of a Created order, or null for an order in any other status (design 5.7:
    /// "<c>estimate</c> is null in every other status").
    /// </param>
    public static ProductionOrderResponse ToDetail(
        ProductionOrder order,
        IReadOnlyList<LinkedComplementaryOrderResponse> complementaryOrders,
        Func<ProductionOrderLine, LineEstimateResponse>? estimateFor)
    {
        // Design 7.3 and architecture 3.2: lines are shown in recipe line order, copied onto the order as LineNo.
        var lines = order.Lines
            .OrderBy(line => line.LineNo)
            .Select(line => ToLine(line, estimateFor?.Invoke(line)))
            .ToList();

        return new ProductionOrderResponse(
            order.Number,
            order.FinishedProductId,
            order.FinishedProduct.Name,
            order.FinishedProduct.Unit.Name,
            order.OrderedQuantity,
            order.Status,
            order.CreatedAt,
            Person(order.CreatedBy),
            order.ProducedQuantity,
            order.OutputVariance,
            Quantity.DisplayPercent(order.OutputVariancePercent),
            Quantity.DisplayPercent(order.ToleranceAtCompletionPercent),
            order.CompletionReason,
            order.CancellationReason,
            lines,
            complementaryOrders,
            History(order));
    }

    /// <summary>
    /// One row of the order list (design 5.7, AC-14.1). An <see cref="Expression{TDelegate}"/>, so EF Core translates it
    /// into SQL that selects only these columns and joins the product and its unit; no entity is loaded.
    /// </summary>
    public static readonly Expression<Func<ProductionOrder, ProductionOrderSummaryResponse>> AsSummary = order => new(
        order.Number,
        order.FinishedProductId,
        order.FinishedProduct.Name,
        order.FinishedProduct.Unit.Name,
        order.OrderedQuantity,
        order.Status,
        order.CreatedAt,
        order.StatusChangedAt,
        order.ProducedQuantity);

    /// <summary>One line of the detail. Values not fixed yet are null on the entity and stay null here (AC-14.2).</summary>
    private static ProductionOrderLineResponse ToLine(ProductionOrderLine line, LineEstimateResponse? estimate) => new(
        line.RawMaterialId,
        line.RawMaterial.Name,
        line.RawMaterial.Unit.Name,
        line.AmountPerUnit,
        line.PlannedAmount,
        estimate,
        line.LeftoverApplied,
        line.WarehouseSend,
        line.ComplementarySends,
        line.CarriedLeftover,
        line.LeftoverEntered,
        line.LeftoverRemaining,
        line.ActualUsage,
        line.RequiredForActualOutput,
        line.UsageVariance,
        Quantity.DisplayPercent(line.UsageVariancePercent),
        line.UsageVarianceAboveTolerance);

    /// <summary>
    /// AC-05.3, AC-14.2: the status history, oldest first, from the four timestamp and user pairs (architecture 3.2).
    /// </summary>
    /// <remarks>
    /// An order reaches each status at most once and always in the order Created, InProduction, then Completed or
    /// Cancelled (design 3.1), so listing the pairs that are filled in, in that order, is already oldest first. The note
    /// is the reason the user gave for completing or cancelling; creating and sending take no reason.
    /// </remarks>
    private static List<OrderHistoryEntryResponse> History(ProductionOrder order)
    {
        var history = new List<OrderHistoryEntryResponse>
        {
            new(OrderStatus.Created, order.CreatedAt, Person(order.CreatedBy), Note: null),
        };

        if (order is { SentAt: { } sentAt, SentBy: { } sentBy })
        {
            history.Add(new(OrderStatus.InProduction, sentAt, Person(sentBy), Note: null));
        }

        if (order is { CompletedAt: { } completedAt, CompletedBy: { } completedBy })
        {
            history.Add(new(OrderStatus.Completed, completedAt, Person(completedBy), order.CompletionReason));
        }

        if (order is { CancelledAt: { } cancelledAt, CancelledBy: { } cancelledBy })
        {
            history.Add(new(OrderStatus.Cancelled, cancelledAt, Person(cancelledBy), order.CancellationReason));
        }

        return history;
    }

    /// <summary>Design 2.3 "People": a user as <c>{ id, email }</c>. Identity's Email is nullable in its type, never in this app.</summary>
    private static PersonResponse Person(AppUser user) => new(user.Id, user.Email!);
}
