using System.Linq.Expressions;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Recipes;

/// <summary>
/// Every rule for recipes (US-08): how much of each raw material one unit of a finished product needs. Reads one
/// recipe, and replaces a recipe's whole line list.
/// </summary>
/// <remarks>
/// <para>
/// Follows the same pattern as <c>ItemService</c>: the read is a plain query; the change runs inside
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/> with the checks in the design 2.2 order, and a broken rule
/// is thrown as an <see cref="ApiException"/>.
/// </para>
/// <para>
/// AC-08.4 (a recipe change affects only orders created later) needs nothing here: production orders copy each line's
/// amount per unit onto their own lines when they are created (T-009), and never read the recipe again. Replacing
/// the recipe's lines therefore cannot touch an existing order.
/// </para>
/// </remarks>
public sealed class RecipeService(AppDbContext db, Clock clock)
{
    /// <summary>Design 7.2: the code for a recipe sent for a raw material (AC-08.2).</summary>
    public const string RecipeNotFinishedProductCode = "recipe-not-finished-product";

    /// <summary>
    /// The one definition of how a stored recipe becomes its response (design 5.4). It is an
    /// <see cref="Expression{TDelegate}"/>, so EF Core translates it into SQL: it joins the product's unit, and loads
    /// the lines with each raw material's name and unit, in <c>LineNo</c> order, in the same query.
    /// </summary>
    private static readonly Expression<Func<Recipe, RecipeResponse>> AsResponse = recipe => new RecipeResponse(
        recipe.FinishedProductId,
        recipe.FinishedProduct.Name,
        recipe.FinishedProduct.Unit.Name,
        recipe.Lines
            .OrderBy(line => line.LineNo)
            .Select(line => new RecipeLineResponse(
                line.RawMaterialId, line.RawMaterial.Name, line.RawMaterial.Unit.Name, line.AmountPerUnit))
            .ToList(),
        recipe.UpdatedAt);

    /// <summary>
    /// AC-08.1: the recipe of item <paramref name="itemId"/>, each line with its unit. <c>404</c> "Item {id} not found."
    /// for an unknown item, and <c>404</c> "{item} has no recipe." for a finished product without one or for a raw
    /// material (design 5.4, 7.2).
    /// </summary>
    public async Task<RecipeResponse> GetAsync(int itemId, CancellationToken cancellationToken)
    {
        // Design 2.2 step 3: the item in the URL must exist. Only its name is needed, for the "no recipe" message.
        var itemName = await db.Items
            .Where(item => item.Id == itemId)
            .Select(item => item.Name)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw ItemNotFound(itemId);

        // A raw material never has a recipe (the PUT refuses one, and an item's type cannot change once it has a
        // recipe, AC-07.3), so "no recipe row" answers both cases in design 5.4 with the same message.
        return await ReadAsync(itemId, cancellationToken)
            ?? throw new NotFoundException($"{itemName} has no recipe.");
    }

    /// <summary>
    /// AC-08.1, AC-08.2: sets the recipe of finished product <paramref name="itemId"/> to exactly the submitted lines,
    /// replacing any previous list, and records who saved it and when. Returns the saved recipe (<c>200</c>).
    /// A body with more than <see cref="RequestLimits.MaxLines"/> lines is refused first, with <c>400</c>
    /// <c>too-many-lines</c> (SEC-01).
    /// </summary>
    public Task<RecipeResponse> ReplaceAsync(int itemId, RecipeRequest request, string userId, CancellationToken cancellationToken)
    {
        // SEC-01: refused before the write transaction; why, and what stays inside: RequestLimits.CheckLineCount.
        RequestLimits.CheckLineCount(request.Lines);

        return db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3: the item in the URL (404) is checked before the body (400).
            var product = await db.Items.FindAsync([itemId], cancellationToken) ?? throw ItemNotFound(itemId);

            // AC-08.2: "a recipe for an item of type RawMaterial". This is about the item in the URL, not about any one
            // field, so it is refused before the lines are looked at: no change to the lines could make it valid.
            if (product.Type != ItemType.FinishedProduct)
            {
                throw new BadRequestException(RecipeNotFinishedProductCode,
                    $"Recipes are for finished products only. {product.Name} is a raw material.");
            }

            var lines = await ValidateLinesAsync(request, cancellationToken);

            var recipe = await db.Recipes
                .Include(existing => existing.Lines)
                .SingleOrDefaultAsync(existing => existing.FinishedProductId == itemId, cancellationToken);

            if (recipe is null)
            {
                recipe = new Recipe { FinishedProductId = itemId };
                db.Recipes.Add(recipe);
            }
            else
            {
                // AC-08.1: saving replaces the whole previous list. Clearing the collection marks every old line as
                // Deleted, because a line cannot exist without its recipe. When the new list keeps a material from the
                // old one, EF Core sends the DELETE before the INSERT in the same SaveChanges, because it knows about
                // the unique index on (recipe, raw material). RecipesTests.Saving_again_replaces_all_previous_lines
                // fails if that ever stops being true.
                recipe.Lines.Clear();
            }

            // AC-05.3: who saved the recipe and when (design 5.4 updatedAt).
            recipe.UpdatedAt = clock.UtcNow;
            recipe.UpdatedById = userId;

            // LineNo keeps the submitted order, counted from 1 (architecture 3.2); messages list materials in it.
            for (var index = 0; index < lines.Count; index++)
            {
                recipe.Lines.Add(new RecipeLine
                {
                    LineNo = index + 1,
                    RawMaterialId = lines[index].RawMaterialId,
                    AmountPerUnit = lines[index].AmountPerUnit,
                });
            }

            // Save inside the transaction, then answer with the same query GET uses, so PUT and GET return the
            // saved recipe in exactly the same form. The query sees this transaction's own uncommitted rows.
            await db.SaveChangesAsync(cancellationToken);
            return (await ReadAsync(itemId, cancellationToken))!;
        }, cancellationToken);
    }

    /// <summary>The recipe of <paramref name="finishedProductId"/> as its response, or null when it has none.</summary>
    private Task<RecipeResponse?> ReadAsync(int finishedProductId, CancellationToken cancellationToken) =>
        db.Recipes
            .Where(recipe => recipe.FinishedProductId == finishedProductId)
            .Select(AsResponse)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Design 2.2 step 4 for a recipe body (AC-08.2): at least one line; on each line an existing raw material that is
    /// not on an earlier line, and an amount greater than 0 with at most 6 decimal places and no more than
    /// <see cref="Quantity.MaxAmountPerUnit"/> (decision #80). Every problem is reported
    /// in one <c>400</c>, keyed by the line's JSON path (design 6), for example <c>lines[2].itemId</c>.
    /// </summary>
    private async Task<IReadOnlyList<ValidLine>> ValidateLinesAsync(RecipeRequest request, CancellationToken cancellationToken)
    {
        // AC-08.2: "a recipe with no lines". A missing list counts as an empty one. There is nothing else to check
        // without lines, so this one message is thrown straight away.
        if (request.Lines is not { Count: > 0 } submitted)
        {
            throw new ValidationException("lines", "A recipe needs at least one line.");
        }

        var errors = new ValidationErrors();

        // Load every named item in one query instead of one query per line.
        var itemIds = submitted
            .Select(line => line?.ItemId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var items = await db.Items
            .Where(item => itemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name, item.Type })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var valid = new List<ValidLine>(submitted.Count);
        var materialsSeen = new HashSet<int>();
        for (var index = 0; index < submitted.Count; index++)
        {
            var line = submitted[index];
            var itemPath = $"lines[{index}].itemId";
            var amountPath = $"lines[{index}].amountPerUnit";

            // One message per field: the first rule that fails is the one the Manager needs to fix.
            if (line?.ItemId is not { } itemId)
            {
                errors.Add(itemPath, "Item is required.");
            }
            else if (!items.TryGetValue(itemId, out var item))
            {
                // Design 2.2 step 4: an unknown id in the body is 400, not 404.
                errors.Add(itemPath, $"Item {itemId} does not exist.");
            }
            else if (item.Type != ItemType.RawMaterial)
            {
                // AC-08.2: "a line whose item is a FinishedProduct".
                errors.Add(itemPath, $"Recipe lines must be raw materials. {item.Name} is a finished product.");
            }
            else if (!materialsSeen.Add(itemId))
            {
                // AC-08.2: "the same raw item on two lines". The first line naming it is kept; each later one is
                // reported, so the message points at the line to remove.
                errors.Add(itemPath, $"{item.Name} appears on more than one line.");
            }

            if (line?.AmountPerUnit is not { } amount)
            {
                errors.Add(amountPath, "Amount per unit is required.");
            }
            else if (amount <= 0)
            {
                // AC-08.2: "an amount of zero or less".
                errors.Add(amountPath, "Amount per unit must be greater than 0.");
            }
            else if (!Quantity.HasAtMostDecimals(amount, Quantity.MaxAmountPerUnitDecimals))
            {
                // AC-08.2, PRD assumption 12: recipe amounts have at most 6 decimal places (trailing zeros do not count).
                errors.Add(amountPath,
                    $"Amount per unit can have at most {Quantity.MaxAmountPerUnitDecimals} decimal places.");
            }
            else if (!Quantity.IsWithinMaxAmountPerUnit(amount))
            {
                // Decision #80: at most 999,999.999999 per unit, so amount × the largest ordered or produced quantity
                // still fits decimal(18,3), both as a planned amount (AC-13.1) and as the required amount for actual
                // output (D-9). It is the last rule on this field (decision #75).
                errors.Add(amountPath, Quantity.AmountPerUnitAboveMaxMessage);
            }

            if (line is { ItemId: { } validId, AmountPerUnit: { } validAmount })
            {
                valid.Add(new ValidLine(validId, validAmount));
            }
        }

        errors.ThrowIfAny();
        return valid;
    }

    private static NotFoundException ItemNotFound(int id) => new($"Item {id} not found.");

    /// <summary>A recipe line that passed every check: an existing raw material and a valid amount.</summary>
    private sealed record ValidLine(int RawMaterialId, decimal AmountPerUnit);
}
