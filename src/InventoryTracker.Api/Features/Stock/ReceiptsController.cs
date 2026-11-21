using System.Security.Claims;
using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// <c>POST /api/v1/receipts</c> (US-09, design 4 and 5.6): records raw material arriving in the Warehouse.
/// </summary>
/// <remarks>
/// A receipt is a command that creates a movement; it has no list of its own and appears in <c>/movements</c> as type
/// <c>Received</c> (design 4). All rules are in <see cref="ReceiptService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/receipts")]
public sealed class ReceiptsController(ReceiptService receipts) : ControllerBase
{
    /// <summary>
    /// Records a receipt (AC-09.1): <c>201</c> with the Received movement and <c>Location: /api/v1/movements/{id}</c>.
    /// <c>400</c> <c>validation</c> for a bad quantity, item, date or note (AC-09.2); <c>400</c>
    /// <c>receipt-not-raw-material</c> for a finished product (AC-09.3).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Record a stock receipt": Warehouse or Manager. A user holding either role is let in (AC-05.2).
    /// </remarks>
    [HttpPost]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<MovementResponse>> Create(ReceiptRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3: the movement records the signed-in user, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var movement = await receipts.CreateAsync(userId, request, cancellationToken);

        // Design 4: the Location is the movement's URL, served by GET /api/v1/movements/{id} (MovementsController.Get).
        // It is written out as a relative path rather than generated with CreatedAtAction, because the movement
        // belongs to another controller; the adjustments endpoint writes it the same way, so both commands give the
        // same form of header.
        return Created($"/api/v1/movements/{movement.Id}", movement);
    }
}
