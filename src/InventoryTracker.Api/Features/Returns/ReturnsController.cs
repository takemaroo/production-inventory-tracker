using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Returns;

/// <summary>
/// The leftover return endpoints (US-22, design 3.9, 4 and 5.9): a Manager instructs or cancels a return, the warehouse
/// (or a Manager) records receiving it, and any signed-in user may list and view them (PRD 4.2).
/// </summary>
/// <remarks>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>CreatedAtAction</c>. All rules are in <see cref="InstructReturnService"/>, <see cref="ReceiveReturnService"/> and
/// <see cref="ReturnService"/>. There is no <c>PUT</c> or <c>DELETE</c>: returns are never edited or deleted (PRD section
/// 5); a wrong one is cancelled and a new one instructed (decision #18).
/// </remarks>
[ApiController]
[Route("api/v1/returns")]
public sealed class ReturnsController(
    InstructReturnService instructions, ReceiveReturnService receipts, ReturnService returns) : ControllerBase
{
    /// <summary>
    /// <c>POST /api/v1/returns</c>: a Manager instructs a return of free leftover (AC-22.1, design 5.9):
    /// <c>{ "itemId": 3, "quantity": 20, "reason": "...", "orderNumber": 9 }</c>, the order optional. <c>201</c> with the
    /// new return, Pending, and <c>Location: /api/v1/returns/{n}</c>; no stock moves yet. <c>400</c> <c>validation</c> for
    /// a bad body (AC-22.2); <c>400</c> <c>return-not-raw-material</c>; <c>409</c> <c>material-in-use</c> (AC-22.3);
    /// <c>409</c> <c>insufficient-free-leftover</c> (AC-22.2).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Instruct or cancel a leftover return": Manager only. A Warehouse-only or Production-only user gets
    /// <c>403</c> (AC-22.6) before the body is looked at (design 2.2).
    /// </remarks>
    [HttpPost]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`material-in-use`: the material is committed to the order in production (AC-22.3); `insufficient-free-leftover` (AC-22.2).")]
    public async Task<ActionResult<ReturnResponse>> Create(ReturnRequest request, CancellationToken cancellationToken)
    {
        // AC-22.1, AC-05.3: the return records who instructed it, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var created = await instructions.InstructAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { number = created.Number }, created);
    }

    /// <summary>
    /// <c>GET /api/v1/returns</c>: one page of returns, newest first, optionally filtered with <c>?status=Pending</c> (any
    /// status, any letter case) and <c>?itemId=3</c> (design 5.9). An unknown status, or an item id that is not a whole
    /// number, is <c>400</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="status">Optional status filter. Received as text so the error uses the design's wording.</param>
    /// <param name="itemId">Optional material filter; an id that matches nothing gives an empty page (design 2.5).</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    /// <remarks>PRD 4.2, "View ... returns": every role, so only the fallback "signed in" policy applies.</remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<ReturnResponse>> List(
        [FromQuery] PageQuery paging,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "itemId")] int? itemId,
        CancellationToken cancellationToken) =>
        returns.ListAsync(paging, status, itemId, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/returns/{number}</c>: one return (design 5.9), or <c>404</c> "Return #{number} not found.".
    /// </summary>
    /// <remarks>PRD 4.2: every role.</remarks>
    [HttpGet("{number:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ReturnResponse> Get(int number, CancellationToken cancellationToken) =>
        returns.GetAsync(number, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/returns/{number}/receive</c>: records that the warehouse received a Pending return (AC-22.4). No
    /// body. <c>200</c> with it, now Received; a ReturnedToWarehouse movement moved the quantity from production storage
    /// to the Warehouse. <c>404</c> for an unknown number; <c>409</c> <c>not-pending</c> (AC-22.5); <c>409</c>
    /// <c>insufficient-stock</c> when production storage holds less than the return (decision #18).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Record receiving a leftover return": Warehouse or Manager. A Production-only user gets <c>403</c>
    /// (AC-22.6).
    /// </remarks>
    [HttpPost("{number:int}/receive")]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`not-pending`: only a Pending return can be received (AC-22.5); `insufficient-stock`: Production storage holds less than the return (decision #18).")]
    public Task<ReturnResponse> Receive(int number, CancellationToken cancellationToken)
    {
        // AC-22.4, AC-05.3: the receipt records who received it, taken from the checked token.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return receipts.ReceiveAsync(number, userId, cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/returns/{number}/cancel</c>: a Manager cancels a Pending return (AC-22.5). No body. <c>200</c> with
    /// it, now Cancelled; its quantity is free leftover again. <c>404</c> for an unknown number; <c>409</c>
    /// <c>not-pending</c> (AC-22.5).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Instruct or cancel a leftover return": Manager only. A Warehouse-only or Production-only user gets
    /// <c>403</c> (AC-22.6).
    /// </remarks>
    [HttpPost("{number:int}/cancel")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`not-pending`: only a Pending return can be cancelled (AC-22.5).")]
    public Task<ReturnResponse> Cancel(int number, CancellationToken cancellationToken)
    {
        // AC-22.5, AC-05.3: the cancellation records who cancelled it, taken from the checked token.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return returns.CancelAsync(number, userId, cancellationToken);
    }
}
