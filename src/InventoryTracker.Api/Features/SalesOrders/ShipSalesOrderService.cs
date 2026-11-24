using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) Shipping a sales order (US-26): the one moment a sales order's goods leave the Warehouse. Every line ships in
/// full, as one <c>ShippedToCustomer</c> movement, and the sales order becomes Shipped (architecture 5.3, decision #158).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a shipment does, in plain words.</b> The Warehouse user has picked the goods of a Created sales order and
/// records that they have gone. No quantities are entered: the sales order already says how much of each item
/// (AC-26.1). If the Warehouse holds enough of every item, each line's quantity is taken out of Warehouse stock and the
/// sales order is marked Shipped with who, when and an optional note. If any item is short, nothing happens at all and
/// the answer names every short line (AC-26.3). There are no partial shipments (decision #141): to ship less, the
/// Seller cancels and creates a smaller sales order.
/// </para>
/// <para>
/// <b>Only Warehouse stock counts</b> (D-11, decision #149). Raw material in production storage, even free leftover, and
/// material in a Pending return are not available to ship until they are back in the Warehouse (AC-26.11). In the other
/// direction, a shipment never waits for or protects production: it may take material a Created production order will
/// need (AC-26.12), it is allowed while an order is InProduction (AC-26.6), and an older Created sales order has no
/// priority over a newer one (AC-26.5). So, unlike the production send, this class checks no production order, no
/// Pending return and no other sales order.
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). Every read, check and write runs inside one
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>, which on SQLite takes the database's single write lock at
/// its start. Two requests that change data therefore never interleave: the second waits, then sees what the first
/// left. That gives the guarantees of AC-26.10:
/// </para>
/// <list type="bullet">
/// <item>One shipment per sales order: the second of two shipments of the same sales order sees Shipped and gets
/// <c>already-shipped</c>, with no movement. The unique index <c>IX_Movements_OneShipmentPerLine</c> is the database's
/// backstop.</item>
/// <item>Two shipments wanting the same stock: the second sees the stock the first left and gets
/// <c>insufficient-stock</c> naming its own short lines. The ledger checks each movement again.</item>
/// </list>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1 v1.1): the sales order in the URL (<c>404</c>), the note (<c>400</c>),
/// the status (<c>409</c>), the Warehouse stock of every line (<c>409</c>), then the writes. There is no line cap: the
/// body carries no lines. The first failing step decides the answer, and nothing is saved.
/// </para>
/// <para>
/// Like <c>SendOrderService</c>, it takes its helpers through its constructor (dependency injection, decision #77):
/// <see cref="StockLedger"/> to write the movements, <see cref="StockQueries"/> for the stock, and
/// <see cref="SalesOrderService"/> for the response, so the ship answers with exactly the detail
/// <c>GET /sales-orders/{number}</c> shows (decision #161). All of them are scoped, so they share this request's
/// <c>AppDbContext</c> and therefore its transaction.
/// </para>
/// </remarks>
public sealed class ShipSalesOrderService(
    AppDbContext db,
    StockLedger ledger,
    StockQueries stock,
    SalesOrderService salesOrders,
    Clock clock,
    ILogger<ShipSalesOrderService> logger)
{
    /// <summary>
    /// Design 7.2: the code for a sales order that is already Shipped (AC-26.7, AC-26.10). It names the state, so the
    /// cancel (T-028) uses it too, with its own sentence ("Codes name the state, messages fit the action").
    /// </summary>
    public const string AlreadyShippedCode = "already-shipped";

    /// <summary>
    /// Design 7.2: the code for a sales order that is Cancelled (AC-26.7, AC-27.3). Shared with the cancel (T-028) in the
    /// same way.
    /// </summary>
    public const string SalesOrderCancelledCode = "sales-order-cancelled";

    /// <summary>
    /// AC-26.1: ships Created sales order <paramref name="number"/> in full: one <c>ShippedToCustomer</c> movement per
    /// line, taking the line's quantity out of the Warehouse, then status Shipped with who shipped it, when and the
    /// note. Returns the sales order detail (design 5.12), now Shipped.
    /// </summary>
    /// <param name="number">The sales order number from the URL.</param>
    /// <param name="request">The optional body; <c>null</c> when none was sent (AC-26.8).</param>
    /// <param name="userId">The signed-in Warehouse or Manager user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c> "Sales order #{number} not found.".</exception>
    /// <exception cref="ValidationException"><c>400</c> for a note over 500 characters (AC-26.8).</exception>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>already-shipped</c> or <c>sales-order-cancelled</c> (AC-26.7), or <c>insufficient-stock</c> naming
    /// every short line (AC-26.3).
    /// </exception>
    public async Task<SalesOrderResponse> ShipAsync(
        int number, ShipSalesOrderRequest? request, string userId, CancellationToken cancellationToken)
    {
        var detail = await db.InWriteTransactionAsync(async () =>
        {
            // Architecture 5.3 step 1, design 2.2 step 3: the record named in the URL. Tracked (no AsNoTracking),
            // because the shipment writes its status onto it. Its lines come with each item's name and unit, for the
            // stock message.
            var salesOrder = await db.SalesOrders
                .Include(order => order.Lines).ThenInclude(line => line.Item).ThenInclude(item => item.Unit)
                .SingleOrDefaultAsync(order => order.Number == number, cancellationToken)
                ?? throw new NotFoundException($"Sales order #{number} not found.");

            // Step 2: the body (AC-26.8), before the state, so a long note on a Shipped sales order is 400, not 409.
            var note = ValidateNote(request);

            // Step 3: the state (AC-26.7, AC-27.3).
            CheckCreated(salesOrder);

            // AC-24.1: line order is LineNo order, used for the stock message and for the movements.
            var lines = salesOrder.Lines.OrderBy(line => line.LineNo).ToList();

            // Step 4: every line against Warehouse stock, before anything is written (AC-26.3).
            await CheckWarehouseStockAsync(number, lines, cancellationToken);

            await WriteShipmentAsync(salesOrder, lines, note, userId, cancellationToken);

            // ReadDetailAsync reads the database, not the tracked objects, so the status change is saved first. Saved
            // inside the open transaction: visible to this request's query, invisible to everyone else until the
            // commit.
            await db.SaveChangesAsync(cancellationToken);
            return await salesOrders.ReadDetailAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 5.3 step 8, section 6 Logging: logged only after the commit, so the log never reports a shipment
        // that was rolled back. Only the number and the user id (decision #163).
        logger.LogInformation("Sales order {SalesOrderNumber} shipped by user {UserId}.", number, userId);
        return detail;
    }

    /// <summary>
    /// Architecture 5.3 step 2, AC-26.8, design 2.6: the shipment note is optional. Missing, <c>null</c>, empty or only
    /// spaces means no note; otherwise it is trimmed and must be 500 characters or fewer, else <c>400</c>
    /// <c>validation</c> "Note must be 500 characters or fewer." under <c>note</c> (design 7.2).
    /// </summary>
    /// <returns>The trimmed note, or <c>null</c>.</returns>
    private static string? ValidateNote(ShipSalesOrderRequest? request)
    {
        var note = Text.Clean(request?.Note);

        // Decision #116: counted in Unicode code points, so an emoji is one character (Text.Length).
        if (Text.Length(note) > ColumnRules.TextLength)
        {
            throw new ValidationException("note", $"Note must be {ColumnRules.TextLength} characters or fewer.");
        }

        return note;
    }

    /// <summary>
    /// Architecture 5.3 step 3, AC-26.7: only a Created sales order ships. Shipped and Cancelled are final (D-10).
    /// </summary>
    /// <remarks>
    /// The customer's active status is deliberately not checked: a sales order whose customer was deactivated after it
    /// was created still ships (AC-26.13, AC-29.4, decision #148). Deactivation only stops new sales orders.
    /// </remarks>
    private static void CheckCreated(SalesOrder salesOrder)
    {
        switch (salesOrder.Status)
        {
            case SalesOrderStatus.Shipped:
                // AC-26.7, AC-26.10: also what the loser of two simultaneous shipments gets, because it reads the status
                // only after the winner has committed (architecture 5.2).
                throw new ConflictException(AlreadyShippedCode, "This sales order has already been shipped.");
            case SalesOrderStatus.Cancelled:
                // AC-27.3: a cancelled sales order never ships, whatever the stock; the status is checked before the stock.
                throw new ConflictException(SalesOrderCancelledCode, "This sales order is cancelled.");
        }
    }

    /// <summary>
    /// Architecture 5.3 step 4, AC-26.3: the Warehouse must hold at least each line's quantity. Every short line is named,
    /// in line order, in one <c>409</c> <c>insufficient-stock</c>; a covered line is not named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why check here when the ledger checks too: <see cref="StockLedger.AddAsync"/> stops at the first short movement
    /// and names only that one, in its own shorter sentence. The PRD wants every short line, with the quantity the sales
    /// order needs (AC-26.3), so all lines are checked before the first movement is added, as the production send does
    /// (decision #158). The ledger still checks each movement as the last line of defence.
    /// </para>
    /// <para>
    /// The figures are read once for all the lines' items (architecture 5.1: stock queries filter by item). Inside this
    /// write transaction <see cref="StockQueries.GetFiguresAsync"/> reads on the same connection, so it sees exactly the
    /// stock the movements will be checked against. Only <c>Warehouse</c> is used (D-11). A line whose quantity equals
    /// the stock is covered (AC-26.4). Each item is on at most one line (a unique index), so no item is counted twice.
    /// </para>
    /// </remarks>
    private async Task CheckWarehouseStockAsync(
        int number, IReadOnlyList<SalesOrderLine> lines, CancellationToken cancellationToken)
    {
        var itemIds = lines.Select(line => line.ItemId).ToList();
        var figures = await stock.GetFiguresAsync(itemIds, cancellationToken);

        var shortLines = lines
            .Where(line => line.Quantity > figures[line.ItemId].Warehouse)
            .Select(line => ShortLineMessage(number, line, figures[line.ItemId].Warehouse))
            .ToList();

        if (shortLines.Count > 0)
        {
            // Design 7.2: one sentence per short line, joined with a space, and no errors field (a ConflictException has
            // none), as for the production send.
            throw new ConflictException(StockLedger.InsufficientStockCode, string.Join(" ", shortLines));
        }
    }

    /// <summary>
    /// Design 7.2: "Not enough stock: {item} in Warehouse has {available} {unit}; sales order #{number} needs {quantity}
    /// {unit}.", with both figures formatted as design 6.1 (for example "1,001 Bag").
    /// </summary>
    private static string ShortLineMessage(int number, SalesOrderLine line, decimal available)
    {
        var unit = line.Item.Unit.Name;
        return $"Not enough stock: {line.Item.Name} in {StockLedger.LocationName(Location.Warehouse)} has " +
            $"{MessageFormat.Number(available)} {unit}; sales order #{number} needs {MessageFormat.Number(line.Quantity)} {unit}.";
    }

    /// <summary>
    /// Architecture 5.3 steps 5 to 7, AC-26.1, AC-28.1: one <c>ShippedToCustomer</c> movement per line, then status
    /// Shipped with who shipped it, when and the note (AC-05.3).
    /// </summary>
    private async Task WriteShipmentAsync(
        SalesOrder salesOrder, IReadOnlyList<SalesOrderLine> lines, string? note, string userId,
        CancellationToken cancellationToken)
    {
        // Step 5, decision #158 (design 9, v1.1 default 14): the clock is read once, so ShippedAt, StatusChangedAt and
        // every movement's OccurredAt are the same instant. The reports filter on ShippedAt (D-13) and the movement list
        // on OccurredAt; with one value, a shipment can never fall on different days in the two.
        var shippedAt = clock.UtcNow;

        // Step 6, AC-26.1, AC-28.1, D-2: each line's full quantity leaves the Warehouse, linked to the sales order and
        // the user. Production storage is never touched (D-11), so its change stays null. Every movement goes through
        // the ledger (architecture 5.1, decision #112), which checks the stock again and saves each one at once, inside
        // this transaction, so the next sum includes it.
        foreach (var line in lines)
        {
            await ledger.AddAsync(new Movement
            {
                Type = MovementType.ShippedToCustomer,
                ItemId = line.ItemId,
                WarehouseChange = -line.Quantity,
                OccurredAt = shippedAt,
                UserId = userId,
                SalesOrderNumber = salesOrder.Number,
            }, cancellationToken);
        }

        // Step 7, AC-26.1: Shipped is final (D-10). StatusChangedAt is the list's statusChangedAt (design 5.12).
        salesOrder.Status = SalesOrderStatus.Shipped;
        salesOrder.ShippedAt = shippedAt;
        salesOrder.ShippedById = userId;
        salesOrder.ShipmentNote = note;
        salesOrder.StatusChangedAt = shippedAt;
    }
}
