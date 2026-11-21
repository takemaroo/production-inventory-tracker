using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Units;

/// <summary>
/// The <c>/api/v1/units</c> endpoints (US-06, design 4 and 5.3). Any signed-in user may read; only a Manager may
/// create, rename or delete (PRD 4.2, "Manage units, items, recipes").
/// </summary>
/// <remarks>
/// <para>
/// <b>How a request flows.</b> This is the first feature controller, so the whole path is described once here.
/// (1) Routing matches the method and URL to one action below, using the <c>[Route]</c> prefix plus the action's
/// <c>[HttpGet]</c>/<c>[HttpPost]</c>/... template. (2) Before the action runs, the authentication middleware reads and
/// validates the bearer token, and the authorization middleware applies the action's rule: the fallback "signed in"
/// policy, or the action's own <c>[Authorize(Roles = ...)]</c>. A request without a valid token gets <c>401</c>, and a
/// signed-in user without a required role gets <c>403</c>; neither reaches the action. (3) <em>Model binding</em>
/// builds the action's parameters from the
/// request: <c>id</c> from the URL segment <c>{id:int}</c>, the request record from the JSON body (with
/// <c>[ApiController]</c>, a record parameter is read from the body without needing <c>[FromBody]</c>), the page from
/// the query string because of <c>[FromQuery]</c>, and a <see cref="CancellationToken"/> that fires if the client
/// disconnects. A body that cannot be read is refused with <c>400</c> at this step (architecture 4.1 item 2).
/// (4) The action calls <see cref="UnitService"/>, which applies every rule and uses the <c>AppDbContext</c> to read
/// and write the database. (5) What the action returns is written as JSON with status <c>200</c>, unless it asks
/// for another status (<c>201</c>, <c>204</c>). If the service throws, the central exception handler writes the
/// error instead.
/// </para>
/// <para>
/// The controller is deliberately thin (architecture 2): no rules here, only HTTP. ASP.NET Core creates it per
/// request and passes the service into the constructor through dependency injection. The parameter list after the
/// class name, <c>UnitsController(UnitService units)</c>, is a C# <em>primary constructor</em>: a short way of writing a
/// constructor whose parameter (<c>units</c>) can be used anywhere in the class.
/// </para>
/// <para>
/// <b>Response attributes.</b> <c>[ProducesResponseType]</c> on an action changes nothing at run time: it tells the
/// OpenAPI description (PRD section 5, <c>/openapi/v1.json</c>) which statuses the action answers with. Each action
/// declares its success status and, when a business rule can refuse it, its <c>409</c> with the codes. The body of a
/// <c>200</c> or <c>201</c> is taken from the action's return type. The <c>400</c>, <c>401</c>, <c>403</c> and
/// <c>404</c> responses follow from the action's parameters and <c>[Authorize]</c>, so
/// <c>Common/OpenApi/ApiResponsesTransformer</c> adds them for every action. Once an action has one of these
/// attributes, the generator no longer assumes <c>200</c> by itself, which is why the success status is always listed.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/units")]
public sealed class UnitsController(UnitService units) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/units</c>: one page of units, newest first (AC-06.1: all signed-in users can list units).
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>[Authorize]</c> attribute: the fallback policy (see <c>AuthSetup</c>) still requires a signed-in user,
    /// which is exactly "All" in the design's endpoint table.
    /// </para>
    /// <para>
    /// The parameter must not be called <c>page</c> or <c>pageSize</c>. When a query key equals a complex parameter's
    /// name, model binding uses that name as a prefix and looks for <c>?page.page=</c>, so <c>?page=2</c> would be
    /// silently ignored.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<UnitResponse>> List([FromQuery] PageQuery paging, CancellationToken cancellationToken) =>
        units.ListAsync(paging, cancellationToken);

    /// <summary><c>GET /api/v1/units/{id}</c>: one unit, or <c>404</c> "Unit {id} not found.".</summary>
    /// <remarks>
    /// <c>{id:int}</c> is a route constraint: the segment must be a whole number, otherwise this action does not match
    /// and the response is the general <c>404</c> "No endpoint matches this path.".
    /// </remarks>
    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<UnitResponse> Get(int id, CancellationToken cancellationToken) =>
        units.GetAsync(id, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/units</c>: creates a unit (AC-06.1). <c>201</c> with the unit and a <c>Location</c> header;
    /// <c>400</c> for a missing or too long name; <c>409</c> <c>unit-name-taken</c> for a duplicate (AC-06.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[Authorize(Roles = Roles.Manager)]</c> replaces the fallback "any signed-in user" rule with "must hold the
    /// Manager role" for this action (PRD 4.2, AC-05.1). A caller without a token gets <c>401</c>; a signed-in user
    /// without the role gets <c>403</c>, written by <c>ForbiddenResponseHandler</c>. Both happen before the action
    /// runs, so a Warehouse user cannot even learn whether a unit exists (design 2.2).
    /// </para>
    /// <para>
    /// <c>CreatedAtAction</c> answers <c>201 Created</c> (design 2.2) with the new unit as the body and a
    /// <c>Location</c> header holding the URL of <see cref="Get"/> for the new id (<c>.../api/v1/units/5</c>), so the
    /// client knows where the new resource lives. The action is named <c>Get</c>, not <c>GetAsync</c>, because MVC
    /// drops an "Async" suffix from action names, and the link would then not be found.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`unit-name-taken`: a unit with this name already exists (AC-06.2).")]
    public async Task<ActionResult<UnitResponse>> Create(UnitRequest request, CancellationToken cancellationToken)
    {
        var unit = await units.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = unit.Id }, unit);
    }

    /// <summary>
    /// <c>PUT /api/v1/units/{id}</c>: renames a unit under the same rules as creating one (AC-06.3). <c>200</c> with
    /// the unit.
    /// </summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`unit-name-taken`: a unit with this name already exists (AC-06.2).")]
    public Task<UnitResponse> Update(int id, UnitRequest request, CancellationToken cancellationToken) =>
        units.UpdateAsync(id, request, cancellationToken);

    /// <summary>
    /// <c>DELETE /api/v1/units/{id}</c>: deletes a unit no item uses (AC-06.4). <c>204</c> with no body; <c>409</c>
    /// <c>unit-in-use</c> "Unit is used by one or more items." otherwise.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`unit-in-use`: one or more items use the unit (AC-06.4).")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await units.DeleteAsync(id, cancellationToken);

        // Design 2.2: a delete succeeds with 204 No Content, because there is nothing left to return.
        return NoContent();
    }
}
