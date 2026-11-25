using System.Linq.Expressions;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Customers;

/// <summary>
/// (v1.1) Every rule for customers (US-29): list, read, create, edit, deactivate, reactivate and delete. Sales orders pick
/// their customer from this list (AC-24.1), so it is master data, like units and items.
/// </summary>
/// <remarks>
/// <para>
/// Built like <c>UnitService</c> and <c>ItemService</c>: reads are plain queries; every change runs inside
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>, so a refused request saves nothing; a broken rule is thrown
/// as an <see cref="ApiException"/> and the central handler writes the design 6 error body. The checks follow the order
/// of architecture 5.1 (v1.1): the customer in the URL (<c>404</c>), then name and notes (<c>400</c>), then the state
/// rules (<c>409</c>).
/// </para>
/// <para>
/// <b>No logging here, on purpose</b> (decision #163, architecture 6 Logging). Customers are master data, and units and
/// items are not logged either. More importantly, a customer's notes may hold business contact details (an address, a
/// phone number), and log files are read and kept far more loosely than the database. So nothing in this class writes a
/// log line, and the error messages it throws never quote the notes.
/// </para>
/// </remarks>
public sealed class CustomerService(AppDbContext db, Clock clock)
{
    /// <summary>Design 7.2: the code for a name another customer already has, ignoring case (AC-29.2).</summary>
    public const string CustomerNameTakenCode = "customer-name-taken";

    /// <summary>Design 7.2: the code for deleting a customer that has, or ever had, a sales order (AC-29.5).</summary>
    public const string CustomerHasSalesOrdersCode = "customer-has-sales-orders";

    /// <summary>
    /// The one definition of how a customer becomes its response (design 5.11). It is an
    /// <see cref="Expression{TDelegate}"/>, a description of the code rather than compiled code, so EF Core translates it
    /// into SQL that joins <c>AspNetUsers</c> for the creator's email and reads only these columns. Every endpoint returns
    /// a customer through it, so the list, one customer and the answer to a change can never differ in shape.
    /// </summary>
    private static readonly Expression<Func<Customer, CustomerResponse>> AsResponse = customer => new CustomerResponse(
        customer.Id,
        customer.Name,
        customer.Notes,
        customer.IsActive,
        customer.CreatedAt,
        new PersonResponse(customer.CreatedBy.Id, customer.CreatedBy.Email!));

    /// <summary>
    /// AC-29.6: one page of customers, newest first (design 2.5), optionally only active (<c>?isActive=true</c>) or only
    /// deactivated ones (<c>?isActive=false</c>). Left out, every customer is listed. <c>400</c> for paging outside its
    /// limits or an <c>isActive</c> that is not <c>true</c> or <c>false</c>, all problems in one response.
    /// </summary>
    public async Task<PagedResult<CustomerResponse>> ListAsync(
        PageQuery page, string? isActive, CancellationToken cancellationToken)
    {
        // One collector for paging and the filter, so every bad query value is reported in one response (design 6).
        var errors = new ValidationErrors();
        page.Validate(errors);
        var activeFilter = BoolParser.ParseOptional(isActive, "isActive", errors);
        errors.ThrowIfAny();

        var query = db.Customers.AsQueryable();
        if (activeFilter is { } wanted)
        {
            query = query.Where(customer => customer.IsActive == wanted);
        }

        // Design 2.5 (v1.1): newest first by creation time, then by id for customers created in the same second, so the
        // order, and which rows land on which page, never depends on the database. Uses the CreatedAt index.
        return await query
            .OrderByDescending(customer => customer.CreatedAt)
            .ThenByDescending(customer => customer.Id)
            .Select(AsResponse)
            .ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>AC-29.6: one customer, active or not, or <c>404</c> "Customer {id} not found.".</summary>
    public async Task<CustomerResponse> GetAsync(int id, CancellationToken cancellationToken) =>
        await ReadAsync(id, cancellationToken) ?? throw NotFound(id);

    /// <summary>
    /// AC-29.1, AC-29.2: creates an active customer with a unique name and optional notes, recording
    /// <paramref name="userId"/> and the current time as who created it and when (AC-05.3).
    /// </summary>
    public Task<CustomerResponse> CreateAsync(CustomerRequest request, string userId, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var input = Validate(request);

            // AC-29.2. Checked inside the write transaction: writers run one at a time (architecture 5.2), so of two
            // simultaneous creates of one name the second already sees the first and gets this 409.
            await ThrowIfNameTakenAsync(input.Name, exceptId: null, cancellationToken);

            var customer = new Customer
            {
                // Decision #170: the NFC, trimmed name from NameRules, and its key.
                Name = input.Name.Name,
                NameNormalized = input.Name.Normalized,
                Notes = input.Notes,
                IsActive = true, // AC-29.1: a new customer is active
                CreatedAt = clock.UtcNow,
                CreatedById = userId,
            };
            db.Customers.Add(customer);

            // The id is generated by the database, so it is still 0 here. Save now, still inside the transaction, so the
            // 201 response and its Location header carry the real id. Nothing is permanent until the commit.
            await db.SaveChangesAsync(cancellationToken);
            return (await ReadAsync(customer.Id, cancellationToken))!;
        }, cancellationToken);

    /// <summary>
    /// AC-29.3: replaces a customer's name and notes, under the same rules as creating one. Its own name in another letter
    /// case is allowed; leaving <c>notes</c> out clears them (full replacement, design 5.11). <c>404</c> for an unknown id,
    /// before any body check.
    /// </summary>
    /// <remarks>
    /// Sales orders store only the customer's id, never a copy of the name, so every sales order and report shows the new
    /// name at once, older ones included (AC-29.3, architecture 4.1 item 17). Nothing else needs updating here.
    /// </remarks>
    public Task<CustomerResponse> UpdateAsync(int id, CustomerRequest request, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            // Architecture 5.1: the record in the URL (404) comes before the body checks (400). FindAsync looks the row up
            // by primary key and keeps it tracked, so the changes below are saved by SaveChangesAsync.
            var customer = await db.Customers.FindAsync([id], cancellationToken) ?? throw NotFound(id);

            var input = Validate(request);

            // Only *another* customer with the same normalized name counts, so "Deli Norte" can become "DELI NORTE".
            await ThrowIfNameTakenAsync(input.Name, exceptId: id, cancellationToken);

            customer.Name = input.Name.Name;
            customer.NameNormalized = input.Name.Normalized;
            customer.Notes = input.Notes;

            // Saved first, so the read below (a database query, not the tracked object) sees the new values.
            await db.SaveChangesAsync(cancellationToken);
            return (await ReadAsync(id, cancellationToken))!;
        }, cancellationToken);

    /// <summary>
    /// AC-29.4: deactivates a customer, whatever its sales orders. A deactivated customer cannot be chosen for a new sales
    /// order, but stays listed and readable. Deactivating one already deactivated succeeds and changes nothing.
    /// </summary>
    public Task<CustomerResponse> DeactivateAsync(int id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: false, cancellationToken);

    /// <summary>
    /// AC-29.4: reactivates a customer, so it can be chosen for new sales orders again. Reactivating an active customer
    /// succeeds and changes nothing.
    /// </summary>
    public Task<CustomerResponse> ReactivateAsync(int id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: true, cancellationToken);

    /// <summary>
    /// AC-29.5: deletes a customer that no sales order, in any status, has ever named. Otherwise <c>409</c>
    /// <c>customer-has-sales-orders</c> "{customer} has sales orders and cannot be deleted. Deactivate it instead.";
    /// <c>404</c> for an unknown id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check and the delete run in one write transaction, so no sales order can be created for this customer in
    /// between (AC-29.8). Sales orders are never deleted (PRD section 5), so "has a sales order now" is the same as "ever
    /// had one": a Cancelled sales order counts, because it still shows the customer (decision #148).
    /// </para>
    /// <para>
    /// The <c>Restrict</c> foreign key from <c>SalesOrders</c> is the database's own backstop, but only a last one: if it
    /// ever refused the delete, the answer would be <c>500</c>, because a FOREIGN KEY failure means this check is wrong
    /// (decision #46). This check is what gives the client the readable <c>409</c>. Sales orders are the only table that
    /// refers to customers; <c>CustomersTests.Has_sales_orders_check_covers_every_foreign_key_to_customers</c> fails if
    /// another is added.
    /// </para>
    /// <para>
    /// After the delete the row is gone, so its name is free for a new customer (the unique index no longer holds it). Its
    /// id is never handed out again (AUTOINCREMENT, decision #162), so the old id stays <c>404</c> for ever.
    /// </para>
    /// </remarks>
    public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var customer = await db.Customers.FindAsync([id], cancellationToken) ?? throw NotFound(id);

            // AC-29.5: any sales order, whatever its status, so no status filter. Answered by the CustomerId index.
            if (await db.SalesOrders.AnyAsync(order => order.CustomerId == id, cancellationToken))
            {
                // Decision #152: the PRD sentence plus the next step, "Deactivate it instead.".
                throw new ConflictException(CustomerHasSalesOrdersCode,
                    $"{customer.Name} has sales orders and cannot be deleted. Deactivate it instead.");
            }

            db.Customers.Remove(customer);
        }, cancellationToken);

    /// <summary>
    /// AC-29.4: the one implementation of deactivate and reactivate. <c>404</c> for an unknown id; nothing else can fail
    /// (architecture 5.1). Setting the value it already has is a no-op: EF Core compares the tracked values and sends no
    /// <c>UPDATE</c> when nothing changed, so a repeat changes nothing and still answers <c>200</c>.
    /// </summary>
    /// <remarks>
    /// There is no "deactivated by whom and when" column: the PRD does not ask for one (architecture 3.2, Customer).
    /// </remarks>
    private Task<CustomerResponse> SetActiveAsync(int id, bool active, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var customer = await db.Customers.FindAsync([id], cancellationToken) ?? throw NotFound(id);

            customer.IsActive = active;

            await db.SaveChangesAsync(cancellationToken);
            return (await ReadAsync(id, cancellationToken))!;
        }, cancellationToken);

    /// <summary>
    /// The customer's response read from the database through <see cref="AsResponse"/>, or null when there is no such
    /// customer. Used for reads and, inside a write transaction, to answer a change; the same connection is used, so it
    /// sees the transaction's own saved changes.
    /// </summary>
    private Task<CustomerResponse?> ReadAsync(int id, CancellationToken cancellationToken) =>
        db.Customers
            .Where(customer => customer.Id == id)
            .Select(AsResponse)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Architecture 5.1 step "name and notes": checks both fields of a customer body and reports every problem in one
    /// <c>400</c> (AC-29.1, AC-29.2; design 2.6 and 7.2), each under its own field.
    /// </summary>
    private static ValidCustomer Validate(CustomerRequest request)
    {
        var errors = new ValidationErrors();

        // Decisions #170, #171, design 2.6: the name rule shared with units and items (NFC, trim, a name with no visible
        // character counts as missing, length in code points after both).
        var name = NameRules.Validate(request.Name, ColumnRules.CustomerNameLength, errors);

        // AC-29.1, design 2.6: optional; blank becomes null, so "no notes" is stored one way only. Decision #116: the
        // limit counts Unicode code points (Text.Length), as PostgreSQL would count a varchar(500) after the planned port.
        var notes = Text.Clean(request.Notes);
        if (Text.Length(notes) > ColumnRules.TextLength)
        {
            errors.Add("notes", $"Notes must be {ColumnRules.TextLength} characters or fewer.");
        }

        errors.ThrowIfAny();
        return new ValidCustomer(name!, notes);
    }

    /// <summary>
    /// AC-29.2: <c>409</c> <c>customer-name-taken</c> "A customer named '{name}' already exists." when another customer
    /// has this name, ignoring letter case and Unicode form (decision #170). The name is quoted as sent, trimmed and in
    /// NFC (design 7.2), so it looks exactly as typed.
    /// </summary>
    /// <remarks>
    /// The comparison is on <c>NameNormalized</c>, the column the unique index covers (architecture 3.1). Writers run one
    /// at a time, so this check normally decides; if a duplicate ever got past it, the index would refuse the insert and
    /// the shared handler would answer <c>409</c> <c>concurrent-change</c> (architecture 4.1 item 11).
    /// </remarks>
    private async Task ThrowIfNameTakenAsync(CleanName name, int? exceptId, CancellationToken cancellationToken)
    {
        var taken = await db.Customers.AnyAsync(
            customer => customer.NameNormalized == name.Normalized && customer.Id != exceptId, cancellationToken);

        if (taken)
        {
            throw new ConflictException(CustomerNameTakenCode, $"A customer named '{name.Name}' already exists.");
        }
    }

    /// <summary>Design 7.2, AC-29.5, AC-29.6: the <c>404</c> for an unknown or deleted customer id.</summary>
    private static NotFoundException NotFound(int id) => new($"Customer {id} not found.");

    /// <summary>
    /// A customer body that passed every field check: the cleaned name with its key (NameRules) and the trimmed notes
    /// or null.
    /// </summary>
    private sealed record ValidCustomer(CleanName Name, string? Notes);
}
