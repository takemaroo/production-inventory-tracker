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
/// Records stock receipts (US-09): raw material arriving from a supplier into the Warehouse, as one Received movement.
/// </summary>
/// <remarks>
/// A receipt has no table of its own: it is a movement of type <c>Received</c> (design 4), written through
/// <see cref="StockLedger"/> inside one write transaction, like every stock change (architecture 5.1, 5.2). Checks follow
/// design 2.2: there is no record in the URL, so first the body's fields (<c>400</c> <c>validation</c>, all problems in
/// one response), then the raw-material rule (<c>400</c> <c>receipt-not-raw-material</c>).
/// </remarks>
public sealed class ReceiptService(AppDbContext db, StockLedger ledger, Clock clock)
{
    /// <summary>Design 7.2: the code for a receipt of a finished product (AC-09.3).</summary>
    public const string ReceiptNotRawMaterialCode = "receipt-not-raw-material";

    /// <summary>AC-09.3: the exact PRD message for a receipt of a finished product.</summary>
    public const string ReceiptNotRawMaterialMessage = "Receipts are for raw materials only.";

    /// <summary>AC-09.2: the design 7.2 message for a receipt date after today.</summary>
    public const string FutureDateMessage = "Receipt date cannot be later than today (UTC).";

    /// <summary>
    /// AC-09.1, AC-09.4: adds the received quantity to the item's Warehouse stock, recording the receipt date, the note,
    /// the user and the UTC time. Returns the Received movement (design 5.6).
    /// </summary>
    /// <param name="userId">The signed-in user, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="request">The receipt body.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    public Task<MovementResponse> CreateAsync(string userId, ReceiptRequest request, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var input = await ValidateAsync(request, cancellationToken);

            // AC-09.3, PRD assumption 10: only raw materials are received. Finished products enter the Warehouse
            // through completed orders (ProducedOutput) or a Manager's adjustment.
            if (input.Item.Type != ItemType.RawMaterial)
            {
                throw new BadRequestException(ReceiptNotRawMaterialCode, ReceiptNotRawMaterialMessage);
            }

            // D-2: Received changes the Warehouse only (W+), so Production storage is left null and unchanged (AC-09.4).
            var movement = new Movement
            {
                Type = MovementType.Received,
                ItemId = input.Item.Id,
                WarehouseChange = input.Quantity,
                OccurredAt = clock.UtcNow,
                UserId = userId,
                ReceiptDate = input.ReceiptDate,
                Note = input.Note,
            };
            await ledger.AddAsync(movement, cancellationToken);

            // Read back through the shared mapping, inside the transaction (which sees its own saved row), so the 201
            // body is exactly what GET /movements/{id} will show.
            return await db.Movements
                .Where(saved => saved.Id == movement.Id)
                .Select(MovementMapping.AsResponse)
                .SingleAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Design 2.2 step 4 for a receipt body (AC-09.2): item present and known, quantity above 0 with at most 3 decimal
    /// places, a valid receipt date not after today (UTC), a note of at most 500 characters. Every problem is reported
    /// in one <c>400</c>, in body order.
    /// </summary>
    private async Task<ValidReceipt> ValidateAsync(ReceiptRequest request, CancellationToken cancellationToken)
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

        // PRD 4.1: at most 3 decimal places; trailing zeros do not count (12.5000 is 12.5, design 2.4).
        if (request.Quantity is not { } quantity)
        {
            errors.Add("quantity", "Quantity is required.");
        }
        else if (quantity <= 0m)
        {
            errors.Add("quantity", "Quantity must be greater than 0.");
        }
        else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
        {
            errors.Add("quantity", "Quantity can have at most 3 decimal places.");
        }
        else if (quantity > StockLedger.MaxQuantity)
        {
            // Not in the PRD: the largest value a quantity column holds (architecture 3.1). Without it, a few huge
            // receipts could overflow the item's stock sum, and movements cannot be deleted to repair it.
            errors.Add("quantity", $"Quantity cannot be more than {MessageFormat.Number(StockLedger.MaxQuantity)}.");
        }

        // Received as text and parsed here, so a malformed date gets a readable message (architecture 4.1 item 3).
        DateOnly? receiptDate = null;
        if (string.IsNullOrWhiteSpace(request.ReceiptDate))
        {
            errors.Add("receiptDate", "Receipt date is required.");
        }
        else if (!DateParser.TryParse(request.ReceiptDate, out var parsed))
        {
            errors.Add("receiptDate", $"Receipt date must be a date in the format {DateParser.Format}.");
        }
        else if (parsed > clock.UtcToday)
        {
            // AC-09.2: "today" is the UTC date, the same for every user wherever they are. Earlier dates are allowed
            // (a delivery recorded late).
            errors.Add("receiptDate", FutureDateMessage);
        }
        else
        {
            receiptDate = parsed;
        }

        // Design 2.6: trimmed; a note of only spaces is no note.
        var note = Text.Clean(request.Note);
        if (note is { Length: > ColumnRules.TextLength })
        {
            errors.Add("note", $"Note must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();
        return new ValidReceipt(item!, request.Quantity!.Value, receiptDate!.Value, note);
    }

    /// <summary>A receipt body that passed every field check: the loaded item, the quantity, the parsed date, the cleaned note.</summary>
    private sealed record ValidReceipt(Item Item, decimal Quantity, DateOnly ReceiptDate, string? Note);
}
