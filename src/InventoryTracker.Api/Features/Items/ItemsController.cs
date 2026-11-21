using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Items;

/// <summary>
/// The <c>/api/v1/items</c> endpoints (US-07, design 4 and 5.4). Any signed-in user may read (AC-07.5); only a
/// Manager may create, change or delete (PRD 4.2). The recipe sub-resource <c>/items/{id}/recipe</c> is a separate
/// controller (T-007).
/// </summary>
/// <remarks>
/// Built the same way as <c>UnitsController</c>, which explains the request flow, model binding, <c>[Authorize]</c>
/// and <c>CreatedAtAction</c>. All rules are in <see cref="ItemService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/items")]
public sealed class ItemsController(ItemService items) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/items</c>: one page of items, newest first, optionally filtered with
    /// <c>?type=RawMaterial</c> or <c>?type=FinishedProduct</c> (AC-07.5, design 5.4). An unknown type is <c>400</c>.
    /// </summary>
    /// <param name="paging">
    /// The <c>page</c> and <c>pageSize</c> query parameters. (Not named <c>page</c>: see <c>UnitsController.List</c>.)
    /// </param>
    /// <param name="type">Optional type filter, any letter case. Received as text so the error uses the design's wording.</param>
    /// <param name="cancellationToken">Fires when the client disconnects.</param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<PagedResult<ItemResponse>> List(
        [FromQuery] PageQuery paging, [FromQuery(Name = "type")] string? type, CancellationToken cancellationToken) =>
        items.ListAsync(paging, type, cancellationToken);

    /// <summary><c>GET /api/v1/items/{id}</c>: one item with its type and unit (AC-07.5), or <c>404</c> "Item {id} not found.".</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ItemResponse> Get(int id, CancellationToken cancellationToken) =>
        items.GetAsync(id, cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/items</c>: creates an item (AC-07.1). <c>201</c> with the item and a <c>Location</c> header;
    /// <c>400</c> for a missing name, unknown type or unknown unit; <c>409</c> <c>item-name-taken</c> for a duplicate
    /// name (AC-07.2).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`item-name-taken`: an item with this name already exists (AC-07.2).")]
    public async Task<ActionResult<ItemResponse>> Create(ItemRequest request, CancellationToken cancellationToken)
    {
        var item = await items.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    /// <summary>
    /// <c>PUT /api/v1/items/{id}</c>: replaces name, description, type and unit (AC-07.3). <c>200</c> with the item;
    /// <c>409</c> <c>item-in-use</c> when type or unit would change on an item in use.
    /// </summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`item-name-taken`, or `item-in-use`: the type or unit cannot change once the item is in use (AC-07.3).")]
    public Task<ItemResponse> Update(int id, ItemRequest request, CancellationToken cancellationToken) =>
        items.UpdateAsync(id, request, cancellationToken);

    /// <summary>
    /// <c>DELETE /api/v1/items/{id}</c>: deletes an item that is not in use (AC-07.4). <c>204</c>; <c>409</c>
    /// <c>item-in-use</c> otherwise.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Manager)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Description = "`item-in-use`: the item has movements, a recipe or orders (AC-07.4).")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await items.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
