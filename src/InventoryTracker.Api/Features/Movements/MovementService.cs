using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Movements;

/// <summary>
/// The movement history (US-11): one page of movements, filtered and newest first, or one movement by id.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Movements are written only by <see cref="StockLedger"/> and are never changed or deleted (AC-11.3,
/// decision #35), so this class has no write transaction (architecture 5.2 applies to changes) and there is no edit or
/// delete endpoint at all.
/// </para>
/// <para>
/// Every row is shaped by the one shared mapping, <see cref="MovementMapping.AsResponse"/> (design 5.6), so a movement
/// looks the same here as in the <c>201</c> of a receipt or an adjustment. Because the history shows every movement
/// exactly as stored, adding up its changes per location gives the stock of <c>GET /stock</c> (AC-11.4): stock is
/// that very sum (D-2, see <see cref="StockQueries"/>).
/// </para>
/// </remarks>
public sealed class MovementService(AppDbContext db)
{
    /// <summary>
    /// AC-11.1, AC-11.2, (v1.1) AC-28.2: one page of movements matching every given filter, newest first. <c>400</c>
    /// <c>validation</c> for paging outside its limits, an unknown location or type, a date not written
    /// <c>yyyy-MM-dd</c>, or <c>from</c> later than <c>to</c>; every problem is reported in one response. No match is
    /// <c>200</c> with an empty page.
    /// </summary>
    public async Task<PagedResult<MovementResponse>> ListAsync(
        PageQuery page, MovementFilter filter, CancellationToken cancellationToken)
    {
        // Design 2.2 step 4: check the whole query first, collecting every message (design 6: one 400 lists them all).
        var errors = new ValidationErrors();
        page.Validate(errors);
        var location = EnumParser.ParseOptional<Location>(filter.Location, "location", "location", errors);
        var type = EnumParser.ParseOptional<MovementType>(filter.Type, "type", "type", errors);
        var from = DateParser.ParseOptional(filter.From, "from", errors);
        var to = DateParser.ParseOptional(filter.To, "to", errors);

        // Design 2.5: 'from' later than 'to' is 400 (an equal pair is a valid one-day range).
        DateParser.CheckRange(from, to, errors);
        errors.ThrowIfAny();

        // Each filter adds one WHERE condition; conditions on one query combine with AND (AC-11.2, design 2.5). The
        // query does not run until ToPagedResultAsync awaits it, so this only builds the SQL.
        var query = db.Movements.AsQueryable();

        if (filter.ItemId is { } itemId)
        {
            query = query.Where(movement => movement.ItemId == itemId);
        }

        // Design 5.6: a movement matches a location when it changes it. A location the movement does not touch is
        // null, and the ledger never stores a zero change (D-2), so "changes it" is exactly "is not null".
        if (location == Location.Warehouse)
        {
            query = query.Where(movement => movement.WarehouseChange != null);
        }
        else if (location == Location.ProductionStorage)
        {
            query = query.Where(movement => movement.ProductionStorageChange != null);
        }

        if (type is { } wantedType)
        {
            query = query.Where(movement => movement.Type == wantedType);
        }

        // Design 5.6: orderNumber also matches the sends of the order's complementary orders. Those movements carry
        // their parent's number in ProductionOrderNumber as well as their own complementary number (architecture 3.2,
        // decision #87), so one comparison finds the order's own movements and its complementary sends together.
        if (filter.OrderNumber is { } orderNumber)
        {
            query = query.Where(movement => movement.ProductionOrderNumber == orderNumber);
        }

        if (filter.ComplementaryOrderNumber is { } complementaryNumber)
        {
            query = query.Where(movement => movement.ComplementaryOrderNumber == complementaryNumber);
        }

        // (v1.1) AC-28.2, architecture 4.1 item 20: only shipments carry a sales order number (the ledger refuses it on
        // every other type), so this finds exactly the sales order's shipment movements. SQLite answers it from the
        // partial index IX_Movements_OneShipmentPerLine: "SalesOrderNumber = 5" implies the index's own filter
        // "SalesOrderNumber IS NOT NULL". The orderNumber filter above compares ProductionOrderNumber, which is always
        // null on a shipment, so it can never return one, even when the two numbers are equal (D-10).
        if (filter.SalesOrderNumber is { } salesOrderNumber)
        {
            query = query.Where(movement => movement.SalesOrderNumber == salesOrderNumber);
        }

        // AC-11.2, design 2.5 and 9: from and to are whole UTC days, both included, compared with occurredAt (not the
        // receipt date). So "from" starts at 00:00:00 UTC of that day, and "to" ends just before 00:00:00 UTC of the
        // next day. DayRange holds that rule (and why it is "before the next midnight"), shared with the reports so the
        // two can never disagree about a day (decision #161).
        if (from is { } firstDay)
        {
            var start = DayRange.Start(firstDay);
            query = query.Where(movement => movement.OccurredAt >= start);
        }

        // Decision #89: to=9999-12-31 has no next day, so EndExclusive gives null and the query gets no upper bound.
        if (to is { } lastDay && DayRange.EndExclusive(lastDay) is { } end)
        {
            query = query.Where(movement => movement.OccurredAt < end);
        }

        // PRD 4.1, design 2.5: newest first, by time descending. Several movements can share one second (a send writes
        // one per line), so the id breaks the tie, newest id first; without it the order of equal times, and so which
        // rows land on which page, would be up to the database. The index on OccurredAt (whose entries also hold the
        // row id) lets SQLite read this order without sorting the table.
        return await query
            .OrderByDescending(movement => movement.OccurredAt)
            .ThenByDescending(movement => movement.Id)
            .Select(MovementMapping.AsResponse)
            .ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>
    /// AC-11.1: one movement, in the same shape as a list row, or <c>404</c> "Movement {id} not found.". This is the URL
    /// in the <c>Location</c> header of a receipt or adjustment (design 4).
    /// </summary>
    public async Task<MovementResponse> GetAsync(int id, CancellationToken cancellationToken)
    {
        return await db.Movements
            .Where(movement => movement.Id == id)
            .Select(MovementMapping.AsResponse)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Movement {id} not found.");
    }
}
