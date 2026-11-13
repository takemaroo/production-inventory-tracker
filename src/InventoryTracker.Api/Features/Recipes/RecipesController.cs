using System.Security.Claims;
using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Recipes;

/// <summary>
/// The recipe of a finished product, <c>/api/v1/items/{id}/recipe</c> (US-08, design 4 and 5.4). Any signed-in user
/// may read it; only a Manager may set it (PRD 4.2, "Manage units, items, recipes").
/// </summary>
/// <remarks>
/// The recipe is a single sub-resource of its item (design 2.1): there is one per finished product, so it has no id
/// of its own and no <c>POST</c> or <c>DELETE</c>. <c>PUT</c> both creates and replaces it. Built the same way as
/// <c>UnitsController</c>, which explains the request flow; all rules are in <see cref="RecipeService"/>.
/// </remarks>
[ApiController]
[Route("api/v1/items/{id:int}/recipe")]
public sealed class RecipesController(RecipeService recipes) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/items/{id}/recipe</c>: the recipe with each line's unit (AC-08.1). <c>404</c> "Item {id} not
    /// found." for an unknown item; <c>404</c> "{item} has no recipe." for a finished product without one or for a raw
    /// material (design 5.4).
    /// </summary>
    /// <remarks>No <c>[Authorize]</c>: the fallback "signed in" policy is exactly "All" in design 4.</remarks>
    [HttpGet]
    public Task<RecipeResponse> Get(int id, CancellationToken cancellationToken) =>
        recipes.GetAsync(id, cancellationToken);

    /// <summary>
    /// <c>PUT /api/v1/items/{id}/recipe</c>: replaces the whole line list of a finished product's recipe (AC-08.1).
    /// <c>200</c> with the saved recipe; <c>400</c> <c>recipe-not-finished-product</c> for a raw material, and
    /// <c>400</c> <c>validation</c> naming each bad line (AC-08.2).
    /// </summary>
    [HttpPut]
    [Authorize(Roles = Roles.Manager)]
    public Task<RecipeResponse> Replace(int id, RecipeRequest request, CancellationToken cancellationToken)
    {
        // AC-05.3: the recipe records who saved it. The id comes from the token's "sub" claim, never from the body.
        var userId = User.FindFirstValue(JwtTokenService.UserIdClaim)!;
        return recipes.ReplaceAsync(id, request, userId, cancellationToken);
    }
}
