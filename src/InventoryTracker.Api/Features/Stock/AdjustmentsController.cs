using System.Security.Claims;
using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// <c>POST /api/v1/adjustments</c> (US-12, design 4 and 5.6): a Manager corrects stock, or writes off spoiled material,
/// with a reason.
/// </summary>
/// <remarks>
/// An adjustment is a command that creates a movement; it has no list of its own and appears in <c>/movements</c> as
/// type <c>Adjustment</c> (design 4). All rules are in <see cref="AdjustmentService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/adjustments")]
public sealed class AdjustmentsController(AdjustmentService adjustments) : ControllerBase
{
    /// <summary>
    /// Records an adjustment (AC-12.1): <c>201</c> with the Adjustment movement and
    /// <c>Location: /api/v1/movements/{id}</c>. <c>400</c> <c>validation</c> for a bad item, location, quantity or reason
    /// (AC-12.2); <c>400</c> <c>finished-product-in-production-storage</c> (AC-12.6); <c>409</c> <c>material-in-use</c>
    /// (AC-12.5); <c>409</c> <c>insufficient-stock</c> (AC-12.3).
    /// </summary>
    /// <remarks>
    /// PRD 4.2, "Make an adjustment": Manager only, so any other role gets <c>403</c> (AC-12.7).
    /// </remarks>
    [HttpPost]
    [Authorize(Roles = Roles.Manager)]
    public async Task<ActionResult<MovementResponse>> Create(AdjustmentRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3: the movement records the signed-in user, taken from the checked token, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var movement = await adjustments.CreateAsync(userId, request, cancellationToken);

        // Design 4: the Location is the movement's URL, served by GET /api/v1/movements/{id} (T-013). Written as a
        // relative path, exactly as the receipts endpoint does, so both commands give the same form of header.
        return Created($"/api/v1/movements/{movement.Id}", movement);
    }
}
