using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Reports;

/// <summary>
/// (v1.1) The two shipped-totals reports (US-32, US-33, D-13): how much of each item was shipped in a period, and how
/// much of each item was shipped to each customer. Read-only, calculated from the sales order tables each time they are
/// asked for (decision #160).
/// </summary>
/// <remarks>
/// <para>
/// <b>One base query for both reports</b> (architecture 3.5). <see cref="ShippedLines"/> selects the sales order lines
/// that count: lines of Shipped sales orders whose shipment time falls in the period, optionally of one customer. Each
/// report groups that same set its own way. Because both start from one definition, the two reports can never disagree
/// about which shipments a period holds. Created and Cancelled sales orders never count, and adjustments are not sales
/// order lines at all, so they cannot count either (AC-32.2, D-13).
/// </para>
/// <para>
/// <b>Why the sums are done by the database.</b> The query groups the lines (<c>GroupBy</c>) and adds them up
/// (<c>Sum</c>, <c>Count</c>) in SQL, so SQLite sends back one small row per item (or per customer and item) instead of
/// every shipped line of the period, which at the PRD's volume is tens of thousands of lines. Only then are the item and
/// customer names joined, to the already small grouped set, and only one page of it is read.
/// </para>
/// <para>
/// <b>Why the order is by <c>NameNormalized</c></b> (design 2.5, architecture 3.1). The reports are the one list ordered
/// by name, A to Z, not newest first (AC-32.1, AC-33.1). "A to Z" means the stored normalized name (trimmed, upper-cased
/// with invariant rules) compared character by character, which is SQLite's default comparison of text. Using the
/// upper-cased copy means letter case never changes the order ("apple" sorts with "Apple", before "Banana"), and the
/// character-by-character (ordinal) rule gives the same order on every machine and in every language setting; a
/// consequence is that "Émile" sorts after "Zeta". The id comes last so that paging is stable even if two names were
/// ever equal.
/// </para>
/// <para>
/// No transaction: the report's count and its page are two statements, like every list (see
/// <c>PagingExtensions</c>); a shipment committed in between only shifts the page, which is acceptable for a read.
/// </para>
/// </remarks>
public sealed class ShippedTotalsService(AppDbContext db)
{
    /// <summary>
    /// US-32: one page of shipped totals per item, A to Z by item name, optionally limited to a period (<c>from</c>,
    /// <c>to</c>), an item type and a customer (AC-32.1). <c>400</c> <c>validation</c> for paging outside its limits, a
    /// date not written <c>yyyy-MM-dd</c>, <c>from</c> later than <c>to</c>, or an unknown item type, every problem in
    /// one response (AC-32.5). No shipment matching the filters is <c>200</c> with an empty page.
    /// </summary>
    public async Task<PagedResult<ShippedByItemResponse>> ByItemAsync(
        PageQuery page, string? from, string? to, string? itemType, int? customerId, CancellationToken cancellationToken)
    {
        // Design 2.2: the query values are checked first, all into one collector, so one 400 lists every problem.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var (firstDay, lastDay) = ParsePeriod(from, to, errors);
        var typeFilter = EnumParser.ParseOptional<ItemType>(itemType, "itemType", "itemType", errors);
        errors.ThrowIfAny();

        var lines = ShippedLines(firstDay, lastDay, customerId);

        // AC-32.1: the item type filter exists on this report only (design 5.13).
        if (typeFilter is { } wantedType)
        {
            lines = lines.Where(line => line.Item.Type == wantedType);
        }

        // D-13, architecture 3.5: one group per item, summed in SQL. Count() counts lines, and a sales order has an item
        // on at most one line (unique index, AC-24.3), so it is the number of sales orders shipped with the item.
        var totals = lines
            .GroupBy(line => line.ItemId)
            .Select(group => new
            {
                ItemId = group.Key,
                Quantity = group.Sum(line => line.Quantity),
                SalesOrderCount = group.Count(),
            });

        // Joined after grouping, so the join meets one row per item. Ordered before the projection, because the sort
        // key (the normalized name) is not part of the response. ToPagedResultAsync then runs one COUNT and reads one page.
        var rows =
            from total in totals
            join item in db.Items on total.ItemId equals item.Id
            orderby item.NameNormalized, item.Id
            select new ShippedByItemResponse(
                item.Id, item.Name, item.Type, item.Unit.Name, total.Quantity, total.SalesOrderCount);

        return await rows.ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>
    /// US-33: one page of shipped totals per customer and item, A to Z by customer name, then item name, optionally
    /// limited to a period (<c>from</c>, <c>to</c>), a customer and an item (AC-33.1). The query checks and answers are
    /// those of <see cref="ByItemAsync"/> (AC-33.2). A deactivated customer's rows still appear, with
    /// <c>isActive: false</c> (AC-33.4).
    /// </summary>
    public async Task<PagedResult<ShippedByCustomerResponse>> ByCustomerAsync(
        PageQuery page, string? from, string? to, int? customerId, int? itemId, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        page.Validate(errors);
        var (firstDay, lastDay) = ParsePeriod(from, to, errors);
        errors.ThrowIfAny();

        var lines = ShippedLines(firstDay, lastDay, customerId);

        // AC-33.1: the item filter exists on this report only (design 5.13).
        if (itemId is { } wantedItem)
        {
            lines = lines.Where(line => line.ItemId == wantedItem);
        }

        // One group per customer and item (architecture 3.5); the customer is the sales order's.
        var totals = lines
            .GroupBy(line => new { line.SalesOrder.CustomerId, line.ItemId })
            .Select(group => new
            {
                group.Key.CustomerId,
                group.Key.ItemId,
                Quantity = group.Sum(line => line.Quantity),
                SalesOrderCount = group.Count(),
            });

        // The customer is joined at request time, never copied onto the sales order, so a rename or a deactivation shows
        // in every row at once, older shipments included.
        var rows =
            from total in totals
            join customer in db.Customers on total.CustomerId equals customer.Id
            join item in db.Items on total.ItemId equals item.Id
            orderby customer.NameNormalized, customer.Id, item.NameNormalized, item.Id
            select new ShippedByCustomerResponse(
                // AC-29.3, AC-33.4: current name and status, read by the join.
                new CustomerSummary(customer.Id, customer.Name, customer.IsActive),
                item.Id, item.Name, item.Type, item.Unit.Name, total.Quantity, total.SalesOrderCount);

        return await rows.ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>
    /// Reads the optional <c>from</c> and <c>to</c> dates (design 2.5, architecture 4.1 item 19), adding the design 7.2
    /// list messages to <paramref name="errors"/>: a value that is not a <c>yyyy-MM-dd</c> date, and <c>from</c> later
    /// than <c>to</c>.
    /// </summary>
    private static (DateOnly? From, DateOnly? To) ParsePeriod(string? from, string? to, ValidationErrors errors)
    {
        var firstDay = DateParser.ParseOptional(from, "from", errors);
        var lastDay = DateParser.ParseOptional(to, "to", errors);
        DateParser.CheckRange(firstDay, lastDay, errors);
        return (firstDay, lastDay);
    }

    /// <summary>
    /// D-13, architecture 3.5: the sales order lines both reports add up. These are the lines of Shipped sales orders whose
    /// shipment time is in the period, and, with <paramref name="customerId"/>, only that customer's. Not run here: it is
    /// a query that the reports go on to group.
    /// </summary>
    /// <remarks>
    /// The period is whole UTC days, both included, either end optional (D-13, design 2.5), compared with
    /// <c>ShippedAt</c>, the shipment time. <see cref="DayRange"/> turns the days into instants exactly as the movement
    /// history does (decision #161). Every movement of a shipment has <c>OccurredAt</c> equal to <c>ShippedAt</c>
    /// (architecture 5.3), so for any period these lines and the <c>ShippedToCustomer</c> movements are the same set
    /// (AC-32.4). The (<c>Status</c>, <c>ShippedAt</c>) index on <c>SalesOrders</c> serves this filter.
    /// </remarks>
    private IQueryable<SalesOrderLine> ShippedLines(DateOnly? from, DateOnly? to, int? customerId)
    {
        // AC-32.2: only Shipped sales orders count; Created and Cancelled ones never do.
        var lines = db.SalesOrderLines.Where(line => line.SalesOrder.Status == SalesOrderStatus.Shipped);

        if (from is { } firstDay)
        {
            var start = DayRange.Start(firstDay);
            lines = lines.Where(line => line.SalesOrder.ShippedAt >= start);
        }

        // Decision #89: to=9999-12-31 has no next day, so EndExclusive gives null and there is no upper bound.
        if (to is { } lastDay && DayRange.EndExclusive(lastDay) is { } end)
        {
            lines = lines.Where(line => line.SalesOrder.ShippedAt < end);
        }

        // AC-32.1, AC-33.1: a customer id that names nothing simply matches no line, so the answer is an empty page
        // (AC-32.5, design 2.5), not a 404.
        if (customerId is { } wantedCustomer)
        {
            lines = lines.Where(line => line.SalesOrder.CustomerId == wantedCustomer);
        }

        return lines;
    }
}
