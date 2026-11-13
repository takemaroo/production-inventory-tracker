namespace InventoryTracker.Api.Features.Recipes;

/// <summary>
/// Body of <c>PUT /api/v1/items/{id}/recipe</c> (design 5.4): the complete new list of lines. It replaces every line
/// the recipe had before (AC-08.1).
/// </summary>
/// <param name="Lines">
/// The raw materials, in the order the Manager wants them kept. At least one line (AC-08.2). The order is stored as
/// each line's <c>LineNo</c>, and later messages list materials in it (design 7.3).
/// </param>
/// <remarks>
/// The list and every field in it are nullable so that a missing value reaches <see cref="RecipeService"/>, which
/// answers with the design's own message naming the line (for example <c>lines[1].amountPerUnit</c>) rather than a
/// framework error (architecture 6, Validation). A line can even be the JSON literal <c>null</c>.
/// </remarks>
public sealed record RecipeRequest(IReadOnlyList<RecipeLineRequest?>? Lines);

/// <summary>One line of a <see cref="RecipeRequest"/>: which raw material, and how much one unit of the product needs.</summary>
/// <param name="ItemId">The id of an existing raw material (AC-08.2: not a finished product, not on another line).</param>
/// <param name="AmountPerUnit">
/// The amount of that raw material for 1 unit of the finished product, in the raw material's own unit (AC-08.1).
/// Greater than 0, at most 6 decimal places (AC-08.2, PRD assumption 12). Sent as a JSON number (design 2.4).
/// </param>
public sealed record RecipeLineRequest(int? ItemId, decimal? AmountPerUnit);

/// <summary>
/// A recipe as the API returns it, from both <c>GET</c> and <c>PUT /api/v1/items/{id}/recipe</c> (design 5.4).
/// </summary>
/// <param name="FinishedProductId">The finished product the recipe makes (the id in the URL).</param>
/// <param name="FinishedProductName">The finished product's name.</param>
/// <param name="Unit">The finished product's unit, for example "Bag": every amount below is per 1 of this unit.</param>
/// <param name="Lines">The raw materials in the order they were saved (their <c>LineNo</c>).</param>
/// <param name="UpdatedAt">When the recipe was last saved, UTC, whole seconds.</param>
public sealed record RecipeResponse(
    int FinishedProductId,
    string FinishedProductName,
    string Unit,
    IReadOnlyList<RecipeLineResponse> Lines,
    DateTime UpdatedAt);

/// <summary>
/// One line of a <see cref="RecipeResponse"/>. AC-08.1: "viewing the recipe returns each line with its unit", and
/// design 2.4: a quantity always travels with its unit.
/// </summary>
/// <param name="ItemId">The raw material's id.</param>
/// <param name="ItemName">The raw material's name.</param>
/// <param name="Unit">The raw material's unit, for example "kg": the unit of <paramref name="AmountPerUnit"/>.</param>
/// <param name="AmountPerUnit">How much of the raw material 1 unit of the finished product needs.</param>
public sealed record RecipeLineResponse(int ItemId, string ItemName, string Unit, decimal AmountPerUnit);
