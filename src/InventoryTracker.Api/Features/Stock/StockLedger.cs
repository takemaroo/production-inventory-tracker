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
/// (T-011), adjustments (T-015) and received returns (T-019) all add their movements through
/// <see cref="AddAsync"/>, which refuses any movement that would take the Warehouse or Production storage below zero
/// (D-2, AC-12.3, AC-15.7).
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
/// <item>It saves each movement as soon as it is added (still inside the transaction, so nothing is permanent until the
/// commit). The next movement for the same item in the same request is then included in the next sum. Without this, a
/// request adding two withdrawals of 8 kg from 12 kg would check both against 12 kg.</item>
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
    /// The largest quantity one movement may carry: the capacity of a <c>decimal(18,3)</c> column (architecture 3.1).
    /// SQLite would store a larger value anyway (it does not enforce precision), but PostgreSQL would refuse it, and a
    /// handful of such values could overflow the stock sums. Services refuse larger input with a <c>400</c>; the ledger
    /// refuses it as a programming error.
    /// </summary>
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
    /// Adds <paramref name="movement"/> to the ledger: checks its shape (D-2), checks that neither location it takes
    /// from would go below zero, inserts it and saves it inside the open write transaction. Afterwards
    /// <c>movement.Id</c> holds its database id.
    /// </summary>
    /// <param name="movement">
    /// A new movement with <c>Type</c>, <c>ItemId</c>, the signed change(s) for its type, <c>OccurredAt</c> (from
    /// <c>Clock.UtcNow</c>) and <c>UserId</c> (the signed-in user) set, plus exactly the order, return, date, note or
    /// reason fields its type uses (see <c>CheckLinks</c>: for example a SentToProduction needs its
    /// <c>ProductionOrderNumber</c>, a Received its <c>ReceiptDate</c>, an Adjustment its <c>Reason</c>).
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
    public async Task AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        // Rule 2 above: the check is only safe while this request holds the write lock until it commits. Any open
        // transaction passes this test; services open theirs with InWriteTransactionAsync.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "StockLedger.AddAsync must run inside db.InWriteTransactionAsync (architecture 5.2).");
        }

        // Rule 1 above, decision #86: the SaveChangesAsync below saves everything the DbContext tracks. A movement put
        // on the DbContext directly (db.Movements.Add) would be saved with it without any stock check, so a direct
        // −100 next to a ledger +1 could take the Warehouse to −99. Earlier ledger movements are already saved
        // (Unchanged, not Added), so any Added movement here did not come through the ledger.
        if (db.ChangeTracker.Entries<Movement>().Any(entry => entry.State == EntityState.Added))
        {
            throw new InvalidOperationException(
                "A movement was added to the DbContext without StockLedger; every movement must go through StockLedger.AddAsync.");
        }

        CheckShape(movement);
        CheckLinks(movement);

        // Only a decrease can take a location below zero, so an increase needs no sum at all (a receipt is one insert).
        await RefuseIfBelowZeroAsync(movement, Location.Warehouse, movement.WarehouseChange, cancellationToken);
        await RefuseIfBelowZeroAsync(movement, Location.ProductionStorage, movement.ProductionStorageChange, cancellationToken);

        db.Movements.Add(movement);

        // Rule 4 above: saved now, inside the transaction, so the next sum in this request includes it.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// D-2: refuses the movement with <c>409</c> when <paramref name="change"/> is a decrease larger than the
    /// location's current stock. Exactly reaching zero is allowed (AC-12.3: −12 kg from 12 kg leaves 0).
    /// </summary>
    private async Task RefuseIfBelowZeroAsync(
        Movement movement, Location location, decimal? change, CancellationToken cancellationToken)
    {
        if (change is not < 0m)
        {
            return;
        }

        var available = await stock.GetStockAsync(movement.ItemId, location, cancellationToken);
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
    /// of W or P, either sign. No movement has a zero quantity (PRD section 6 row 17, AC-15.4).
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
    /// with its parent's ProductionOrderNumber (design 5.6).</description></item>
    /// </list>
    /// Note and Reason must also be stored as the services clean them (trimmed, not blank, design 2.6) and fit their
    /// 500-character columns, which SQLite would not enforce.
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

        foreach (var (field, text) in new[] { ("Note", movement.Note), ("Reason", movement.Reason) })
        {
            if (text is not null && (Text.Clean(text) != text || text.Length > ColumnRules.TextLength))
            {
                throw Invalid(movement, $"{field} must be trimmed, not blank, and at most {ColumnRules.TextLength} characters");
            }
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
