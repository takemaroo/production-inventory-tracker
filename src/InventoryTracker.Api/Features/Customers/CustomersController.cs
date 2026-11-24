using System.Security.Claims;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Customers;

/// <summary>
/// (v1.1) The seven <c>/api/v1/customers</c> endpoints (US-29, design 3.12, 4 and 5.11): list, read one, create, edit,
/// deactivate, reactivate and delete. Warehouse, Manager and Seller users may read; Manager and Seller users may change.
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c> and
/// <c>[ProducesResponseType]</c>. The controller is thin: every rule is in <see cref="CustomerService"/>.
/// </para>
/// <para>
/// <b>Why each action carries its own <c>[Authorize]</c></b> (architecture 4.1 item 13, decision #156). The reads and the
/// changes need different roles. ASP.NET Core <em>combines</em> a class-level and an action-level attribute with AND, so
/// a class attribute of "Warehouse, Manager or Seller" plus an action attribute of "Manager or Seller" would still let
/// only Managers and Sellers change customers. The trouble is the <c>403</c> sentence: <c>ForbiddenResponseHandler</c>
/// names the roles of every requirement the user failed. A Warehouse-only user passes the class attribute and fails only
/// the action's, so their sentence would still be right. But a Production-only user changing a customer fails both, and
/// would be told "It needs the Warehouse, Manager or Seller role." instead of "It needs the Manager or Seller role.",
/// which is wrong: Warehouse would not let them change a customer. One attribute per action keeps the <c>403</c> message
/// exact (design 7.1):
/// a Production-only user reading gets "It needs the Warehouse, Manager or Seller role.", a Warehouse-only user changing
/// gets "It needs the Manager or Seller role." (AC-29.7). The roles are checked before the action runs, so a refused user
/// cannot learn which ids exist (design 2.2).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/customers")]
public sealed class CustomersController(CustomerService customers) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/customers</c>: one page of customers, newest first, optionally filtered by
    /// <c>?isActive=true</c> or <c>false</c> (AC-29.6, design 5.11). <c>400</c> for bad paging or an <c>isActive</c>
    /// other than <c>true</c> or <c>false</c>.
    /// </summary>
    /// <param name="paging">The <c>page</c> and <c>pageSize</c> query parameters (see <c>UnitsController.List</c> for the name).</param>
    /// <param name="isActive">Optional, <c>true</c> or <c>false</c> in any letter case. Received as text so the error uses the API's own wording.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<CustomerResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery(Name = "isActive")] string? isActive, CancellationToken cancellationToken) =>
        customers.ListAsync(paging, isActive, cancellationToken);

    /// <summary>
    /// <c>GET /api/v1/customers/{id}</c>: one customer, active or deactivated (AC-29.4, AC-29.6), or <c>404</c>
    /// "Customer {id} not found.".
    /// </summary>
    [HttpGet("{id:int}")]
    [Authorize(Roles = Roles.WarehouseManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<CustomerResponse> Get(int id, CancellationToken cancellationToken) =>
        customers.GetAsync(id, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/customers</c>: creates an active customer (AC-29.1). <c>201</c> with the customer and a
    /// <c>Location</c> header; <c>400</c> for a missing or too long name or too long notes; <c>409</c>
    /// <c>customer-name-taken</c> for a name another customer has, ignoring case (AC-29.2).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`customer-name-taken`: another customer has this name, ignoring case (AC-29.2).")]
    public async Task<ActionResult<CustomerResponse>> Create(CustomerRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3: "created by" is the caller, taken from the checked token (its sub claim), never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        var customer = await customers.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = customer.Id }, customer);
    }

    /// <summary>
    /// <c>PUT /api/v1/customers/{id}</c>: replaces the customer's name and notes under the same rules as creating one
    /// (AC-29.3). <c>200</c> with the customer; <c>404</c> for an unknown id, before any body check.
    /// </summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`customer-name-taken`: another customer has this name, ignoring case (AC-29.2).")]
    public Task<CustomerResponse> Update(int id, CustomerRequest request, CancellationToken cancellationToken) =>
        customers.UpdateAsync(id, request, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/customers/{id}/deactivate</c>: the customer can no longer be chosen for a new sales order, but stays
    /// listed and readable (AC-29.4). <c>200</c> with the customer, also when it was already deactivated.
    /// </summary>
    /// <remarks>
    /// No body parameter: the action takes none, and one that is sent is ignored (architecture 4.1 item 15).
    /// </remarks>
    [HttpPost("{id:int}/deactivate")]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<CustomerResponse> Deactivate(int id, CancellationToken cancellationToken) =>
        customers.DeactivateAsync(id, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/customers/{id}/reactivate</c>: the customer can be chosen for new sales orders again (AC-29.4).
    /// <c>200</c> with the customer, also when it was already active.
    /// </summary>
    [HttpPost("{id:int}/reactivate")]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<CustomerResponse> Reactivate(int id, CancellationToken cancellationToken) =>
        customers.ReactivateAsync(id, cancellationToken);

    /// <summary>
    /// <c>DELETE /api/v1/customers/{id}</c>: deletes a customer that has never had a sales order (AC-29.5). <c>204</c>
    /// with no body; <c>409</c> <c>customer-has-sales-orders</c> otherwise, even when every sales order is Cancelled.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.ManagerOrSeller)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`customer-has-sales-orders`: a sales order, in any status, names the customer (AC-29.5).")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await customers.DeleteAsync(id, cancellationToken);

        // Design 2.2: a delete succeeds with 204 No Content, because there is nothing left to return.
        return NoContent();
    }
}
