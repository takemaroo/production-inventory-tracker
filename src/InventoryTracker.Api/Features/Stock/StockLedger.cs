using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// The only code that writes stock movements (architecture 5.1). Receipts (T-008), sends (T-010, T-018), completions
/// (T-011), adjustments (T-015) and received returns (T-019) add their movements through <see cref="AddAsync"/>, and
/// (v1.2, T-035) sales order shipments (T-027) add all of theirs at once through <see cref="AddRangeAsync"/>. Both
/// refuse any movement that would take the Warehouse or Production storage below zero (D-2, AC-12.3, AC-15.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock is a sum of movements, never a stored number</b> (D-2, decision #20; see <see cref="StockQueries"/> for the
/// full reasoning). So "changing stock" always means "adding a movement", and this class is the one door through which
/// a movement enters the database. Movements are insert-only (AC-11.3, decision #35): once added, a movement is never
/// changed or deleted, so a check made here can never be undone later by an edit.
/// </para>
/// <para>
/// <b>How the ledger guarantees stock never goes negative, even with simultaneous requests.</b>
/// </para>
/// <list type="number">
/// <item>It is the only writer of movements, so no code path can skip the check. It also refuses to save while the
/// DbContext holds any other new movement that did not come through it (decision #86): its save would otherwise carry
/// that unchecked movement into the database alongside its own.</item>
/// <item>It must be called inside <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>. What it can actually check
/// is only that <em>a</em> transaction is open on the DbContext (it cannot tell which helper opened it), and it refuses
/// to run when none is. In this codebase services open transactions only through <c>InWriteTransactionAsync</c>, which
/// on SQLite starts with <c>BEGIN IMMEDIATE</c>: that takes the database's single write lock, so write transactions run
/// strictly one after another (architecture 5.2). The read transaction <see cref="StockQueries"/> opens for itself is
/// closed again before it returns, so it is never open when the ledger runs.</item>
/// <item>Inside that transaction it reads the current stock from the database (the sum), checks that the movement
/// leaves it at zero or more, and inserts the movement. No other request can add a movement between the read and the
/// commit, because no other request can hold the write lock. Two requests can therefore never both see the old stock
/// (the classic "check, then act" race).</item>
/// <item>Every movement is checked against the stock <em>including the movements before it in the same request</em>.
/// Without this, a request adding two withdrawals of 8 kg from 12 kg would check both against 12 kg. Movements of an
/// earlier call are already saved (inside the transaction, so nothing is permanent until the commit), so the next
/// call's read includes them. Within one <see cref="AddRangeAsync"/> call the ledger keeps a running balance per item
/// and location: the stock it read once, plus each earlier movement of the batch, in order.</item>
/// <item>A refused movement throws, and the whole transaction rolls back, so the request saves nothing (PRD 4.1,
/// "rejected").</item>
/// </list>
/// <para>
/// <b>Using it.</b> A service asks for it in its constructor and dependency injection supplies it (decision #77;
/// <c>FeatureRegistration</c> registers it). It is <em>scoped</em>, one instance per HTTP request, built with that
/// request's <c>AppDbContext</c>, so the ledger's sum, insert and save run in the very transaction the service opened.
/// (A ledger holding a different DbContext would read and write outside that transaction, and the guarantee above
/// would be lost.) The service then calls it inside its write transaction:
/// </para>
/// <code>
/// public sealed class ReceiptService(AppDbContext db, StockLedger ledger, Clock clock)
/// {
///     public Task&lt;MovementResponse&gt; CreateAsync(...) =&gt; db.InWriteTransactionAsync(async () =&gt;
///     {
///         // ... 404, 400 and 409 checks in the design 2.2 order ...
///         var movement = new Movement { Type = MovementType.Received, ItemId = 3, WarehouseChange = 1000m,
///                                       OccurredAt = clock.UtcNow, UserId = userId, /* ... */ };
///         await ledger.AddAsync(movement, cancellationToken);   // movement.Id is set afterwards
///         ...
///     }, cancellationToken);
/// }
/// </code>
/// <para>
/// <b>The ledger is the last line of defence, not the user-facing check.</b> Its refusal names one item and one
/// location. A caller that must name every short line in one message (AC-15.7, AC-18.4), or that needs different
/// wording (a received return, design 7.2 "Returns"), checks all its lines first with <see cref="StockQueries"/> and
/// <see cref="InsufficientStockMessage"/>, throws its own <c>409</c>, and only then adds the movements. The ledger
/// still checks again, so a mistake in the caller can never produce negative stock.
/// </para>
/// <para>
/// <b>One movement or a batch</b> (T-035, SEC-24). <see cref="AddAsync"/> is simply a batch of one, so both run the same
/// checks in the same order. A service that writes many movements at once (the shipment of a sales order with up to 500
/// lines) passes them all to <see cref="AddRangeAsync"/>. The difference is cost, not rules: see that method for why
/// one read and one save per batch matter while the request holds the database's single write lock.
/// </para>
/// <para>
/// <b>What the ledger does not check</b>: business rules of the action (raw materials only, order status, ranges, the
/// material being in use). Those belong to the calling service. A movement whose shape breaks D-2 (wrong columns, wrong
/// sign, a zero quantity, no user, a non-UTC time) is a programming error, not a client error: it throws
/// <see cref="ArgumentException"/>, which the API answers with <c>500</c> and logs.
/// </para>
/// </remarks>
public sealed class StockLedger(AppDbContext db, StockQueries stock)
{
    /// <summary>Design 7.2: the code for a movement that would take a location below zero.</summary>
    public const string InsufficientStockCode = "insufficient-stock";

    /// <summary>
    /// The largest change one movement may carry: the capacity of a <c>decimal(18,3)</c> column (architecture 3.1).
    /// SQLite would store a larger value anyway (it does not enforce precision), but PostgreSQL would refuse it. The
    /// ledger refuses a larger change as a programming error (<c>ArgumentException</c>, <c>500</c>).
    /// </summary>
    /// <remarks>
    /// This is not the client limit. Services check what a client sends against <see cref="Quantity.MaxQuantity"/>
    /// (999,999,999.999, decision #80) and answer <c>400</c>; they never use this constant (decision #81). The ledger's
    /// guard stays at the column's full capacity because computed movements are not input: a
    /// <c>ConsumedInProduction</c> movement carries the actual usage (D-9), which is at most the leftover applied plus
    /// every send, and so can be larger than any single entered quantity.
    /// </remarks>
    public const decimal MaxQuantity = 999_999_999_999_999.999m;

    /// <summary>
    /// The design 7.2 message for a location without enough stock (AC-12.3, AC-15.7, AC-18.4):
    /// "Not enough stock: Cheese in Warehouse has 12 kg.". Public so callers that check several lines first
    /// can use the same sentence for each.
    /// </summary>
    /// <param name="itemName">The item's name.</param>
    /// <param name="location">The location that is short.</param>
    /// <param name="available">The stock the location holds now.</param>
    /// <param name="unit">The item's unit name.</param>
    public static string InsufficientStockMessage(string itemName, Location location, decimal available, string unit) =>
        $"Not enough stock: {itemName} in {LocationName(location)} has {MessageFormat.Number(available)} {unit}.";

    /// <summary>
    /// The location as written in messages: "Warehouse" or "Production storage" (design 7.2). Responses use the enum
    /// names (<c>ProductionStorage</c>); sentences use words.
    /// </summary>
    public static string LocationName(Location location) => location switch
    {
        Location.Warehouse => "Warehouse",
        Location.ProductionStorage => "Production storage",
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown location."),
    };

    /// <summary>
    /// Adds <paramref name="movement"/> to the ledger: checks its shape (D-2) and link fields, checks against the
    /// database that a complementary order it names belongs to its production order, checks that neither location it
    /// takes from would go below zero, inserts it and saves it inside the open write transaction. Afterwards
    /// <c>movement.Id</c> holds its database id.
    /// </summary>
    /// <param name="movement">
    /// A new movement with <c>Type</c>, <c>ItemId</c>, the signed change(s) for its type, <c>OccurredAt</c> (from
    /// <c>Clock.UtcNow</c>) and <c>UserId</c> (the signed-in user) set, plus exactly the order, return, date, note or
    /// reason fields its type uses (see <c>CheckLinks</c>: for example a SentToProduction needs its
    /// <c>ProductionOrderNumber</c>, a Received its <c>ReceiptDate</c>, an Adjustment its <c>Reason</c>, (v1.1) a
    /// ShippedToCustomer its <c>SalesOrderNumber</c>).
    /// </param>
    /// <param name="cancellationToken">Cancels the queries and the save.</param>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>insufficient-stock</c> "Not enough stock: {item} in {location} has {available} {unit}." when the
    /// movement would take a location below zero. Nothing is added.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Called outside a transaction, or the DbContext holds a new movement that did not come through the ledger.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The movement breaks D-2, the audit rules or the link rules (a programming error).
    /// </exception>
    /// <exception cref="DbUpdateException">
    /// (v1.1) The database refused the insert: a <c>SalesOrderNumber</c> that names no sales order (the foreign key), or
    /// a second shipment movement for the same sales order and item (<c>IX_Movements_OneShipmentPerLine</c>). The ledger
    /// deliberately leaves both checks to the database (architecture 5.1, decision #157); see <c>CheckLinks</c>.
    /// </exception>
    public async Task AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        // A batch of one: the same checks, in the same order, as every batch (T-035). Kept as its own method because
        // most services add one movement at a time and read movement.Id straight afterwards.
        await AddRangeAsync([movement], cancellationToken);
    }

    /// <summary>
    /// (T-035, SEC-24) Adds <paramref name="movements"/> to the ledger as one batch: checks each one, in the order given,
    /// exactly as <see cref="AddAsync"/> would if it were called for each in turn, then inserts them all with one save
    /// inside the open write transaction. If any movement is refused, none is added. Afterwards each <c>movement.Id</c>
    /// holds its database id.
    /// </summary>
    /// <param name="movements">New movements, each as described for <see cref="AddAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the queries and the save.</param>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>insufficient-stock</c> for the first movement, in order, that would take a location below zero,
    /// with the stock left by the movements before it. Nothing is added.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for <see cref="AddAsync"/>.</exception>
    /// <exception cref="ArgumentException">
    /// As for <see cref="AddAsync"/>, and when the same movement object appears twice in the batch.
    /// </exception>
    /// <exception cref="DbUpdateException">As for <see cref="AddAsync"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>What "batching" means here, and why it matters.</b> Recording a 500-line shipment one movement at a time meant,
    /// for every line: a scan of everything the DbContext tracks (the stray-movement check below), one <c>SUM</c> query
    /// for that item's stock, and one <c>SaveChangesAsync</c>. <c>SaveChangesAsync</c> is not just "send the INSERT": EF
    /// Core first runs <em>DetectChanges</em>, which compares every tracked object (here the sales order, its 500 lines,
    /// items and units, and every movement saved so far) with the copy it took when it loaded it, to find what changed.
    /// So each save cost a little more than the one before, and 500 of them cost far more than 500 times one. Measured
    /// on the section 5 data (T-035): 1.5 s for 500 lines, about 70% of it EF Core's change-tracking scans and 15% the
    /// per-line sums, all while holding SQLite's single write lock, so every other user's save waited (SEC-24).
    /// </para>
    /// <para>
    /// As a batch, the same work is done once: one scan, one grouped <c>SUM</c> per location the batch takes from, for
    /// all its items together (<see cref="StockQueries.GetStockAsync(IReadOnlyCollection{int}, Location, CancellationToken)"/>,
    /// read from the covering index <c>IX_Movements_StockSums</c>, decision #223), and one <c>SaveChangesAsync</c>: one DetectChanges, then the INSERTs one after another with nothing in between.
    /// </para>
    /// <para>
    /// <b>Why the rules are unchanged.</b> Called one at a time, each movement was checked against a sum that included
    /// the movements saved before it. Here the ledger reads the stock once, then keeps a running balance per item and
    /// location: after checking a movement it adds that movement's changes, so the next movement of the same item is
    /// checked against exactly what a fresh sum would have returned. The checks run per movement, in the order given (shape, links,
    /// complementary parent, Warehouse, Production storage), so the first problem found is the same one a call per
    /// movement would have met first. No other request can change the stock between the read and the save, because
    /// this request holds the write lock (rule 2 above).
    /// </para>
    /// </remarks>
    public async Task AddRangeAsync(IReadOnlyList<Movement> movements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movements);
        if (movements.Any(movement => movement is null))
        {
            throw new ArgumentNullException(nameof(movements), "A batch of movements cannot contain null.");
        }

        // Rule 2 above: the check is only safe while this request holds the write lock until it commits. Any open
        // transaction passes this test; services open theirs with InWriteTransactionAsync.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "StockLedger.AddAsync and AddRangeAsync must run inside db.InWriteTransactionAsync (architecture 5.2).");
        }

        // Rule 1 above, decision #86: the SaveChangesAsync below saves everything the DbContext tracks. A movement put
        // on the DbContext directly (db.Movements.Add) would be saved with it without any stock check, so a direct
        // −100 next to a ledger +1 could take the Warehouse to −99. Earlier ledger movements are already saved
        // (Unchanged, not Added), and this batch's are added only after every check below, so any Added movement here
        // did not come through the ledger.
        if (db.ChangeTracker.Entries<Movement>().Any(entry => entry.State == EntityState.Added))
        {
            throw new InvalidOperationException(
                "A movement was added to the DbContext without StockLedger; every movement must go through StockLedger.AddAsync.");
        }

        if (movements.Count == 0)
        {
            return;
        }

        // Rule 3 above, once for the whole batch: the current stock of every item some movement takes from. Only a
        // decrease can take a location below zero, so a batch of increases (a receipt) reads nothing at all.
        var balances = await ReadBalancesAsync(movements, cancellationToken);

        // A movement is added once (AC-11.3). Called one at a time, a second call with the same object would find its Id
        // already set; in one batch neither copy has an Id yet, so a repeat is caught here instead.
        var seen = new HashSet<Movement>(ReferenceEqualityComparer.Instance);
        foreach (var movement in movements)
        {
            if (!seen.Add(movement))
            {
                throw Invalid(movement, "it appears twice in the batch; a movement is added only once");
            }

            CheckShape(movement);
            CheckLinks(movement);
            await CheckComplementaryParentAsync(movement, cancellationToken);
            await RefuseIfBelowZeroAsync(movement, Location.Warehouse, movement.WarehouseChange, balances, cancellationToken);
            await RefuseIfBelowZeroAsync(movement, Location.ProductionStorage, movement.ProductionStorageChange, balances, cancellationToken);
            ApplyToBalances(movement, balances);
        }

        // Rule 4 above: every movement passed, so all are added and saved together with one SaveChangesAsync, still
        // inside the transaction, so nothing is permanent until the commit. The next call's read in this request
        // includes them.
        db.Movements.AddRange(movements);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The running balances for <see cref="AddRangeAsync"/>: the current stock of every item, in every location, that
    /// some movement of the batch takes stock from. One query per location taken from, whatever the number of
    /// movements: a shipment takes only from the Warehouse, so it reads once. Pairs the batch only adds to are left out,
    /// because nothing is ever checked against them.
    /// </summary>
    private async Task<Dictionary<(int ItemId, Location Location), decimal>> ReadBalancesAsync(
        IReadOnlyList<Movement> movements, CancellationToken cancellationToken)
    {
        var balances = new Dictionary<(int ItemId, Location Location), decimal>();
        foreach (var location in new[] { Location.Warehouse, Location.ProductionStorage })
        {
            var takenFrom = movements
                .Where(movement => Change(movement, location) < 0m)
                .Select(movement => movement.ItemId)
                .Distinct()
                .ToList();

            if (takenFrom.Count == 0)
            {
                continue;
            }

            foreach (var (itemId, available) in await stock.GetStockAsync(takenFrom, location, cancellationToken))
            {
                balances[(itemId, location)] = available;
            }
        }

        return balances;
    }

    /// <summary>
    /// Adds an accepted movement's changes to the running balances, so the next movement of the same item in the batch
    /// is checked against what a fresh sum would return (rule 4 above). Increases count too: a receipt earlier in a batch
    /// covers a withdrawal later in it, as it would if each had been saved on its own.
    /// </summary>
    private static void ApplyToBalances(Movement movement, Dictionary<(int ItemId, Location Location), decimal> balances)
    {
        foreach (var location in new[] { Location.Warehouse, Location.ProductionStorage })
        {
            var key = (movement.ItemId, location);
            if (balances.TryGetValue(key, out var balance))
            {
                balances[key] = balance + (Change(movement, location) ?? 0m);
            }
        }
    }

    /// <summary>The movement's signed change in <paramref name="location"/>, or <c>null</c> when it does not touch it.</summary>
    private static decimal? Change(Movement movement, Location location) =>
        location == Location.Warehouse ? movement.WarehouseChange : movement.ProductionStorageChange;

    /// <summary>
    /// D-2: refuses the movement with <c>409</c> when <paramref name="change"/> is a decrease larger than the
    /// location's stock in the running balance (the stock read for the batch plus the batch's earlier movements).
    /// Exactly reaching zero is allowed (AC-12.3: −12 kg from 12 kg leaves 0).
    /// </summary>
    private async Task RefuseIfBelowZeroAsync(Movement movement, Location location, decimal? change,
        IReadOnlyDictionary<(int ItemId, Location Location), decimal> balances, CancellationToken cancellationToken)
    {
        if (change is not < 0m)
        {
            return;
        }

        // Every item and location a movement takes from was read by ReadBalancesAsync, so the key is always there.
        var available = balances[(movement.ItemId, location)];
        if (available + change.Value >= 0m)
        {
            return;
        }

        // The item's name and unit are read only for the message, so the usual (successful) path skips this query.
        var item = await db.Items
            .Where(candidate => candidate.Id == movement.ItemId)
            .Select(candidate => new { candidate.Name, Unit = candidate.Unit.Name })
            .SingleAsync(cancellationToken);

        throw new ConflictException(InsufficientStockCode,
            InsufficientStockMessage(item.Name, location, available, item.Unit));
    }

    /// <summary>
    /// D-2 and AC-05.3: throws <see cref="ArgumentException"/> when the movement is not one the PRD allows. Which
    /// change columns each type fills (architecture 3.2): Received W+; SentToProduction W−, P+ (same quantity);
    /// ConsumedInProduction P−; ProducedOutput W+; ReturnedToWarehouse W+, P− (same quantity); Adjustment exactly one
    /// of W or P, either sign; (v1.1) ShippedToCustomer W− only. No movement has a zero quantity (PRD section 6 row 17,
    /// AC-15.4).
    /// </summary>
    private static void CheckShape(Movement movement)
    {
        if (movement.Id != 0)
        {
            // Insert-only (AC-11.3): a movement is added once and never again.
            throw Invalid(movement, "it already has an id; a movement is added only once");
        }

        // AC-05.3: every movement records who did it and when, in UTC whole seconds (design 2.3).
        if (string.IsNullOrWhiteSpace(movement.UserId))
        {
            throw Invalid(movement, "it has no UserId");
        }

        if (movement.OccurredAt.Kind != DateTimeKind.Utc || movement.OccurredAt.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw Invalid(movement, "OccurredAt must be a UTC time in whole seconds; use Clock.UtcNow");
        }

        var warehouse = movement.WarehouseChange;
        var production = movement.ProductionStorageChange;

        foreach (var change in new[] { warehouse, production })
        {
            if (change is { } value && (value == 0m || Math.Abs(value) > MaxQuantity || !Quantity.HasAtMostDecimals(value, Quantity.MaxQuantityDecimals)))
            {
                throw Invalid(movement, "a change must be non-zero, at most 3 decimal places, within decimal(18,3)");
            }
        }

        var valid = movement.Type switch
        {
            MovementType.Received or MovementType.ProducedOutput => warehouse > 0m && production is null,
            MovementType.ConsumedInProduction => warehouse is null && production < 0m,
            MovementType.SentToProduction => warehouse < 0m && production == -warehouse,
            MovementType.ReturnedToWarehouse => production < 0m && warehouse == -production,
            // Exactly one location: one of the two is null and the other is not.
            MovementType.Adjustment => (warehouse is null) != (production is null),
            // (v1.1) D-2, D-11, AC-28.1: a shipment only takes goods out of the Warehouse. Nothing is ever shipped from
            // production storage, so that column must be null.
            MovementType.ShippedToCustomer => warehouse < 0m && production is null,
            // A type added to the enum without a rule here is refused rather than let through unchecked.
            _ => false,
        };

        if (!valid)
        {
            throw Invalid(movement, "its change columns do not match its type (D-2)");
        }
    }

    /// <summary>
    /// Architecture 3.2 and design 5.6: which link and text fields each movement type carries. A movement is stored for
    /// ever (AC-11.3), so a wrong combination (a receipt date on a send, a reason on a receipt, a send without its order)
    /// would stay in the history and in every filter of <c>GET /movements</c>. Throws <see cref="ArgumentException"/>,
    /// because only a programming mistake can produce one.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <listheader><term>Field</term><description>Rule</description></listheader>
    /// <item><term>ReceiptDate</term><description>Received only, and required there (US-09).</description></item>
    /// <item><term>Note</term><description>Received only, optional (the receipt note, design 5.6).</description></item>
    /// <item><term>Reason</term><description>Adjustment only, and required there (US-12).</description></item>
    /// <item><term>ReturnNumber</term><description>ReturnedToWarehouse only, and required there (AC-22.4).</description></item>
    /// <item><term>ProductionOrderNumber</term><description>Required on SentToProduction (a send is always for an order,
    /// or for a complementary order, whose parent it names, design 5.6), ConsumedInProduction and ProducedOutput (both
    /// made by completing an order, AC-19.2). Optional on ReturnedToWarehouse, because a return may be linked to an order
    /// (AC-22.1). Not allowed on Received or Adjustment, which no order causes.</description></item>
    /// <item><term>ComplementaryOrderNumber</term><description>SentToProduction only, optional, and always together
    /// with its parent's ProductionOrderNumber (design 5.6). That the order named really is the parent is checked
    /// against the database by <see cref="CheckComplementaryParentAsync"/>.</description></item>
    /// <item><term>SalesOrderNumber</term><description>(v1.1) ShippedToCustomer only, and required there (AC-28.1,
    /// decision #157). A shipment carries none of the other links or texts: no rule above lists ShippedToCustomer as
    /// allowed, so they are all refused on it without a rule of their own.</description></item>
    /// </list>
    /// Note and Reason must also be stored as the services clean them (trimmed, not blank, design 2.6) and fit their
    /// 500-character columns, which SQLite would not enforce.
    /// <para>
    /// (v1.1) Why the ledger does not read the sales order. Two things about <c>SalesOrderNumber</c> are left to the
    /// database on purpose (architecture 5.1, decision #157). A number that names no sales order fails the insert with
    /// "FOREIGN KEY constraint failed"; a second shipment movement for the same sales order and item fails the unique
    /// index <c>IX_Movements_OneShipmentPerLine</c>. Either way the save throws <see cref="DbUpdateException"/> and the
    /// transaction rolls back, so the database gives the same guarantee a read here would, without the extra query. The
    /// complementary order is different: its rule (the right parent) is not something a foreign key can express.
    /// </para>
    /// </remarks>
    private static void CheckLinks(Movement movement)
    {
        var type = movement.Type;

        CheckField(movement, "ReceiptDate", movement.ReceiptDate is not null,
            allowed: type == MovementType.Received, required: type == MovementType.Received);
        CheckField(movement, "Note", movement.Note is not null,
            allowed: type == MovementType.Received, required: false);
        CheckField(movement, "Reason", movement.Reason is not null,
            allowed: type == MovementType.Adjustment, required: type == MovementType.Adjustment);
        CheckField(movement, "ReturnNumber", movement.ReturnNumber is not null,
            allowed: type == MovementType.ReturnedToWarehouse, required: type == MovementType.ReturnedToWarehouse);
        CheckField(movement, "ProductionOrderNumber", movement.ProductionOrderNumber is not null,
            allowed: type is MovementType.SentToProduction or MovementType.ConsumedInProduction
                or MovementType.ProducedOutput or MovementType.ReturnedToWarehouse,
            required: type is MovementType.SentToProduction or MovementType.ConsumedInProduction or MovementType.ProducedOutput);
        CheckField(movement, "ComplementaryOrderNumber", movement.ComplementaryOrderNumber is not null,
            allowed: type == MovementType.SentToProduction, required: false);

        // (v1.1) AC-28.1, decision #157: the link that makes a shipment a shipment, and nothing else may carry it. Design
        // 5.6 shows salesOrderNumber as null on every other type, even an adjustment whose reason names a sales order
        // (AC-28.6), so GET /movements?salesOrderNumber= can only ever return real shipments.
        CheckField(movement, "SalesOrderNumber", movement.SalesOrderNumber is not null,
            allowed: type == MovementType.ShippedToCustomer, required: type == MovementType.ShippedToCustomer);

        foreach (var (field, text) in new[] { ("Note", movement.Note), ("Reason", movement.Reason) })
        {
            // Decision #116: the same code-point count as the services use, so this guard never refuses text they accepted.
            if (text is not null && (Text.Clean(text) != text || Text.Length(text) > ColumnRules.TextLength))
            {
                throw Invalid(movement, $"{field} must be trimmed, not blank, and at most {ColumnRules.TextLength} characters");
            }
        }
    }

    /// <summary>
    /// Design 5.6, decision #87: a complementary send names both its complementary order and that order's parent, and the
    /// two must belong together. Throws <see cref="ArgumentException"/> when <c>ComplementaryOrderNumber</c> is set and the
    /// complementary order does not exist or has a different parent than <c>ProductionOrderNumber</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckLinks"/> can only see the movement itself; this needs one small read (by primary key), so it runs
    /// only for movements that carry a complementary order. Why it matters: <c>GET /movements?orderNumber=12</c> and the
    /// order's history rely on <c>ProductionOrderNumber</c>, and a movement can never be corrected (AC-11.3), so a
    /// mismatched pair would misreport the order's material for ever. Only a programming mistake can produce one (the
    /// complementary send copies the parent from the complementary order it loaded), hence a <c>500</c>.
    /// </remarks>
    private async Task CheckComplementaryParentAsync(Movement movement, CancellationToken cancellationToken)
    {
        if (movement.ComplementaryOrderNumber is not { } complementaryNumber)
        {
            return;
        }

        var parent = await db.ComplementaryOrders
            .AsNoTracking()
            .Where(complementary => complementary.Number == complementaryNumber)
            .Select(complementary => (int?)complementary.ParentOrderNumber)
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            throw Invalid(movement, $"complementary order #{complementaryNumber} does not exist");
        }

        if (parent != movement.ProductionOrderNumber)
        {
            throw Invalid(movement,
                $"complementary order #{complementaryNumber} belongs to order #{parent}, not to order #{movement.ProductionOrderNumber}");
        }
    }

    /// <summary>Throws when a field is set on a type that does not allow it, or missing on a type that requires it.</summary>
    private static void CheckField(Movement movement, string field, bool isSet, bool allowed, bool required)
    {
        if (isSet && !allowed)
        {
            throw Invalid(movement, $"{field} is not allowed on {movement.Type}");
        }

        if (!isSet && required)
        {
            throw Invalid(movement, $"{field} is required on {movement.Type}");
        }
    }

    private static ArgumentException Invalid(Movement movement, string reason) =>
        new($"Movement of type {movement.Type} for item {movement.ItemId} refused: {reason}.", nameof(movement));
}
