using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;
using InventoryTracker.Api.Features.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// Creating and reading production orders (US-13, US-14): create an order from its product's recipe, list orders, and
/// show one order in full with its leftover estimate.
/// </summary>
/// <remarks>
/// <para>
/// Follows the pattern of the other features: the change runs inside
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> with the checks in the design 2.2 order, and a broken rule
/// is thrown as an <see cref="ApiException"/>. Creating an order moves no stock (AC-13.4), so <c>StockLedger</c> is not
/// needed here; <see cref="StockQueries"/> supplies the free leftover for the estimate.
/// </para>
/// <para>
/// <c>IOptions&lt;InventoryOptions&gt;</c> is how ASP.NET Core hands a class its settings: the <c>Inventory</c> section
/// of the configuration, bound once at start-up (see <see cref="InventoryOptions"/>). Here it gives the tolerance t.
/// </para>
/// </remarks>
public sealed class ProductionOrderService(
    AppDbContext db, StockQueries stock, Clock clock, IOptions<InventoryOptions> options)
{
    /// <summary>Design 7.2: the code for an order of a raw material (AC-13.3).</summary>
    public const string OrderNotFinishedProductCode = "order-not-finished-product";

    /// <summary>Design 7.2: the code for an order of a finished product without a recipe (AC-13.3).</summary>
    public const string NoRecipeCode = "no-recipe";

    /// <summary>
    /// AC-14.1: one page of orders, newest first (PRD 4.1, design 2.5), optionally only one status
    /// (<c>?status=Created</c>).
    /// </summary>
    public async Task<PagedResult<ProductionOrderSummaryResponse>> ListAsync(
        PageQuery page, string? status, CancellationToken cancellationToken)
    {
        // One collector for paging and the filter, so every bad query value is reported in one response.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var statusFilter = EnumParser.ParseOptional<OrderStatus>(status, "status", "status", errors);
        errors.ThrowIfAny();

        var query = db.ProductionOrders.AsQueryable();
        if (statusFilter is { } wanted)
        {
            query = query.Where(order => order.Status == wanted);
        }

        // Design 2.5: newest first by creation time, then by number for orders created in the same second.
        return await query
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Number)
            .Select(ProductionOrderMapping.AsSummary)
            .ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>
    /// AC-14.2, AC-13.2: one order in full (design 5.7), with a fresh estimate per line while it is Created, or
    /// <c>404</c> "Order #{number} not found." (AC-14.3).
    /// </summary>
    /// <remarks>
    /// The whole read runs in one read snapshot (decision #85). The estimate combines the waiting orders with the free
    /// leftover; read at two different moments, a send committed in between would count the same leftover twice.
    /// </remarks>
    public Task<ProductionOrderResponse> GetAsync(int number, CancellationToken cancellationToken) =>
        stock.InReadTransactionAsync(() => ReadDetailAsync(number, cancellationToken), cancellationToken);

    /// <summary>
    /// AC-13.1: creates an order for a finished product with a recipe: the next order number, status Created, who and
    /// when, and one line per recipe line with the amount per unit copied and the planned amount. Moves no stock
    /// (AC-13.4). Returns the new order's detail (<c>201</c>).
    /// </summary>
    /// <param name="request">The body: finished product and ordered quantity.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    public Task<ProductionOrderResponse> CreateAsync(
        ProductionOrderRequest request, string userId, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2: there is no record in the URL, so the body's fields come first (400 validation, every
            // problem in one response), then the rules about the product itself (400 with their own codes), the same
            // order as receipts (decision #78).
            var (product, orderedQuantity) = await ValidateAsync(request, cancellationToken);

            // AC-13.3: "an item that is not a FinishedProduct".
            if (product.Type != ItemType.FinishedProduct)
            {
                throw new BadRequestException(OrderNotFinishedProductCode,
                    $"Orders are for finished products only. {product.Name} is a raw material.");
            }

            // AC-13.3: "a finished product with no recipe". A recipe always has at least one line (AC-08.2).
            var recipeLines = await db.RecipeLines
                .Where(line => line.FinishedProductId == product.Id)
                .OrderBy(line => line.LineNo)
                .ToListAsync(cancellationToken);
            if (recipeLines.Count == 0)
            {
                throw new BadRequestException(NoRecipeCode,
                    $"{product.Name} has no recipe. Set its recipe before creating an order.");
            }

            var now = clock.UtcNow;
            var order = new ProductionOrder
            {
                FinishedProductId = product.Id,
                OrderedQuantity = orderedQuantity,
                Status = OrderStatus.Created,

                // AC-13.1, AC-05.3: who created the order and when. While the order is Created, its last status change
                // is its creation (StatusChangedAt is the list's summary field).
                CreatedAt = now,
                CreatedById = userId,
                StatusChangedAt = now,
            };

            // AC-13.1, AC-08.4 (the recipe snapshot): each line COPIES the recipe's amount per unit onto the order, and
            // the planned amount is worked out from that copy once, now. From here on the order never reads the recipe
            // again: the send uses PlannedAmount, and completion's RequiredForActualOutput (D-9) uses the line's own
            // AmountPerUnit. So a Manager can change the recipe tomorrow and this order keeps exactly the amounts it was
            // created with; only orders created after the change use the new recipe.
            foreach (var recipeLine in recipeLines)
            {
                order.Lines.Add(new ProductionOrderLine
                {
                    LineNo = recipeLine.LineNo,
                    RawMaterialId = recipeLine.RawMaterialId,
                    AmountPerUnit = recipeLine.AmountPerUnit,

                    // AC-13.1: planned = round3(amount per unit × ordered quantity), through the one implementation in
                    // Domain/Calculations (T-004). Decision #80's limits keep it inside decimal(18,3).
                    PlannedAmount = ProductionCalculations.PlannedAmount(recipeLine.AmountPerUnit, orderedQuantity),
                });
            }

            db.ProductionOrders.Add(order);

            // The number is generated by the database. Saving inside the write transaction gives it the next number
            // (design 2.1). Because SQLite write transactions run one at a time (architecture 5.2), two Managers
            // creating orders at the same moment get different, consecutive numbers.
            await db.SaveChangesAsync(cancellationToken);

            // AC-13.2: the 201 already carries the estimate. Read back inside the same transaction, which sees the new
            // order, so the response is exactly what GET will show.
            return await ReadDetailAsync(order.Number, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// The detail of order <paramref name="number"/> (design 5.7), or <c>404</c> "Order #{number} not found.". Used by
    /// <c>GET</c> and <c>POST</c> here, and meant for the responses of send, complete and cancel (T-010, T-011, T-016),
    /// which return the same detail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must run inside a transaction, a write transaction or <see cref="StockQueries.InReadTransactionAsync{T}"/>, so the
    /// order, the waiting orders and the stock figures all come from one snapshot (decision #85). It refuses to run
    /// without one, so a caller cannot get a mixed-moment estimate by accident.
    /// </para>
    /// <para>
    /// <b>Call it after <c>SaveChangesAsync</c>.</b> It reads the database, not the objects the caller has in memory
    /// (the queries use <c>AsNoTracking</c>, and the stock figures are SQL sums), so a change that is only tracked, not
    /// yet saved, would be missing from the response: a send that has set InProduction but not saved it would come back
    /// as a Created order with an estimate. Saving inside the open write transaction makes the change visible to these
    /// queries while still keeping it uncommitted. To make that mistake impossible to miss, it refuses to run while the
    /// DbContext holds unsaved changes.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open on the DbContext, or the DbContext holds changes that have not been saved.
    /// </exception>
    public async Task<ProductionOrderResponse> ReadDetailAsync(int number, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "ReadDetailAsync must run inside a write transaction or StockQueries.InReadTransactionAsync (decision #85).");
        }

        // ChangeTracker is EF Core's list of the objects this DbContext has loaded or added, and what changed on them.
        // HasChanges() is true while any of those changes is still unsaved.
        if (db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "ReadDetailAsync reads the database; call SaveChangesAsync first so the response includes this request's changes.");
        }

        // Include tells EF Core to load the related rows too, so the mapping finds the product's unit, the people and the
        // lines in memory. AsSplitQuery makes EF Core load them with a few separate SELECTs (the order with its
        // single-row relations, then its lines with their materials and units) instead of one big JOIN, which would
        // repeat every order column once per line. Separate queries could normally see different moments of the
        // database, but here they all run inside the transaction checked above, so they read the same snapshot.
        // AsNoTracking: these objects are only read, never saved, so EF Core need not keep track of them.
        var order = await db.ProductionOrders
            .AsNoTracking()
            .Include(candidate => candidate.FinishedProduct).ThenInclude(product => product.Unit)
            .Include(candidate => candidate.CreatedBy)
            .Include(candidate => candidate.SentBy)
            .Include(candidate => candidate.CompletedBy)
            .Include(candidate => candidate.CancelledBy)
            .Include(candidate => candidate.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
            ?? throw new NotFoundException($"Order #{number} not found.");

        // AC-14.2: "its complementary orders with their status", read from their own table, oldest first.
        var complementaryOrders = await db.ComplementaryOrders
            .Where(complementary => complementary.ParentOrderNumber == number)
            .OrderBy(complementary => complementary.Number)
            .Select(complementary => new LinkedComplementaryOrderResponse(
                complementary.Number, complementary.Status, complementary.RequestedAt))
            .ToListAsync(cancellationToken);

        // Design 5.7: the estimate exists only while the order is Created.
        var estimateFor = order.Status == OrderStatus.Created
            ? await EstimatorAsync(order, cancellationToken)
            : null;

        return ProductionOrderMapping.ToDetail(order, complementaryOrders, estimateFor);
    }

    /// <summary>
    /// AC-13.2: works out, for each line of a Created order, the estimate design 5.7 shows: leftover applied (D-5),
    /// nominal send and send range (D-7), at the current tolerance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The leftover estimate, in plain words.</b> Leftover from earlier runs stays in production storage (decision #5),
    /// and some of it is free: not committed to the running order and not waiting to be returned (D-4). That free amount
    /// is shared among the orders waiting in Created status, <b>oldest first</b> (D-5, decision #10): the oldest order
    /// takes as much as it plans to use, the next takes what is left, and so on, separately for each material. So each
    /// kilogram counts towards one order only. This order's share is its estimated leftover applied L. The warehouse then
    /// needs to send about P − L (the nominal send), and accepts anything from max(0, P × (1 − t) − L) to
    /// P × (1 + t) − L (D-7).
    /// </para>
    /// <para>
    /// Worked example (AC-15.5): 50 kg of free Cheese, orders #7 (planned 30 kg), #8 (1,000 kg) and #9 (500 kg) created in
    /// that order. #7 takes 30 (nominal 0, range 0 to 0.6), #8 the remaining 20 (nominal 980, range 960 to 1,000), #9
    /// nothing (nominal 500, range 490 to 510).
    /// </para>
    /// <para>
    /// It is an estimate because it is recalculated from current data on every read (AC-13.2): a receipt does not change
    /// it, but a send, cancellation, return or write-off of leftover does. The real figure is fixed on the line only at
    /// send (D-6, T-010), with the same allocator.
    /// </para>
    /// <para>
    /// Only Created orders that are older than this one (or created in the same second with a lower number) are loaded:
    /// younger orders come after it in the queue and cannot change its share.
    /// </para>
    /// </remarks>
    private async Task<Func<ProductionOrderLine, LineEstimateResponse>> EstimatorAsync(
        ProductionOrder order, CancellationToken cancellationToken)
    {
        var materialIds = order.Lines.Select(line => line.RawMaterialId).ToList();

        // D-4 for this order's materials only (architecture 5.1: stock sums are always filtered by item).
        var figures = await stock.GetFiguresAsync(materialIds, cancellationToken);
        var freeLeftover = figures.ToDictionary(pair => pair.Key, pair => pair.Value.FreeLeftover);

        // D-5: the queue up to and including this order. Lines must be loaded: the allocator reads each order's lines,
        // and an order without them would silently take nothing (see LeftoverAllocator.Allocate).
        var queue = await db.ProductionOrders
            .AsNoTracking()
            .Where(waiting => waiting.Status == OrderStatus.Created
                && (waiting.CreatedAt < order.CreatedAt
                    || (waiting.CreatedAt == order.CreatedAt && waiting.Number <= order.Number)))
            .Include(waiting => waiting.Lines)
            .ToListAsync(cancellationToken);

        var allocation = LeftoverAllocator.Allocate(queue, freeLeftover);
        var tolerance = options.Value.TolerancePercent;

        // Decision #94: the tolerance shown in the estimate is a displayed percentage like every other, so it follows the
        // same rule (2 places, decision #58). The range itself is calculated with the configured value, unrounded.
        var shownTolerance = Quantity.DisplayPercent(tolerance);

        return line =>
        {
            var leftoverApplied = allocation.For(order.Number, line.RawMaterialId);

            // D-7 through T-004: limits rounded to 3 places, so the range shown is exactly the range a send will accept
            // (architecture 4.1 item 4, decision #24).
            var range = ProductionCalculations.SendRange(line.PlannedAmount, leftoverApplied, tolerance);
            return new LineEstimateResponse(leftoverApplied, range.Nominal, range.Min, range.Max, shownTolerance);
        };
    }

    /// <summary>
    /// Design 2.2 step 4 for an order body (AC-13.3): the finished product is given and exists; the ordered quantity is
    /// given, above 0, with at most 3 decimal places, and at most 999,999,999.999 (decision #80). Every problem is reported
    /// in one <c>400</c>, keyed by the field.
    /// </summary>
    private async Task<(Item Product, decimal OrderedQuantity)> ValidateAsync(
        ProductionOrderRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        // Design 2.2 step 4 includes "unknown ids in the body": an unknown item is 400, not 404.
        Item? product = null;
        if (request.FinishedProductId is not { } productId)
        {
            errors.Add("finishedProductId", "Finished product is required.");
        }
        else
        {
            product = await db.Items.FindAsync([productId], cancellationToken);
            if (product is null)
            {
                errors.Add("finishedProductId", $"Item {productId} does not exist.");
            }
        }

        // One message per field, from the first rule it fails, in the order of architecture 3.1 (decision #75):
        // required, sign, decimal places, maximum.
        if (request.OrderedQuantity is not { } quantity)
        {
            errors.Add("orderedQuantity", "Ordered quantity is required.");
        }
        else if (quantity <= 0m)
        {
            // AC-13.3: "an ordered quantity of zero or less".
            errors.Add("orderedQuantity", "Ordered quantity must be greater than 0.");
        }
        else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
        {
            // AC-13.3, PRD 4.1: "more than 3 decimal places"; trailing zeros do not count (design 2.4).
            errors.Add("orderedQuantity",
                $"Ordered quantity can have at most {Quantity.MaxQuantityDecimals} decimal places.");
        }
        else if (!Quantity.IsWithinMax(quantity))
        {
            // Decision #80: at most 999,999,999.999. With the recipe limit (999,999.999999 per unit), every planned
            // amount this order can produce still fits decimal(18,3) and the multiplication cannot overflow.
            errors.Add("orderedQuantity", Quantity.AboveMaxMessage("Ordered quantity"));
        }

        errors.ThrowIfAny();
        return (product!, request.OrderedQuantity!.Value);
    }
}
