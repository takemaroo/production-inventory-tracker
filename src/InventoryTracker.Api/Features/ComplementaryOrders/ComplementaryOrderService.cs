using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Features.ComplementaryOrders;

/// <summary>
/// Reading complementary orders (US-16, design 5.8) and the Manager's decision on them (US-17): approve or reject a
/// request for extra material.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Manager decides.</b> One send per order is the rule (decision #4); extra material during a run only leaves
/// the warehouse through a complementary order, with a reason and a Manager's approval, because extra material is where
/// waste hides. Approving makes the request sendable (T-018); rejecting ends it for good (AC-17.2). A Manager may approve
/// a request they raised themselves (PRD section 6 row 6, decision #14), so nothing here compares the approver with the
/// requester.
/// </para>
/// <para>
/// <b>One decision per request, even under simultaneous requests</b> (architecture 5.2). Approve and reject each run
/// inside one <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the database's single
/// write lock from the first read to the commit. The status is checked and changed inside that lock, so when two
/// Managers decide on the same request at the same moment, the second one waits, then sees the first one's decision and
/// gets <c>409</c> <c>not-pending-approval</c> naming it. The completion and the cancellation of the parent order cancel
/// its unsent complementary orders inside their own write transactions (<c>UnsentComplementaryOrders</c>), so a decision
/// and the parent's end can never interleave either: a decision that comes second finds Cancelled.
/// </para>
/// <para>
/// <b>Check order</b> (design 2.2): the complementary order in the URL (<c>404</c>), the body (<c>400</c>, reject's note
/// only), then the state (<c>409</c>). Nothing here moves stock: material moves only when an approved request is sent
/// (T-018).
/// </para>
/// </remarks>
public sealed class ComplementaryOrderService(
    AppDbContext db, Clock clock, IOptions<InventoryOptions> options, ILogger<ComplementaryOrderService> logger)
{
    /// <summary>Design 7.2: the code for approving or rejecting a complementary order that is not PendingApproval (AC-17.3).</summary>
    public const string NotPendingApprovalCode = "not-pending-approval";

    /// <summary>
    /// <c>GET /complementary-orders</c>: one page of complementary orders, newest first (PRD 4.1, design 2.5), optionally
    /// only one status (<c>?status=Approved</c>) and/or one parent order (<c>?orderNumber=12</c>), combined with AND.
    /// </summary>
    /// <remarks>
    /// Design 3.7 uses these filters as work queues: a Manager looks for <c>status=PendingApproval</c>, the warehouse for
    /// <c>status=Approved</c>. An unknown status is <c>400</c>; an order number that matches nothing is an empty page.
    /// </remarks>
    public async Task<PagedResult<ComplementaryOrderResponse>> ListAsync(
        PageQuery page, string? status, int? orderNumber, CancellationToken cancellationToken)
    {
        // One collector for paging and the status filter, so every bad query value is reported in one response.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var statusFilter = EnumParser.ParseOptional<ComplementaryOrderStatus>(status, "status", "status", errors);
        errors.ThrowIfAny();

        var query = db.ComplementaryOrders.AsQueryable();
        if (statusFilter is { } wanted)
        {
            query = query.Where(complementary => complementary.Status == wanted);
        }

        if (orderNumber is { } parent)
        {
            query = query.Where(complementary => complementary.ParentOrderNumber == parent);
        }

        // Design 2.5: newest first by creation (request) time, then by number for requests made in the same second.
        var rows = await WithDetails(query)
            .OrderByDescending(complementary => complementary.RequestedAt)
            .ThenByDescending(complementary => complementary.Number)
            .ToPagedResultAsync(page, cancellationToken);

        var tolerance = options.Value.TolerancePercent;
        return new PagedResult<ComplementaryOrderResponse>(
            rows.Items.Select(row => ComplementaryOrderMapping.ToResponse(row, tolerance)).ToList(),
            rows.Page, rows.PageSize, rows.TotalCount);
    }

    /// <summary>
    /// <c>GET /complementary-orders/{number}</c>: one complementary order (design 5.8), or <c>404</c> "Complementary order
    /// #{number} not found." (design 7.2).
    /// </summary>
    public Task<ComplementaryOrderResponse> GetAsync(int number, CancellationToken cancellationToken) =>
        ReadAsync(number, cancellationToken);

    /// <summary>
    /// The response of complementary order <paramref name="number"/> as stored (design 5.8), or <c>404</c>. The one way to
    /// build it, used by <c>GET</c>, approve and reject here, by the request (<see cref="RequestComplementaryOrderService"/>)
    /// and meant for T-018's send, so every endpoint returns the same shape.
    /// </summary>
    /// <remarks>
    /// It reads the database, not objects in memory (<c>AsNoTracking</c>), so a caller that has changed the complementary
    /// order calls <c>SaveChangesAsync</c> first. Saving inside the caller's open write transaction makes the change
    /// visible to this query while it stays uncommitted for everyone else. It is a single SQL query, so all of it comes
    /// from one moment of the database even outside a transaction.
    /// </remarks>
    /// <exception cref="NotFoundException">No complementary order has this number.</exception>
    public async Task<ComplementaryOrderResponse> ReadAsync(int number, CancellationToken cancellationToken)
    {
        var order = await WithDetails(db.ComplementaryOrders)
            .SingleOrDefaultAsync(complementary => complementary.Number == number, cancellationToken)
            ?? throw NotFound(number);

        return ComplementaryOrderMapping.ToResponse(order, options.Value.TolerancePercent);
    }

    /// <summary>
    /// AC-17.1: a Manager approves PendingApproval complementary order <paramref name="number"/>. It becomes Approved,
    /// recording who and when (AC-05.3), and can now be sent (T-018). Returns it (design 5.8).
    /// </summary>
    /// <param name="number">The complementary order number from the URL.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no complementary order has this number.</exception>
    /// <exception cref="ConflictException"><c>409</c> <c>not-pending-approval</c>: it is not PendingApproval (AC-17.3).</exception>
    public async Task<ComplementaryOrderResponse> ApproveAsync(
        int number, string userId, CancellationToken cancellationToken)
    {
        var response = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3, then step 5 (approve has no body, so there is no step 4).
            var order = await LoadForDecisionAsync(number, cancellationToken);
            CheckPendingApproval(order);

            // AC-17.1, AC-05.3: Approved, with who and when. Decision #14: the approver may be the requester.
            order.Status = ComplementaryOrderStatus.Approved;
            order.ApprovedAt = clock.UtcNow;
            order.ApprovedById = userId;

            await db.SaveChangesAsync(cancellationToken);
            return await ReadAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change, with the number and the user, only after the commit.
        logger.LogInformation("Complementary order {ComplementaryOrderNumber} approved by user {UserId}.", number, userId);
        return response;
    }

    /// <summary>
    /// AC-17.2: a Manager rejects PendingApproval complementary order <paramref name="number"/>, with an optional note. It
    /// becomes Rejected, recording who, when and the note (AC-05.3), and can never be sent: only an Approved complementary
    /// order can be sent (AC-18.3), and a Rejected one can never become Approved (AC-17.3). Returns it (design 5.8).
    /// </summary>
    /// <param name="number">The complementary order number from the URL.</param>
    /// <param name="request">The body with the optional note; <c>null</c> when no body was sent.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no complementary order has this number.</exception>
    /// <exception cref="ValidationException"><c>400</c> <c>validation</c>: the note is longer than 500 characters.</exception>
    /// <exception cref="ConflictException"><c>409</c> <c>not-pending-approval</c>: it is not PendingApproval (AC-17.3).</exception>
    public async Task<ComplementaryOrderResponse> RejectAsync(
        int number, RejectComplementaryOrderRequest? request, string userId, CancellationToken cancellationToken)
    {
        var response = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2: step 3 (the record), step 4 (the note), step 5 (the state).
            var order = await LoadForDecisionAsync(number, cancellationToken);
            var note = ValidateNote(request);
            CheckPendingApproval(order);

            // AC-17.2, AC-05.3: Rejected, with who, when and the optional note.
            order.Status = ComplementaryOrderStatus.Rejected;
            order.RejectedAt = clock.UtcNow;
            order.RejectedById = userId;
            order.DecisionNote = note;

            await db.SaveChangesAsync(cancellationToken);
            return await ReadAsync(number, cancellationToken);
        }, cancellationToken);

        logger.LogInformation("Complementary order {ComplementaryOrderNumber} rejected by user {UserId}.", number, userId);
        return response;
    }

    /// <summary>
    /// Every navigation the response needs, so <see cref="ComplementaryOrderMapping"/> finds them in memory: the five
    /// people and the lines with their materials and units.
    /// </summary>
    /// <remarks>
    /// <c>Include</c> tells EF Core to load related rows in the same query (a SQL JOIN). There is only one collection,
    /// the lines, so the JOIN repeats the complementary order's columns once per line, which for a handful of lines is
    /// cheaper than separate queries. <c>AsNoTracking</c>: these objects are only read, never saved.
    /// </remarks>
    private static IQueryable<ComplementaryOrder> WithDetails(IQueryable<ComplementaryOrder> query) => query
        .AsNoTracking()
        .Include(complementary => complementary.RequestedBy)
        .Include(complementary => complementary.ApprovedBy)
        .Include(complementary => complementary.RejectedBy)
        .Include(complementary => complementary.SentBy)
        .Include(complementary => complementary.CancelledBy)
        .Include(complementary => complementary.Lines).ThenInclude(line => line.RawMaterial).ThenInclude(item => item.Unit);

    /// <summary>
    /// Design 2.2 step 3: the complementary order named in the URL, tracked (no <c>AsNoTracking</c>) because the decision
    /// is written onto it, or <c>404</c>.
    /// </summary>
    private async Task<ComplementaryOrder> LoadForDecisionAsync(int number, CancellationToken cancellationToken) =>
        await db.ComplementaryOrders.SingleOrDefaultAsync(complementary => complementary.Number == number, cancellationToken)
        ?? throw NotFound(number);

    /// <summary>
    /// Design 2.2 step 5, AC-17.3: only a PendingApproval complementary order can be approved or rejected. Any other status
    /// is <c>409</c> <c>not-pending-approval</c> "Complementary order #{n} is {status}. Only a PendingApproval complementary
    /// order can be approved or rejected." (design 7.2). This is also what the loser of two simultaneous decisions gets,
    /// naming the winner's decision.
    /// </summary>
    /// <remarks>
    /// Why every other status is final for a decision: Approved and Rejected have been decided already, and a decision is
    /// never changed; Sent has moved its material; Cancelled belongs to a parent order that has ended (AC-19.6, AC-21.4).
    /// Refusing Rejected → Approved is what makes AC-17.2's "can never be sent" hold.
    /// </remarks>
    private static void CheckPendingApproval(ComplementaryOrder order)
    {
        if (order.Status != ComplementaryOrderStatus.PendingApproval)
        {
            throw new ConflictException(NotPendingApprovalCode,
                $"Complementary order #{order.Number} is {order.Status}. Only a PendingApproval complementary order can be approved or rejected.");
        }
    }

    /// <summary>
    /// Design 2.2 step 4, AC-17.2, design 2.6: the rejection note is optional. Missing, <c>null</c>, empty or only spaces
    /// means no note; otherwise it is trimmed and must be 500 characters or fewer, else <c>400</c> <c>validation</c> "Note
    /// must be 500 characters or fewer." under <c>note</c> (design 7.2, "Note or reason over 500").
    /// </summary>
    /// <returns>The trimmed note, or <c>null</c>.</returns>
    private static string? ValidateNote(RejectComplementaryOrderRequest? request)
    {
        var note = Text.Clean(request?.Note);
        if (note is { Length: > ColumnRules.TextLength })
        {
            throw new ValidationException("note", $"Note must be {ColumnRules.TextLength} characters or fewer.");
        }

        return note;
    }

    /// <summary>Design 7.2: <c>404</c> "Complementary order #{number} not found.".</summary>
    private static NotFoundException NotFound(int number) => new($"Complementary order #{number} not found.");
}
