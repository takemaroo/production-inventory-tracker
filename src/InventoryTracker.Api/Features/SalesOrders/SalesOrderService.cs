using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.SalesOrders;

/// <summary>
/// (v1.1) Creating and reading sales orders (US-24, US-25): create a sales order for an active customer, list sales
/// orders, and show one in full. Also the one builder of the sales order detail, <see cref="ReadDetailAsync"/>, which the
/// ship (T-027) and the cancel (T-028) answer with too (decision #161).
/// </summary>
/// <remarks>
/// <para>
/// <b>What creating a sales order does, in plain words.</b> It records what a customer has ordered, and nothing more: no
/// stock is checked and none moves (AC-24.2). The Warehouse ships it later, in one go (US-26); until then its lines only
/// count as open demand (D-12). So a sales order can be created for an item with no stock at all, and several sales
/// orders can want the same stock (AC-24.6). That is why this class needs neither <c>StockLedger</c> nor
/// <c>StockQueries</c>.
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2, architecture 5.1 v1.1). The framework answers <c>401</c>, <c>403</c> and unreadable
/// bodies first. Then, here: the line cap (<c>400</c> <c>too-many-lines</c>), on its own and before the write
/// transaction; then, inside one write transaction, every field check in one <c>400</c>, then <c>409</c>
/// <c>customer-inactive</c>, then the insert.
/// </para>
/// <para>
/// <c>ILogger&lt;T&gt;</c> is the framework's logger, handed in by dependency injection like the DbContext; the
/// <c>T</c> names the log category, so a log line says which class wrote it.
/// </para>
/// </remarks>
public sealed class SalesOrderService(AppDbContext db, Clock clock, ILogger<SalesOrderService> logger)
{
    /// <summary>Design 7.2: the code for a sales order for a deactivated customer (AC-24.4).</summary>
    public const string CustomerInactiveCode = "customer-inactive";

    /// <summary>
    /// AC-25.1: one page of sales order summaries, newest first (design 2.5), optionally filtered by <c>status</c>,
    /// <c>itemId</c> (sales orders with at least one line for that item) and <c>customerId</c>, combined with AND.
    /// <c>400</c> for paging outside its limits or a status that is not Created, Shipped or Cancelled, every problem in
    /// one response. An id that matches nothing gives an empty page, not an error (design 2.5).
    /// </summary>
    public async Task<PagedResult<SalesOrderSummaryResponse>> ListAsync(
        PageQuery page, string? status, int? itemId, int? customerId, CancellationToken cancellationToken)
    {
        // One collector for paging and the status filter, so every bad query value is reported in one response.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var statusFilter = EnumParser.ParseOptional<SalesOrderStatus>(status, "status", "status", errors);
        errors.ThrowIfAny();

        // Each filter narrows the same query, so they combine with AND (design 2.5). Architecture 3.5 names the index
        // that serves each: Status, CustomerId, and for the item an EXISTS on the SalesOrderLines ItemId index.
        var query = db.SalesOrders.AsQueryable();
        if (statusFilter is { } wantedStatus)
        {
            query = query.Where(order => order.Status == wantedStatus);
        }

        if (customerId is { } wantedCustomer)
        {
            query = query.Where(order => order.CustomerId == wantedCustomer);
        }

        if (itemId is { } wantedItem)
        {
            query = query.Where(order => order.Lines.Any(line => line.ItemId == wantedItem));
        }

        // Design 2.5 (v1.1): newest first by creation time, then by number for sales orders created in the same second,
        // so the order, and which rows land on which page, never depends on the database.
        return await query
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Number)
            .Select(SalesOrderMapping.AsSummary)
            .ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>
    /// AC-25.2: one sales order in full (design 5.12), in any status, or <c>404</c> "Sales order #{number} not found."
    /// (AC-25.3).
    /// </summary>
    public Task<SalesOrderResponse> GetAsync(int number, CancellationToken cancellationToken) =>
        ReadDetailAsync(number, cancellationToken);

    /// <summary>
    /// AC-24.1: creates a sales order for an active customer: the next sales order number (D-10), status Created, who and
    /// when, the trimmed customer reference, and one line per body line in the order sent. Moves and checks no stock
    /// (AC-24.2). Returns the new sales order's detail (<c>201</c>).
    /// </summary>
    /// <param name="request">The body: customer, optional customer reference, lines.</param>
    /// <param name="userId">The signed-in Seller or Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="ValidationException">
    /// <c>400</c> <c>too-many-lines</c> for more than <see cref="RequestLimits.MaxLines"/> lines (AC-24.5), on its own;
    /// otherwise <c>400</c> <c>validation</c> with every field problem (AC-24.3).
    /// </exception>
    /// <exception cref="ConflictException"><c>409</c> <c>customer-inactive</c> (AC-24.4).</exception>
    public async Task<SalesOrderResponse> CreateAsync(
        CreateSalesOrderRequest request, string userId, CancellationToken cancellationToken)
    {
        // AC-24.5, SEC-01: refused before the write transaction, and before every other check, the customer included
        // (design 2.2). Why, and what stays inside: RequestLimits.CheckLineCount.
        RequestLimits.CheckLineCount(request.Lines);

        var response = await db.InWriteTransactionAsync(async () =>
        {
            // All field checks first (AC-24.3), then the customer's state (AC-24.4): a body with a bad line and a
            // deactivated customer gets the 400 only (design 2.2, "field checks first").
            var input = await ValidateAsync(request, cancellationToken);

            if (!input.Customer.IsActive)
            {
                // AC-24.4 with decision #152's next step. Checked inside the write transaction, so a deactivation that
                // commits first is always seen (architecture 5.2, "Deactivation and a new sales order").
                throw new ConflictException(CustomerInactiveCode,
                    $"Customer {input.Customer.Name} is deactivated. Reactivate the customer or choose another one.");
            }

            var now = clock.UtcNow;
            var salesOrder = new SalesOrder
            {
                // AC-24.1: only the customer's id is stored, never its name, so the sales order always shows the
                // customer's current name (AC-29.3).
                CustomerId = input.Customer.Id,
                CustomerReference = input.CustomerReference,
                Status = SalesOrderStatus.Created,

                // AC-24.1, AC-05.3: who created it and when. While it is Created, its last status change is its creation
                // (StatusChangedAt is the list's statusChangedAt, architecture 5.1).
                CreatedAt = now,
                CreatedById = userId,
                StatusChangedAt = now,

                // AC-24.1: lines keep the order they were sent in, stored as LineNo from 1 (decision #162).
                Lines = input.Lines
                    .Select((line, position) => new SalesOrderLine
                    {
                        LineNo = position + 1,
                        ItemId = line.ItemId,
                        Quantity = line.Quantity,
                    })
                    .ToList(),
            };
            db.SalesOrders.Add(salesOrder);

            // The number is generated by the database (its own sequence, D-10), so it is still 0 here. Saving inside the
            // write transaction gives it the next number; writers run one at a time (architecture 5.2), so two sales
            // orders created at the same moment get different numbers. Nothing is permanent until the commit.
            await db.SaveChangesAsync(cancellationToken);
            return await ReadDetailAsync(salesOrder.Number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging, decision #163: after the commit, so the log never reports a sales order that was rolled
        // back. Only the number and the user id: the customer's notes are never logged.
        logger.LogInformation("Sales order {SalesOrderNumber} created by user {UserId}.", response.Number, userId);
        return response;
    }

    /// <summary>
    /// The detail of sales order <paramref name="number"/> (design 5.12), or <c>404</c> "Sales order #{number} not
    /// found.". The one builder of the detail (architecture 3.5, decision #161): <c>GET</c>, the create, and later the
    /// ship (T-027) and the cancel (T-028) all answer with it, so their responses cannot differ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One query.</b> <c>Include</c> tells EF Core to load the related rows with the sales order: the customer, the
    /// people, and the lines with their items and units. Without <c>AsSplitQuery</c>, EF Core loads them all with one SQL
    /// statement and its joins, and one statement always reads one consistent state of the database, so a plain read
    /// needs no read transaction (unlike the production order detail, whose estimate combines several queries).
    /// <c>AsNoTracking</c>: these objects are only read, never saved.
    /// </para>
    /// <para>
    /// <b>Call it after <c>SaveChangesAsync</c></b> when answering a change. It reads the database, not the objects the
    /// caller holds in memory, so a change that is only tracked, not saved, would be missing from the response. Inside
    /// the open write transaction the saved change is visible to this query while still uncommitted. To make that
    /// mistake impossible to miss, it refuses to run while the DbContext holds unsaved changes.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The DbContext holds changes that have not been saved.</exception>
    public async Task<SalesOrderResponse> ReadDetailAsync(int number, CancellationToken cancellationToken)
    {
        // ChangeTracker is EF Core's list of the objects this DbContext has loaded or added, and what changed on them.
        if (db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "ReadDetailAsync reads the database; call SaveChangesAsync first so the response includes this request's changes.");
        }

        var salesOrder = await db.SalesOrders
            .AsNoTracking()
            .Include(order => order.Customer)
            .Include(order => order.CreatedBy)
            .Include(order => order.ShippedBy)
            .Include(order => order.CancelledBy)
            .Include(order => order.Lines).ThenInclude(line => line.Item).ThenInclude(item => item.Unit)
            .SingleOrDefaultAsync(order => order.Number == number, cancellationToken)
            ?? throw new NotFoundException($"Sales order #{number} not found.");

        return SalesOrderMapping.ToDetail(salesOrder);
    }

    /// <summary>
    /// Design 2.2 step 4 for a sales order body (AC-24.3; architecture 4.1 item 16; design 7.2 "Sales orders"). Every
    /// problem is reported in one <c>400</c> <c>validation</c>, keyed by the field's JSON path, in this order:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>customerId</c>: "Customer is required.", or "Customer {customerId} does not exist." for an id that names
    /// no customer, a deleted one included (AC-29.8).</item>
    /// <item><c>customerReference</c>: optional, trimmed, blank becomes null; over 100 characters is "Customer reference
    /// must be 100 characters or fewer.".</item>
    /// <item><c>lines</c>: missing, <c>null</c> or empty is "At least one line is required.".</item>
    /// <item>Each line, in body order, one message per field from the first rule it fails (decision #75): the item is
    /// given, exists, and is not on an earlier line; the quantity is given, above 0, has at most 3 decimal places and is
    /// at most 999,999,999.999. A line sent as <c>null</c> gets both "Item is required." and "Quantity is required.".</item>
    /// </list>
    /// <para>
    /// There is no item-type rule: raw materials and finished products are both sold (decision #144, AC-24.8). The lines
    /// are new, not matched to an existing order, so <c>OrderLineMatcher</c> does not apply (architecture 4.1 item 16).
    /// </para>
    /// </remarks>
    private async Task<ValidSalesOrder> ValidateAsync(CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        // Design 2.2 step 4 includes "unknown ids in the body": an unknown customer is 400, not 404. Only the three
        // columns the rules need are read (id, name, active flag); nothing here is changed, so nothing is tracked.
        CustomerFacts? customer = null;
        if (request.CustomerId is not { } customerId)
        {
            errors.Add("customerId", "Customer is required.");
        }
        else
        {
            customer = await db.Customers
                .Where(candidate => candidate.Id == customerId)
                .Select(candidate => new CustomerFacts(candidate.Id, candidate.Name, candidate.IsActive))
                .SingleOrDefaultAsync(cancellationToken);
            if (customer is null)
            {
                errors.Add("customerId", $"Customer {customerId} does not exist.");
            }
        }

        // AC-24.1, design 2.6: optional; trimmed, and blank becomes null, so "no reference" is stored one way only.
        // Decision #116: the length counts Unicode code points, so an emoji is one character.
        var customerReference = Text.Clean(request.CustomerReference);
        if (Text.Length(customerReference) > ColumnRules.CustomerReferenceLength)
        {
            errors.Add("customerReference",
                $"Customer reference must be {ColumnRules.CustomerReferenceLength} characters or fewer.");
        }

        var lines = request.Lines is { Count: > 0 } submitted
            ? await ValidateLinesAsync(submitted, errors, cancellationToken)
            : null;
        if (lines is null)
        {
            // AC-24.3: "no lines". A missing or null list counts as an empty one.
            errors.Add("lines", "At least one line is required.");
        }

        errors.ThrowIfAny();

        // ThrowIfAny has thrown if the customer or the lines were missing, so neither is null here; "!" tells the
        // compiler so.
        return new ValidSalesOrder(customer!, customerReference, lines!);
    }

    /// <summary>
    /// AC-24.3: checks every submitted line (see <see cref="ValidateAsync"/>), adding each problem to
    /// <paramref name="errors"/>, and returns every line that has an item id and a valid quantity, in body order. That
    /// list may still hold an unknown or repeated item; the caller uses it only after <c>ThrowIfAny</c> has passed, when
    /// no line has any problem left.
    /// </summary>
    private async Task<List<ValidLine>> ValidateLinesAsync(
        IReadOnlyList<SalesOrderLineRequest?> submitted, ValidationErrors errors, CancellationToken cancellationToken)
    {
        // Architecture 4.1 item 16: the item names are loaded in one query for all ids (WHERE Id IN (...)), never one
        // query per line. An id that names no item is simply absent from the dictionary.
        var itemIds = submitted.Select(line => line?.ItemId).OfType<int>().Distinct().ToList();
        var itemNames = await db.Items
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

        var valid = new List<ValidLine>(submitted.Count);
        var itemsSeen = new HashSet<int>();
        for (var index = 0; index < submitted.Count; index++)
        {
            var line = submitted[index];
            var itemPath = $"lines[{index}].itemId";

            if (line?.ItemId is not { } itemId)
            {
                errors.Add(itemPath, "Item is required.");
            }
            else if (!itemNames.TryGetValue(itemId, out var itemName))
            {
                // Design 2.2 step 4: an unknown id in the body is 400, not 404.
                errors.Add(itemPath, $"Item {itemId} does not exist.");
            }
            else if (!itemsSeen.Add(itemId))
            {
                // AC-24.3: "the same item on two lines". The first line naming it is kept and every later one is
                // reported, so the message points at the line to remove (decision #75). The unique index on
                // (sales order, item) is the database's backstop.
                errors.Add(itemPath, $"{itemName} appears on more than one line.");
            }

            var quantity = CheckQuantity(line?.Quantity, $"lines[{index}].quantity", errors);

            // Collected whenever both values are present; an item error on this line is already in errors, and
            // ThrowIfAny refuses the body before this list is ever used.
            if (line?.ItemId is { } validItem && quantity is { } validQuantity)
            {
                valid.Add(new ValidLine(validItem, validQuantity));
            }
        }

        return valid;
    }

    /// <summary>
    /// AC-24.3, design 7.2: one line's quantity, with one message from the first rule it fails, in the order of
    /// decision #75: required, greater than 0, at most 3 decimal places, at most 999,999,999.999 (decision #80). Returns
    /// the quantity when it passes, otherwise null.
    /// </summary>
    private static decimal? CheckQuantity(decimal? value, string path, ValidationErrors errors)
    {
        if (value is not { } quantity)
        {
            errors.Add(path, "Quantity is required.");
        }
        else if (quantity <= 0m)
        {
            errors.Add(path, "Quantity must be greater than 0.");
        }
        else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
        {
            // PRD 4.1: trailing zeros do not count, so 12.5000 is 12.5 (design 2.4).
            errors.Add(path, $"Quantity can have at most {Quantity.MaxQuantityDecimals} decimal places.");
        }
        else if (!Quantity.IsWithinMax(quantity))
        {
            // Decision #80: every entered quantity is at most 999,999,999.999, so it fits the decimal(18,3) column and
            // any sum a later report or open-demand figure makes from a few of them.
            errors.Add(path, Quantity.AboveMaxMessage("Quantity"));
        }
        else
        {
            return quantity;
        }

        return null;
    }

    /// <summary>What the create needs to know about the chosen customer: its id, current name and active status.</summary>
    private sealed record CustomerFacts(int Id, string Name, bool IsActive);

    /// <summary>
    /// A line with an item id and a valid quantity. It is only used after <c>ThrowIfAny</c> has passed, so by then the item
    /// is known to exist and not to be repeated.
    /// </summary>
    private sealed record ValidLine(int ItemId, decimal Quantity);

    /// <summary>A sales order body that passed every field check: the customer, the trimmed reference or null, the lines.</summary>
    private sealed record ValidSalesOrder(CustomerFacts Customer, string? CustomerReference, List<ValidLine> Lines);
}
