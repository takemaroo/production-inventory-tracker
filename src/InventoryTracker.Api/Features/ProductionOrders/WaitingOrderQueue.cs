using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// D-5 "oldest order first" against the database: loads the queue of waiting (Created) orders that comes before an order
/// and shares the free leftover out along it. The one implementation behind both the estimate on a Created order
/// (AC-13.2, <see cref="ProductionOrderService"/>) and the leftover applied fixed by its send (D-6,
/// <see cref="SendOrderService"/>), decision #97.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both must use this one method.</b> The warehouse weighs out what the estimate says: the nominal send and the
/// range shown on a Created order (design 3.6 step 2). The send then checks the quantity against the range worked out
/// from the leftover it applies. If the two were calculated from different queues (one counting orders created in the
/// same second differently, say, or forgetting to load the lines), the estimate could promise 20 kg of leftover while
/// the send applied 50, and a send weighed out exactly as shown would be rejected, or accepted with the wrong figure
/// fixed on the order for ever. Written once, the same data always gives the same answer, before and at the send.
/// </para>
/// <para>
/// <b>Which orders are in the queue.</b> Only Created orders wait for leftover. They are served oldest first by creation
/// time, and orders created in the same second by number, lowest first (architecture 3.3). So the queue for an order is
/// every Created order created before it, or in the same second with a number up to its own, plus the order itself.
/// Younger orders come after it and cannot change its share, so they are not loaded.
/// </para>
/// <para>
/// A <c>static</c> class with the request's <c>AppDbContext</c> passed in, rather than a service of its own: it holds no
/// state, and passing the DbContext makes it plain that the query runs in the caller's transaction, which must already be
/// open so the queue and the free leftover come from one snapshot (decision #85). It refuses to run without one.
/// </para>
/// <para>
/// Public, not internal, only so the test project can call it directly to prove that refusal; feature code reaches it
/// through the two services above.
/// </para>
/// </remarks>
public static class WaitingOrderQueue
{
    /// <summary>
    /// D-5: the leftover of each raw material allocated to each order in <paramref name="order"/>'s queue, oldest first.
    /// Read this order's share with <c>LeftoverAllocation.For(order.Number, rawMaterialId)</c>.
    /// </summary>
    /// <param name="db">The request's DbContext, with its read or write transaction open.</param>
    /// <param name="order">The Created order whose share is wanted (its number and creation time are used).</param>
    /// <param name="freeLeftoverByMaterial">Free leftover per raw material (D-4), from <c>StockQueries.GetFiguresAsync</c>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="db"/>.</exception>
    public static async Task<LeftoverAllocation> AllocateAsync(
        AppDbContext db,
        ProductionOrder order,
        IReadOnlyDictionary<int, decimal> freeLeftoverByMaterial,
        CancellationToken cancellationToken)
    {
        // Decision #85: the caller read the free leftover in this same transaction. Without one, this query would see
        // the database a moment later, and a send committed in between could hand the same leftover out twice. Same
        // guard as ProductionOrderService.ReadDetailAsync.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "WaitingOrderQueue.AllocateAsync must run inside a write transaction or StockQueries.InReadTransactionAsync (decision #85).");
        }

        // Lines must be loaded: the allocator reads each order's lines, and an order without them would silently take
        // nothing (see LeftoverAllocator.Allocate). AsNoTracking: these copies are only read, never saved, and stay apart
        // from any order the caller is changing.
        var queue = await db.ProductionOrders
            .AsNoTracking()
            .Where(waiting => waiting.Status == OrderStatus.Created
                && (waiting.CreatedAt < order.CreatedAt
                    || (waiting.CreatedAt == order.CreatedAt && waiting.Number <= order.Number)))
            .Include(waiting => waiting.Lines)
            .ToListAsync(cancellationToken);

        return LeftoverAllocator.Allocate(queue, freeLeftoverByMaterial);
    }
}
