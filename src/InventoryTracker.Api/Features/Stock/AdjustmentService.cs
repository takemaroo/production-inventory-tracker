using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// Records a Manager's stock adjustment (US-12): a correction after a physical count, or a write-off of spoiled
/// material, as one Adjustment movement in one location with the reason on record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why adjustments exist.</b> Movements can never be edited or deleted (AC-11.3), so the only way to make the books
/// match what is physically on the shelf is a new movement that says by how much, and why. A write-off is simply a
/// negative adjustment (AC-12.4); a positive one adds stock that was found, or brings a finished product into the
/// Warehouse (PRD section 2: finished products enter stock only through completion or an adjustment).
/// </para>
/// <para>
/// Like a receipt, an adjustment has no table of its own: it is a movement of type <c>Adjustment</c> (design 4), written
/// through <see cref="StockLedger"/> inside one write transaction (architecture 5.1, 5.2). The ledger is what guarantees
/// that stock never goes below zero, even when two adjustments arrive at the same moment (PRD section 5): both run one
/// after the other inside SQLite's single write lock, so the second sees the first one's result.
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2; there is no record in the URL, so no <c>404</c>): the body's fields (<c>400</c>
/// <c>validation</c>, every problem in one response); a finished product in Production storage (<c>400</c>
/// <c>finished-product-in-production-storage</c>, AC-12.6, after the field errors as for receipts, decision #78); a
/// material of the order in production (<c>409</c> <c>material-in-use</c>, AC-12.5, design 2.2 step 5); then the stock
/// itself (<c>409</c> <c>insufficient-stock</c>, AC-12.3, step 7), which the ledger checks as it adds the movement.
/// </para>
/// </remarks>
public sealed class AdjustmentService(AppDbContext db, StockLedger ledger, Clock clock)
{
    /// <summary>Design 7.2: the code for an adjustment of a finished product in Production storage (AC-12.6).</summary>
    public const string FinishedProductInProductionStorageCode = "finished-product-in-production-storage";

    /// <summary>AC-12.6, design 7.2: finished products are adjusted in the Warehouse only (PRD assumption 10).</summary>
    public const string FinishedProductInProductionStorageMessage = "Finished products are adjusted in the Warehouse only.";

    /// <summary>
    /// AC-12.1: changes the stock of one item in one location by the signed quantity, recording the reason, the user
    /// and the UTC time. Returns the Adjustment movement (design 5.6).
    /// </summary>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="request">The adjustment body.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="ValidationException"><c>400</c> <c>validation</c>: a bad item, location, quantity or reason (AC-12.2).</exception>
    /// <exception cref="BadRequestException"><c>400</c>: a finished product in Production storage (AC-12.6).</exception>
    /// <exception cref="ConflictException">
    /// <c>409</c>: the material is in use by the InProduction order (AC-12.5), or the location does not hold enough
    /// stock for a negative adjustment (AC-12.3).
    /// </exception>
    public Task<MovementResponse> CreateAsync(string userId, AdjustmentRequest request, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var input = await ValidateAsync(request, cancellationToken);

            if (input.Location == Location.ProductionStorage)
            {
                // AC-12.6, PRD assumption 10: production storage holds raw materials waiting to be used. Finished
                // products never go there (completion puts them straight into the Warehouse, AC-19.2), so stock of one
                // in production storage could only ever be a mistake.
                if (input.Item.Type == ItemType.FinishedProduct)
                {
                    throw new BadRequestException(
                        FinishedProductInProductionStorageCode, FinishedProductInProductionStorageMessage);
                }

                await RefuseIfInUseAsync(input.Item, cancellationToken);
            }

            // D-2: an Adjustment changes exactly one location, by the signed quantity; the other location stays null.
            var movement = new Movement
            {
                Type = MovementType.Adjustment,
                ItemId = input.Item.Id,
                WarehouseChange = input.Location == Location.Warehouse ? input.Quantity : null,
                ProductionStorageChange = input.Location == Location.ProductionStorage ? input.Quantity : null,
                OccurredAt = clock.UtcNow,
                UserId = userId,
                Reason = input.Reason,
            };

            // AC-12.3, design 2.2 step 7: the ledger sums the location's stock inside this transaction and refuses a
            // negative adjustment larger than it with 409 "Not enough stock: Cheese in Production storage has 12 kg.".
            // Reaching exactly zero is allowed (−12 kg from 12 kg leaves 0 kg).
            await ledger.AddAsync(movement, cancellationToken);

            // Read back through the shared mapping, inside the transaction, so the 201 body is exactly what
            // GET /movements/{id} shows (the same approach as receipts).
            return await db.Movements
                .Where(saved => saved.Id == movement.Id)
                .Select(MovementMapping.AsResponse)
                .SingleAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// AC-12.5, decision #11, PRD assumption 3: refuses a production-storage adjustment of a material on the order that
    /// is InProduction, with <c>409</c> <c>material-in-use</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is blocked.</b> When the running order completes, its actual usage is worked out from what was put into
    /// production storage for it and what is left there at the end (D-9), and its ConsumedInProduction movement is the
    /// stock there before completion less the leftover entered (AC-19.2). A write-off, or a counted surplus, in the middle
    /// of the run would change production storage without being part of either figure: the order's usage variance would
    /// then be wrong, and the two figures would disagree (the completion logs a Warning if they ever do, decision #98
    /// (f)). So the Manager waits until the order is completed or cancelled. The Warehouse is not part of the usage
    /// figures, so Warehouse adjustments of the same material are allowed.
    /// </para>
    /// <para>
    /// <b>Why it is safe against a simultaneous send or completion.</b> This query runs inside the write transaction,
    /// which holds SQLite's single write lock (architecture 5.2). A send that starts the order cannot commit between this
    /// check and the adjustment's insert, and neither can a completion that ends it. Whichever request takes the lock
    /// first goes first; the other then sees its result: an adjustment after a send is refused here, and a send after an
    /// adjustment calculates its leftover from the adjusted stock (D-6).
    /// </para>
    /// <para>
    /// The order's lines are what the order uses: a complementary order can only add material already on its parent
    /// (PRD assumption 6), so it needs no separate check. At most one order is InProduction (AC-15.10).
    /// </para>
    /// </remarks>
    private async Task RefuseIfInUseAsync(Item item, CancellationToken cancellationToken)
    {
        var runningOrder = await db.ProductionOrderLines
            .Where(line => line.RawMaterialId == item.Id && line.Order.Status == OrderStatus.InProduction)
            .Select(line => (int?)line.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (runningOrder is { } number)
        {
            // The shared code (ErrorCodes.MaterialInUse): a return of the same material is refused with it too (AC-22.3).
            throw new ConflictException(ErrorCodes.MaterialInUse,
                $"{item.Name} in production storage is in use by order #{number}. Adjust it after the order is completed or cancelled.");
        }
    }

    /// <summary>
    /// Design 2.2 step 4 for an adjustment body (AC-12.1, AC-12.2): item present and known, location Warehouse or
    /// ProductionStorage, quantity not zero with at most 3 decimal places and a size of at most 999,999,999.999
    /// (decision #80), reason present and at most 500 characters after trimming. Every problem is reported in one
    /// <c>400</c>, in body order.
    /// </summary>
    private async Task<ValidAdjustment> ValidateAsync(AdjustmentRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        // Design 2.2 step 4 includes "unknown ids in the body": an unknown item is 400, not 404.
        Item? item = null;
        if (request.ItemId is not { } itemId)
        {
            errors.Add("itemId", "Item is required.");
        }
        else
        {
            item = await db.Items.FindAsync([itemId], cancellationToken);
            if (item is null)
            {
                errors.Add("itemId", $"Item {itemId} does not exist.");
            }
        }

        // Design 2.3: received as text and parsed here, any letter case; a missing or unknown value gets
        // "Location must be Warehouse or ProductionStorage." (design 7.2).
        var location = EnumParser.ParseRequired<Location>(request.Location, "location", "Location", errors);

        // One message per field, from the first rule it breaks, in the order of architecture 3.1: required, then the
        // sign rule (for adjustments: not zero), then decimal places, then the maximum (decisions #75, #81).
        if (request.Quantity is not { } quantity)
        {
            errors.Add("quantity", "Quantity is required.");
        }
        else if (quantity == 0m)
        {
            // AC-12.2: an adjustment of nothing changes nothing, and no movement may have a zero quantity (PRD row 17).
            errors.Add("quantity", "Quantity cannot be zero.");
        }
        else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
        {
            // AC-12.2, PRD 4.1; trailing zeros do not count (design 2.4). Decision #95: the message names the field.
            errors.Add("quantity", "Quantity can have at most 3 decimal places.");
        }
        else if (!Quantity.IsWithinMax(quantity))
        {
            // Decision #80, PRD row 20: the limit is on the size of the signed number, so -999,999,999.999 is the lowest
            // accepted value. The shared check compares the absolute value; the message depends on which end was
            // passed (design 7.2), because "must be 999,999,999.999 or less" would be untrue for -1,000,000,000.
            errors.Add("quantity", quantity > 0m ? Quantity.AboveMaxMessage("Quantity") : Quantity.BelowMinMessage("Quantity"));
        }

        // AC-12.1, AC-12.2, design 2.6: trimmed first; a reason of only spaces is no reason (decision #87: the ledger
        // stores it exactly as cleaned here).
        var reason = Text.Clean(request.Reason);
        if (reason is null)
        {
            errors.Add("reason", "Reason is required.");
        }
        else if (reason.Length > ColumnRules.TextLength)
        {
            errors.Add("reason", $"Reason must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();
        return new ValidAdjustment(item!, location!.Value, request.Quantity!.Value, reason!);
    }

    /// <summary>An adjustment body that passed every field check: the loaded item, the location, the signed quantity, the cleaned reason.</summary>
    private sealed record ValidAdjustment(Item Item, Location Location, decimal Quantity, string Reason);
}
