using System.Linq.Expressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) Turns a stored sales order into the two response shapes of design 5.12: the full detail and the list row.
/// </summary>
/// <remarks>
/// One mapping for every status, so the answers to create (T-025), ship (T-027) and cancel (T-028) look exactly like
/// <c>GET /sales-orders/{number}</c>. It calculates nothing except the status history, which it builds from the sales
/// order's timestamp pairs, as <c>ProductionOrderMapping</c> does for production orders (decision #22).
/// </remarks>
public static class SalesOrderMapping
{
    /// <summary>
    /// One row of the sales order list (design 5.12, AC-25.1). An <see cref="Expression{TDelegate}"/>: EF Core reads it
    /// as a description and turns it into one SQL query that joins <c>Customers</c> for the current name and active
    /// status, and counts the lines with a subquery, so no entity and no line is loaded into memory.
    /// </summary>
    public static readonly Expression<Func<SalesOrder, SalesOrderSummaryResponse>> AsSummary = order => new(
        order.Number,

        // AC-29.3, AC-33.4: current name and status, read by the join.
        new CustomerSummary(order.Customer.Id, order.Customer.Name, order.Customer.IsActive),
        order.CustomerReference,
        order.Status,
        order.CreatedAt,
        order.StatusChangedAt,
        order.Lines.Count);

    /// <summary>The full sales order detail of design 5.12.</summary>
    /// <param name="order">
    /// The sales order with <c>Customer</c>, <c>CreatedBy</c>, <c>ShippedBy</c>, <c>CancelledBy</c> and <c>Lines</c> with
    /// each <c>Item.Unit</c> loaded (see <see cref="SalesOrderService.ReadDetailAsync"/>).
    /// </param>
    public static SalesOrderResponse ToDetail(SalesOrder order)
    {
        // AC-24.1: lines keep the order in which they were entered, stored as LineNo from 1 (architecture 3.2). The
        // database returns the joined lines in no promised order, so they are sorted here.
        var lines = order.Lines
            .OrderBy(line => line.LineNo)
            .Select(line => new SalesOrderLineResponse(
                line.ItemId, line.Item.Name, line.Item.Type, line.Item.Unit.Name, line.Quantity))
            .ToList();

        return new SalesOrderResponse(
            order.Number,

            // AC-29.3, AC-33.4: current name and status, read by the join.
            new CustomerSummary(order.Customer.Id, order.Customer.Name, order.Customer.IsActive),
            order.CustomerReference,
            order.Status,
            order.CreatedAt,
            Person(order.CreatedBy),
            order.ShipmentNote,
            order.CancellationReason,
            lines,
            History(order));
    }

    /// <summary>
    /// AC-05.3, AC-25.2: the status history, oldest first, from the three timestamp and user pairs (architecture 3.2).
    /// </summary>
    /// <remarks>
    /// A sales order is Created first and then reaches at most one of Shipped or Cancelled (design 3.1), so listing the
    /// filled pairs in this order is already oldest first. Design 5.12 gives each entry its note: none on Created, the
    /// shipment note on Shipped, the reason on Cancelled.
    /// </remarks>
    private static List<SalesOrderHistoryEntryResponse> History(SalesOrder order)
    {
        var history = new List<SalesOrderHistoryEntryResponse>
        {
            new(SalesOrderStatus.Created, order.CreatedAt, Person(order.CreatedBy), Note: null),
        };

        if (order is { ShippedAt: { } shippedAt, ShippedBy: { } shippedBy })
        {
            history.Add(new(SalesOrderStatus.Shipped, shippedAt, Person(shippedBy), order.ShipmentNote));
        }

        if (order is { CancelledAt: { } cancelledAt, CancelledBy: { } cancelledBy })
        {
            history.Add(new(SalesOrderStatus.Cancelled, cancelledAt, Person(cancelledBy), order.CancellationReason));
        }

        return history;
    }

    /// <summary>Design 2.3 "People": a user as <c>{ id, email }</c>. Identity's Email is nullable in its type, never in this app.</summary>
    private static PersonResponse Person(AppUser user) => new(user.Id, user.Email!);
}
