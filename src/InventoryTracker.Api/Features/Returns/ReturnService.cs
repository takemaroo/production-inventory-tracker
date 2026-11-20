using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// Reading leftover returns (US-22, design 5.9) and a Manager's cancellation of a Pending one (AC-22.5). Also holds the
/// rules every return action shares: the one way to build the response, the <c>404</c> wording and the "only a Pending
/// return" check.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a return is, in plain words</b> (decision #11, PRD section 6 row 3). Leftover raw material sits in production
/// storage after a run. When the Manager decides it should go back to the Warehouse, they instruct a return
/// (<see cref="InstructReturnService"/>): it is Pending, nothing moves yet, but the quantity no longer counts as free
/// leftover (D-4), so no order is planned with it. The warehouse then carries the material back and records it
/// (<see cref="ReceiveReturnService"/>), which moves the stock. If the instruction was a mistake, the Manager cancels it
/// here and the quantity is free leftover again.
/// </para>
/// <para>
/// <b>One outcome per return, even under simultaneous requests</b> (architecture 5.2). Cancel, like receive, runs inside
/// one <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> call, which on SQLite holds the database's single write
/// lock from the first read to the commit. The status is checked and changed inside that lock, so when a cancel and a
/// receive (or two of either) arrive at the same moment, the second waits, then sees the first one's result and gets
/// <c>409</c> <c>not-pending</c> naming it (AC-22.5).
/// </para>
/// </remarks>
public sealed class ReturnService(AppDbContext db, Clock clock, ILogger<ReturnService> logger)
{
    /// <summary>Design 7.2: the code for receiving or cancelling a return that is not Pending (AC-22.5).</summary>
    public const string NotPendingCode = "not-pending";

    /// <summary>
    /// <c>GET /returns</c>: one page of returns, newest first (PRD 4.1, design 2.5), optionally only one status
    /// (<c>?status=Pending</c>) and/or one material (<c>?itemId=3</c>), combined with AND.
    /// </summary>
    /// <remarks>
    /// Design 3.9 uses the status filter as the warehouse's work queue: <c>?status=Pending</c> lists what is waiting to be
    /// carried back. An unknown status is <c>400</c>; an item id that matches nothing (even one that does not exist) is an
    /// empty page (design 2.5).
    /// </remarks>
    public async Task<PagedResult<ReturnResponse>> ListAsync(
        PageQuery page, string? status, int? itemId, CancellationToken cancellationToken)
    {
        // One collector for paging and the status filter, so every bad query value is reported in one response.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var statusFilter = EnumParser.ParseOptional<ReturnStatus>(status, "status", "status", errors);
        errors.ThrowIfAny();

        var query = db.Returns.AsQueryable();
        if (statusFilter is { } wanted)
        {
            query = query.Where(leftoverReturn => leftoverReturn.Status == wanted);
        }

        if (itemId is { } material)
        {
            query = query.Where(leftoverReturn => leftoverReturn.ItemId == material);
        }

        // Design 2.5: newest first by creation (instruction) time, then by number for returns made in the same second.
        var rows = await WithDetails(query)
            .OrderByDescending(leftoverReturn => leftoverReturn.CreatedAt)
            .ThenByDescending(leftoverReturn => leftoverReturn.Number)
            .ToPagedResultAsync(page, cancellationToken);

        return new PagedResult<ReturnResponse>(
            rows.Items.Select(ReturnMapping.ToResponse).ToList(), rows.Page, rows.PageSize, rows.TotalCount);
    }

    /// <summary>
    /// <c>GET /returns/{number}</c>: one return (design 5.9), or <c>404</c> "Return #{number} not found." (design 7.2).
    /// </summary>
    public Task<ReturnResponse> GetAsync(int number, CancellationToken cancellationToken) =>
        ReadAsync(number, cancellationToken);

    /// <summary>
    /// The response of return <paramref name="number"/> as stored (design 5.9), or <c>404</c>. The one way to build it,
    /// used by every return endpoint, so they all return the same shape.
    /// </summary>
    /// <remarks>
    /// It reads the database, not objects in memory (<c>AsNoTracking</c>), so a caller that has changed the return calls
    /// <c>SaveChangesAsync</c> first. Saving inside the caller's open write transaction makes the change visible to this
    /// query while it stays uncommitted for everyone else.
    /// </remarks>
    /// <exception cref="NotFoundException">No return has this number.</exception>
    public async Task<ReturnResponse> ReadAsync(int number, CancellationToken cancellationToken)
    {
        var leftoverReturn = await WithDetails(db.Returns)
            .SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
            ?? throw NotFound(number);

        return ReturnMapping.ToResponse(leftoverReturn);
    }

    /// <summary>
    /// AC-22.5: a Manager cancels Pending return <paramref name="number"/>. It becomes Cancelled, recording who and when
    /// (AC-05.3), and its quantity is free leftover again (D-4 counts only Pending returns). No stock moves: nothing had
    /// moved yet. Returns it (design 5.9).
    /// </summary>
    /// <param name="number">The return number from the URL.</param>
    /// <param name="userId">The signed-in Manager, from the token's <c>sub</c> claim (AC-05.3).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <exception cref="NotFoundException"><c>404</c>: no return has this number.</exception>
    /// <exception cref="ConflictException"><c>409</c> <c>not-pending</c>: it is Received or Cancelled (AC-22.5).</exception>
    public async Task<ReturnResponse> CancelAsync(int number, string userId, CancellationToken cancellationToken)
    {
        var response = await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3, then step 5 (cancel has no body, so there is no step 4). Tracked, because the
            // cancellation is written onto it.
            var leftoverReturn = await db.Returns.SingleOrDefaultAsync(candidate => candidate.Number == number, cancellationToken)
                ?? throw NotFound(number);
            CheckPending(leftoverReturn);

            // AC-22.5, AC-05.3: Cancelled, with who and when.
            leftoverReturn.Status = ReturnStatus.Cancelled;
            leftoverReturn.CancelledAt = clock.UtcNow;
            leftoverReturn.CancelledById = userId;

            await db.SaveChangesAsync(cancellationToken);
            return await ReadAsync(number, cancellationToken);
        }, cancellationToken);

        // Architecture 6, Logging: each state change, with the number and the user, only after the commit.
        logger.LogInformation("Return {ReturnNumber} cancelled by user {UserId}.", number, userId);
        return response;
    }

    /// <summary>
    /// Design 2.2 step 5, AC-22.5: only a Pending return can be received or cancelled. Any other status is <c>409</c>
    /// <c>not-pending</c> "Return #{n} is {status}. Only a Pending return can be received or cancelled." (design 7.2).
    /// This is also what the loser of two simultaneous actions on one return gets, naming the winner's outcome.
    /// </summary>
    /// <remarks>
    /// Why Received and Cancelled are final: a Received return has moved its stock, and the movement can never be undone
    /// (AC-11.3); receiving it again would move the material twice. A Cancelled return has given its quantity back to free
    /// leftover, where the next send may already have used it. A new instruction is the way to return more (decision #18).
    /// </remarks>
    internal static void CheckPending(LeftoverReturn leftoverReturn)
    {
        if (leftoverReturn.Status != ReturnStatus.Pending)
        {
            throw new ConflictException(NotPendingCode,
                $"Return #{leftoverReturn.Number} is {leftoverReturn.Status}. Only a Pending return can be received or cancelled.");
        }
    }

    /// <summary>
    /// Design 7.2: <c>404</c> "Return #{number} not found.". Shared with the receive (<see cref="ReceiveReturnService"/>),
    /// so every return endpoint words it the same way.
    /// </summary>
    internal static NotFoundException NotFound(int number) => new($"Return #{number} not found.");

    /// <summary>
    /// Every navigation the response needs, so <see cref="ReturnMapping"/> finds them in memory: the material and its unit,
    /// and the three people. <c>Include</c> loads them in the same SQL query (a JOIN); <c>AsNoTracking</c> because these
    /// objects are only read, never saved.
    /// </summary>
    private static IQueryable<LeftoverReturn> WithDetails(IQueryable<LeftoverReturn> query) => query
        .AsNoTracking()
        .Include(leftoverReturn => leftoverReturn.Item).ThenInclude(item => item.Unit)
        .Include(leftoverReturn => leftoverReturn.CreatedBy)
        .Include(leftoverReturn => leftoverReturn.ReceivedBy)
        .Include(leftoverReturn => leftoverReturn.CancelledBy);
}
