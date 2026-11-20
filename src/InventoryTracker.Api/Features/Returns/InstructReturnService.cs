using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// A Manager instructs a leftover return (US-22, AC-22.1 to AC-22.3): some free leftover of a raw material is to go back
/// from production storage to the Warehouse. The return is Pending; nothing moves until the warehouse records receiving it
/// (<see cref="ReceiveReturnService"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Pending already matters.</b> From the moment it is instructed, the quantity is no longer free leftover (D-4):
/// <see cref="StockQueries"/> subtracts Pending returns, so <c>GET /stock</c> shows it under <c>pendingReturns</c> and
/// the leftover estimates of Created orders (D-5) stop counting it. And while it is Pending, no order that uses the
/// material can be sent (AC-15.11, checked by the send): material that is about to leave must not be applied to an
/// order, or that order's usage figures (D-9) would be wrong.
/// </para>
/// <para>
/// <b>The two limits on what can be instructed.</b> Only free leftover can be returned (AC-22.2): material committed to
/// the running order (D-3) or already in another Pending return is not available. And nothing at all can be returned of a
/// material the InProduction order uses (AC-22.3, decision #11): its stock in production storage is part of that order's
/// usage calculation until it is completed or cancelled.
/// </para>
/// <para>
/// <b>Everything in one write transaction</b> (architecture 5.2). The checks and the insert run inside one
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the database's single write lock
/// from the first read to the commit. So an instruction and a send of an order that uses the material can never
/// interleave: if the send is first, the order is InProduction when the instruction checks AC-22.3 and it is refused; if
/// the instruction is first, the send finds the Pending return and is refused (AC-15.11). The complementary send relies
/// on this (decision #106 (c)). Two instructions for the same material run one after the other too, so together they can
/// never exceed the free leftover.
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2; there is no record in the URL, so no <c>404</c>): the body's fields (<c>400</c>
/// <c>validation</c>, every problem in one response, unknown ids included); a finished product (<c>400</c>
/// <c>return-not-raw-material</c>, after the field errors as for receipts, decision #78); the material in use by the
/// running order (<c>409</c> <c>material-in-use</c>, step 5); then the free leftover (<c>409</c>
/// <c>insufficient-free-leftover</c>, step 7, a stock check).
/// </para>
/// </remarks>
public sealed class InstructReturnService(
    AppDbContext db, StockQueries stock, ReturnService returns, Clock clock, ILogger<InstructReturnService> logger)
{
    /// <summary>Design 7.2: the code for a return of a finished product.</summary>
    public const string ReturnNotRawMaterialCode = "return-not-raw-material";

    /// <summary>Design 7.2: the message for a return of a finished product.</summary>
    public const string ReturnNotRawMaterialMessage = "Returns are for raw materials only.";

    /// <summary>Design 7.2: the code for a return larger than the material's free leftover (AC-22.2).</summary>
    public const string InsufficientFreeLeftoverCode = "insufficient-free-leftover";

    /// <summary>
    /// AC-22.1: records a Pending return of <c>quantity</c> of a raw material, with the reason, the optional order link,
    /// and who and when (AC-05.3). Moves no stock. Returns the new return (design 5.9).
    /// </summary>
    /// <param name="request">The body: item, quantity, reason, optional order number.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="ValidationException"><c>400</c> <c>validation</c>: a bad item, quantity, reason or order (AC-22.2).</exception>
    /// <exception cref="BadRequestException"><c>400</c> <c>return-not-raw-material</c>: the item is a finished product.</exception>
    /// <exception cref="ConflictException">
    /// <c>409</c> <c>material-in-use</c> (AC-22.3) or <c>insufficient-free-leftover</c> (AC-22.2).
    /// </exception>
    public async Task<ReturnResponse> InstructAsync(ReturnRequest request, string userId, CancellationToken cancellationToken)
    {
        var response = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 4: the body.
            var input = await ValidateAsync(request, cancellationToken);

            // PRD section 2, US-22: leftover in production storage is raw material waiting to be used; finished products
            // never go there (completion puts them straight into the Warehouse, AC-19.2), so there is nothing to return.
            if (input.Item.Type != ItemType.RawMaterial)
            {
                throw new BadRequestException(ReturnNotRawMaterialCode, ReturnNotRawMaterialMessage);
            }

            // Step 5 (AC-22.3), then step 7 (AC-22.2). Both read inside this transaction, so no send or other return can
            // change the answer before the insert below commits.
            await RefuseIfInUseAsync(input.Item, cancellationToken);
            await RefuseIfAboveFreeLeftoverAsync(input, cancellationToken);

            // AC-22.1, AC-05.3: Pending, with who and when. No movement: the material has not moved yet.
            var leftoverReturn = new LeftoverReturn
            {
                ItemId = input.Item.Id,
                Quantity = input.Quantity,
                Reason = input.Reason,
                ProductionOrderNumber = input.OrderNumber,
                Status = ReturnStatus.Pending,
                CreatedAt = clock.UtcNow,
                CreatedById = userId,
            };
            db.Returns.Add(leftoverReturn);

            // The number is generated by the database, so save first (still inside the transaction) to read it.
            // Design 2.1: returns have their own sequence.
            await db.SaveChangesAsync(cancellationToken);
            return await returns.ReadAsync(leftoverReturn.Number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change, only after the commit, so the log never reports a rolled-back one.
        logger.LogInformation("Return {ReturnNumber} of item {ItemId} instructed by user {UserId}.",
            response.Number, response.ItemId, userId);
        return response;
    }

    /// <summary>
    /// AC-22.3, decision #11, PRD section 6 row 3: refuses a return of a material on the order that is InProduction, with
    /// <c>409</c> <c>material-in-use</c> "{item} is in use by order #{n}, which is in production. Return it after the
    /// order is completed or cancelled." (design 7.2).
    /// </summary>
    /// <remarks>
    /// When the running order completes, its usage is worked out from what was put into production storage for it and
    /// what is left there at the end (D-9). Taking some of that material away mid-run, even an amount the order did not
    /// need, would change the end figure without being part of the calculation, so the Manager waits until the order ends
    /// (the same reasoning as AC-12.5 for adjustments). The order's lines are what it uses: a complementary order can only
    /// add material already on its parent (PRD assumption 6), and at most one order is InProduction (AC-15.10).
    /// </remarks>
    private async Task RefuseIfInUseAsync(Item item, CancellationToken cancellationToken)
    {
        var runningOrder = await db.ProductionOrderLines
            .Where(line => line.RawMaterialId == item.Id && line.Order.Status == OrderStatus.InProduction)
            .Select(line => (int?)line.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (runningOrder is { } number)
        {
            // The same code as the adjustment's AC-12.5 refusal (design 7.2): the same rule, a different sentence.
            throw new ConflictException(ErrorCodes.MaterialInUse,
                $"{item.Name} is in use by order #{number}, which is in production. Return it after the order is completed or cancelled.");
        }
    }

    /// <summary>
    /// AC-22.2, D-4: refuses a quantity greater than the material's free leftover, with <c>409</c>
    /// <c>insufficient-free-leftover</c> "Not enough free leftover: {item} has {free} {unit} free in production storage."
    /// (design 7.2). Exactly the free leftover is allowed (design 3.9 returns all 20 kg of 20 kg free).
    /// </summary>
    /// <remarks>
    /// Free leftover is production storage stock less the material committed to the InProduction order less the Pending
    /// returns (D-4), from the one implementation in <see cref="StockQueries.GetFiguresAsync"/>, read inside this write
    /// transaction. After <see cref="RefuseIfInUseAsync"/> nothing is committed, so it is storage less Pending returns: an
    /// earlier Pending return of the same material has already taken its share. Leftover allocated to Created orders as an
    /// estimate (D-5) is still free: it is only fixed when an order is sent (D-6).
    /// </remarks>
    private async Task RefuseIfAboveFreeLeftoverAsync(ValidReturn input, CancellationToken cancellationToken)
    {
        var figures = await stock.GetFiguresAsync([input.Item.Id], cancellationToken);
        var free = figures[input.Item.Id].FreeLeftover;
        if (input.Quantity > free)
        {
            throw new ConflictException(InsufficientFreeLeftoverCode,
                $"Not enough free leftover: {input.Item.Name} has {MessageFormat.Number(free)} {input.Item.Unit.Name} free in production storage.");
        }
    }

    /// <summary>
    /// Design 2.2 step 4 for a return body (AC-22.1, AC-22.2). Every problem is reported in one <c>400</c>
    /// <c>validation</c>, in body order, one message per field from the first rule it breaks (decision #75):
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>itemId</c>: "Item is required.", or "Item {id} does not exist." (design 2.2: unknown ids in the body are
    /// <c>400</c>, not <c>404</c>).</item>
    /// <item><c>quantity</c>, in the order of architecture 3.1 (required, sign, decimal places, maximum): "Quantity is
    /// required.", "Quantity must be greater than 0.", "Quantity can have at most 3 decimal places." (decision #95),
    /// "Quantity must be 999,999,999.999 or less." (decision #80).</item>
    /// <item><c>reason</c>, trimmed first (design 2.6): "Reason is required." or "Reason must be 500 characters or
    /// fewer.".</item>
    /// <item><c>orderNumber</c>, optional: "Order #{n} does not exist." when it names no order (design 7.2). Any status
    /// is accepted (design 5.9).</item>
    /// </list>
    /// </remarks>
    private async Task<ValidReturn> ValidateAsync(ReturnRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        // The unit is loaded with the item for the free-leftover message.
        Item? item = null;
        if (request.ItemId is not { } itemId)
        {
            errors.Add("itemId", "Item is required.");
        }
        else
        {
            item = await db.Items.Include(candidate => candidate.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (item is null)
            {
                errors.Add("itemId", $"Item {itemId} does not exist.");
            }
        }

        if (request.Quantity is not { } quantity)
        {
            errors.Add("quantity", "Quantity is required.");
        }
        else if (quantity <= 0m)
        {
            // AC-22.1: a return of nothing moves nothing, and no movement may have a zero quantity (PRD row 17).
            errors.Add("quantity", "Quantity must be greater than 0.");
        }
        else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
        {
            // PRD 4.1; trailing zeros do not count (design 2.4).
            errors.Add("quantity", "Quantity can have at most 3 decimal places.");
        }
        else if (!Quantity.IsWithinMax(quantity))
        {
            // Decision #80: every entered quantity is at most 999,999,999.999, through the one shared check (#81).
            errors.Add("quantity", Quantity.AboveMaxMessage("Quantity"));
        }

        var reason = Text.Clean(request.Reason);
        if (reason is null)
        {
            errors.Add("reason", "Reason is required.");
        }
        else if (reason.Length > ColumnRules.TextLength)
        {
            errors.Add("reason", $"Reason must be {ColumnRules.TextLength} characters or fewer.");
        }

        // Design 5.9: the link may name an order in any status (for example a cancelled one, AC-22.1), but it must exist,
        // because the received movement will carry it (decision #107) and movements can never be corrected.
        if (request.OrderNumber is { } orderNumber
            && !await db.ProductionOrders.AnyAsync(order => order.Number == orderNumber, cancellationToken))
        {
            errors.Add("orderNumber", $"Order #{orderNumber} does not exist.");
        }

        errors.ThrowIfAny();

        // ThrowIfAny has thrown if any required field was missing, so the "!" values are not null here.
        return new ValidReturn(item!, request.Quantity!.Value, reason!, request.OrderNumber);
    }

    /// <summary>A return body that passed every field check: the loaded item (with its unit), the quantity, the cleaned reason, the optional order.</summary>
    private sealed record ValidReturn(Item Item, decimal Quantity, string Reason, int? OrderNumber);
}
