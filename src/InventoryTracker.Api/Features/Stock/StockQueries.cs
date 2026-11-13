using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// Reads the current stock figures of items: Warehouse and Production storage stock (D-2), material committed to the
/// InProduction order (D-3), quantity in Pending returns, and free leftover (D-4). Every feature that needs a stock
/// figure asks this class, so stock is calculated in exactly one way (architecture 3.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why stock is a sum and never a stored number.</b> The database has no "balance" column anywhere. The stock of an
/// item in a location is the sum of that location's change over all of the item's movements (D-2, decision #20). A
/// stored balance would be a second copy of the same fact, and the two copies could disagree: a bug, a crash between
/// two writes or a hand-edit of one table would leave the history saying one thing and the balance another. With a
/// sum there is only one copy, the history itself, so the stock shown by <c>GET /stock</c> is always exactly what the
/// movement list adds up to (AC-11.4), by construction. Movements are insert-only (AC-11.3), so history cannot be
/// rewritten either.
/// </para>
/// <para>
/// <b>Performance.</b> Summing is cheap only when it is filtered: architecture 5.1 measured under 10 ms for one item and
/// about 140 ms for a grouped sum over 100,000 movements. So every method here takes the item ids it needs (a page of
/// <c>GET /stock</c>, the lines of one order) and sums only their movements, using the index on <c>Movements.ItemId</c>.
/// Never call these methods in a loop over items; pass all the ids at once.
/// </para>
/// <para>
/// <b>Inside a write transaction</b> these queries run on the request's own connection. They see every row the
/// transaction has already saved (not yet committed, so invisible to everyone else), and, because SQLite write
/// transactions run one at a time (architecture 5.2), no other request can change the rows between this read and the
/// commit. That is what makes a check such as "is there enough stock?" safe to act on. Rows only added to the
/// DbContext and not yet saved are not seen: the database does not know about them yet.
/// </para>
/// <para>
/// <b>Getting it.</b> A service lists it as a constructor parameter, for example
/// <c>public sealed class SendOrderService(AppDbContext db, StockQueries stock)</c>, and dependency injection supplies
/// it (decision #77; <c>FeatureRegistration</c> registers it). It is registered as <em>scoped</em>: one instance per
/// HTTP request, created with that request's own <c>AppDbContext</c>. That matters: inside a write transaction its
/// queries must run on the same DbContext, and so the same connection and transaction, as the service's own work.
/// </para>
/// </remarks>
public sealed class StockQueries(AppDbContext db)
{
    /// <summary>
    /// D-2: the stock of one item in one location, the sum of that location's change over the item's movements.
    /// An item with no movements, or none touching that location, has 0 (AC-10.2). Used by <see cref="StockLedger"/>
    /// to check a movement before it is added.
    /// </summary>
    public async Task<decimal> GetStockAsync(int itemId, Location location, CancellationToken cancellationToken)
    {
        var movements = db.Movements.Where(movement => movement.ItemId == itemId);

        // SQL SUM skips NULLs (a movement that does not touch this location), and returns NULL when no row has a
        // value, hence "?? 0". EF Core translates the decimal sum on SQLite (architecture 3.1) and adds the stored
        // decimals exactly, so 0.1 + 0.2 is 0.3, not 0.30000000000000004.
        var sum = location == Location.Warehouse
            ? await movements.SumAsync(movement => movement.WarehouseChange, cancellationToken)
            : await movements.SumAsync(movement => movement.ProductionStorageChange, cancellationToken);

        return sum ?? 0m;
    }

    /// <summary>
    /// Every stock figure of design 5.5 for each of <paramref name="itemIds"/>, keyed by item id. Ids with no
    /// movements get zeros (AC-10.2). The figures are calculated the same way for any item; the caller decides what to
    /// show (finished products show <c>null</c> for the leftover figures, design 5.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three small queries, each filtered by the ids, whatever the number of ids.
    /// </para>
    /// <para>
    /// <b>One snapshot for all three (decision #85).</b> Free leftover combines the results of different queries:
    /// Production storage stock from the movements, committed material from the order lines. Run one after another
    /// without a transaction, each query reads the database as it is at that moment, so a send committed between them
    /// would give a row that never existed. For example, storage 50 kg read before the send (still the old figure) and
    /// 1,000 kg committed read after it give "50 in storage, 1,000 committed, 0 free", while before the send it was
    /// "50 in storage, 0 committed, 50 free" and after it "1,000, 1,000, 0". So outside a write transaction the three
    /// queries run inside one read transaction. In WAL mode SQLite gives a read transaction one consistent snapshot of
    /// the database from its first read until it ends, without blocking writers.
    /// </para>
    /// <para>
    /// Inside a write transaction (a service checking stock before it writes), the queries simply use that transaction.
    /// It already gives one consistent view, because no other writer can run until it commits (architecture 5.2), and it
    /// sees the request's own saved movements. A second, nested transaction is not possible on one connection anyway.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, StockFigures>> GetFiguresAsync(
        IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<int, StockFigures>();
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await ReadFiguresAsync(itemIds, cancellationToken);
        }

        return await InReadTransactionAsync(() => ReadFiguresAsync(itemIds, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="read"/> inside one read-only transaction, so every query in it sees the same snapshot.
    /// </summary>
    /// <remarks>
    /// EF Core's own <c>BeginTransactionAsync</c> is not used here: on SQLite it issues <c>BEGIN IMMEDIATE</c>, which takes
    /// the single write lock (that is what makes <c>InWriteTransactionAsync</c> serialize writers). A read must not queue
    /// behind every write, so this opens a <em>deferred</em> transaction (<c>BEGIN</c>) directly on the SQLite
    /// connection and hands it to EF Core with <c>UseTransactionAsync</c>. A deferred transaction takes no lock until it
    /// first reads, and then only a shared read snapshot. PostgreSQL later: replace the body with
    /// <c>BeginTransactionAsync(IsolationLevel.RepeatableRead)</c>, which gives the same one-snapshot reads.
    /// <para>
    /// The DbContext is left as it was found, whatever happens (a normal end, an error, a cancelled request): no
    /// transaction attached, and the connection closed again unless the caller had opened it.
    /// </para>
    /// </remarks>
    private async Task<T> InReadTransactionAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        // EF Core normally opens and closes the connection around each query; it must stay open for the transaction.
        // EF counts opens: CloseConnectionAsync below closes it only if this call is the one that opened it, so a
        // connection the caller had already opened stays open.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(deferred: true);

            // UseTransactionAsync returns EF Core's wrapper around the transaction. Disposing that wrapper is what
            // detaches the transaction from the DbContext and undoes the extra connection open EF did when attaching
            // it. (UseTransactionAsync(null) detaches but does not undo the open, so the connection stayed open after
            // every read: T-008 review round 2.)
            var efTransaction = await db.Database.UseTransactionAsync(transaction, cancellationToken);
            try
            {
                var result = await read();

                // Nothing was written, so commit and rollback are the same; commit ends the snapshot cleanly.
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            finally
            {
                // Runs on every exit, including an error or a cancelled request; an uncommitted transaction is then
                // rolled back when `transaction` is disposed at the end of this block.
                await efTransaction!.DisposeAsync();
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The three queries and the D-3/D-4 arithmetic behind <see cref="GetFiguresAsync"/>.</summary>
    private async Task<IReadOnlyDictionary<int, StockFigures>> ReadFiguresAsync(
        IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        // D-2: one grouped sum for both locations of every requested item.
        var sums = await db.Movements
            .Where(movement => itemIds.Contains(movement.ItemId))
            .GroupBy(movement => movement.ItemId)
            .Select(group => new
            {
                ItemId = group.Key,
                Warehouse = group.Sum(movement => movement.WarehouseChange) ?? 0m,
                ProductionStorage = group.Sum(movement => movement.ProductionStorageChange) ?? 0m,
            })
            .ToDictionaryAsync(row => row.ItemId, cancellationToken);

        // D-3: the lines of the InProduction order for these materials. The database allows only one InProduction
        // order (its unique filtered index, AC-15.10) and one line per material on an order, so there is at most one
        // line per material and ToDictionary cannot meet a duplicate key.
        var committedLines = await db.ProductionOrderLines
            .Where(line => line.Order.Status == OrderStatus.InProduction && itemIds.Contains(line.RawMaterialId))
            .Select(line => new
            {
                line.RawMaterialId,
                line.OrderNumber,
                line.LeftoverApplied,
                line.WarehouseSend,
                line.ComplementarySends,
            })
            .ToDictionaryAsync(line => line.RawMaterialId, cancellationToken);

        // US-22, AC-22.1: material in a Pending return has not moved yet but is no longer free leftover (D-4).
        var pendingReturns = await db.Returns
            .Where(leftoverReturn => leftoverReturn.Status == ReturnStatus.Pending && itemIds.Contains(leftoverReturn.ItemId))
            .GroupBy(leftoverReturn => leftoverReturn.ItemId)
            .Select(group => new { ItemId = group.Key, Quantity = group.Sum(leftoverReturn => leftoverReturn.Quantity) })
            .ToDictionaryAsync(row => row.ItemId, row => row.Quantity, cancellationToken);

        var figures = new Dictionary<int, StockFigures>();
        foreach (var itemId in itemIds.Distinct())
        {
            // An item with no movements has no group in the sum, and 0 in both locations (AC-10.2).
            var hasMovements = sums.TryGetValue(itemId, out var sum);
            var warehouse = hasMovements ? sum!.Warehouse : 0m;
            var productionStorage = hasMovements ? sum!.ProductionStorage : 0m;

            // D-3 via the one implementation in Domain/Calculations (architecture 3.3). The send fixes all three
            // figures on the line (T-010), so a null here only means "nothing yet" and counts as 0.
            var committed = 0m;
            int? committedOrderNumber = null;
            if (committedLines.TryGetValue(itemId, out var line))
            {
                committed = ProductionCalculations.Committed(
                    line.LeftoverApplied ?? 0m, line.WarehouseSend ?? 0m, line.ComplementarySends ?? 0m);
                committedOrderNumber = line.OrderNumber;
            }

            var pending = pendingReturns.GetValueOrDefault(itemId);

            // D-4: production storage − committed − pending returns, never below zero.
            var free = ProductionCalculations.FreeLeftover(productionStorage, committed, pending);

            figures[itemId] = new StockFigures(
                warehouse, productionStorage, committed, committedOrderNumber, pending, free);
        }

        return figures;
    }
}

/// <summary>The stock figures of one item, as calculated by <see cref="StockQueries.GetFiguresAsync"/>.</summary>
/// <param name="Warehouse">D-2: Warehouse stock.</param>
/// <param name="ProductionStorage">D-2: Production storage stock.</param>
/// <param name="CommittedToOrder">D-3: committed to the InProduction order; 0 when no running order uses the item.</param>
/// <param name="CommittedOrderNumber">The InProduction order's number, or null when <paramref name="CommittedToOrder"/> has no order.</param>
/// <param name="PendingReturns">Total quantity of the item in Pending returns.</param>
/// <param name="FreeLeftover">D-4: production storage − committed − pending returns, never below 0.</param>
public sealed record StockFigures(
    decimal Warehouse,
    decimal ProductionStorage,
    decimal CommittedToOrder,
    int? CommittedOrderNumber,
    decimal PendingReturns,
    decimal FreeLeftover);
