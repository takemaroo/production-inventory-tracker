using System.Linq.Expressions;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Items;

/// <summary>
/// Every rule for items, the raw materials and finished products (US-07): list, read, create, change and delete.
/// </summary>
/// <remarks>
/// Follows the same pattern as <c>UnitService</c>: reads are plain queries; every change runs inside
/// <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>, with the checks in the design 2.2 order (<c>404</c>,
/// then <c>400</c> input, then <c>409</c> state), and a broken rule is thrown as an <see cref="ApiException"/>.
/// </remarks>
public sealed class ItemService(AppDbContext db, Clock clock)
{
    /// <summary>AC-07.3: the exact PRD message for changing type or unit of an item in use.</summary>
    public const string TypeAndUnitLockedMessage = "Type and unit cannot change once the item is in use.";

    /// <summary>Design 7.2: the code for a duplicate item name (AC-07.2).</summary>
    public const string ItemNameTakenCode = "item-name-taken";

    /// <summary>Design 7.2: the code for changing type or unit of, or deleting, an item in use (AC-07.3, AC-07.4).</summary>
    public const string ItemInUseCode = "item-in-use";

    /// <summary>
    /// The one definition of how an item becomes its response (design 5.4), used in two ways. Reads pass it to
    /// <c>Select</c>: it is an <see cref="Expression{TDelegate}"/>, a description of the code rather than compiled code,
    /// so EF Core can translate it into SQL that selects only these columns, joining <c>Units</c> for the unit's name.
    /// </summary>
    private static readonly Expression<Func<Item, ItemResponse>> AsResponse = item => new ItemResponse(
        item.Id, item.Name, item.Description, item.Type, item.UnitId, item.Unit.Name, item.CreatedAt);

    /// <summary>
    /// The same mapping compiled once into a normal method, for an item already in memory (after a create or change).
    /// Built from <see cref="AsResponse"/>, so the two can never differ. The item's <c>Unit</c> must be loaded.
    /// </summary>
    private static readonly Func<Item, ItemResponse> ToResponse = AsResponse.Compile();

    /// <summary>
    /// AC-07.5: one page of items, newest first (PRD 4.1, design 2.5), optionally only one type
    /// (<c>?type=RawMaterial</c>, design 5.4).
    /// </summary>
    public async Task<PagedResult<ItemResponse>> ListAsync(PageQuery page, string? type, CancellationToken cancellationToken)
    {
        // One collector for paging and the filter, so every bad query value is reported in one response.
        var errors = new ValidationErrors();
        page.Validate(errors);
        var typeFilter = EnumParser.ParseOptional<ItemType>(type, "type", "type", errors);
        errors.ThrowIfAny();

        // No AsNoTracking needed: the query ends in Select(AsResponse), which returns response records, not entities.
        var query = db.Items.AsQueryable();
        if (typeFilter is { } wanted)
        {
            query = query.Where(item => item.Type == wanted);
        }

        // Design 2.5: newest first by creation time, then by id for items created in the same second.
        return await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(AsResponse)
            .ToPagedResultAsync(page, cancellationToken);
    }

    /// <summary>AC-07.5: one item with its type and unit, or <c>404</c> "Item {id} not found.".</summary>
    public async Task<ItemResponse> GetAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.Items
            .Where(candidate => candidate.Id == id)
            .Select(AsResponse)
            .SingleOrDefaultAsync(cancellationToken);

        return item ?? throw NotFound(id);
    }

    /// <summary>
    /// AC-07.1, AC-07.2: creates an item with a unique name, an optional description, a type and an existing unit.
    /// </summary>
    public Task<ItemResponse> CreateAsync(ItemRequest request, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var input = await ValidateAsync(request, cancellationToken);
            await ThrowIfNameTakenAsync(input.Name, exceptId: null, cancellationToken);

            var item = new Item
            {
                Name = input.Name,
                NameNormalized = NameNormalizer.Normalize(input.Name),
                Description = input.Description,
                Type = input.Type,
                UnitId = input.Unit.Id,
                Unit = input.Unit,
                CreatedAt = clock.UtcNow,
            };
            db.Items.Add(item);

            // The id is generated by the database; save now (still inside the transaction) so the 201 can show it.
            await db.SaveChangesAsync(cancellationToken);
            return ToResponse(item);
        }, cancellationToken);

    /// <summary>
    /// AC-07.3: replaces all four fields of an item. Name and description may change at any time; type and unit only
    /// while the item is not in use.
    /// </summary>
    public Task<ItemResponse> UpdateAsync(int id, ItemRequest request, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2: the id in the URL (404) is checked before the body (400).
            var item = await db.Items.FindAsync([id], cancellationToken) ?? throw NotFound(id);

            var input = await ValidateAsync(request, cancellationToken);

            // The item's own current name does not count as taken, so its letter case can be changed.
            await ThrowIfNameTakenAsync(input.Name, exceptId: id, cancellationToken);

            // AC-07.3: sending the stored type and unit again is not a change, so a full-replacement PUT that only
            // edits the name or description is always allowed, even for an item in use.
            var changesTypeOrUnit = input.Type != item.Type || input.Unit.Id != item.UnitId;
            if (changesTypeOrUnit && await IsInUseAsync(id, cancellationToken))
            {
                throw new ConflictException(ItemInUseCode, TypeAndUnitLockedMessage);
            }

            item.Name = input.Name;
            item.NameNormalized = NameNormalizer.Normalize(input.Name);
            item.Description = input.Description;
            item.Type = input.Type;
            item.UnitId = input.Unit.Id;
            item.Unit = input.Unit;
            return ToResponse(item);
        }, cancellationToken);

    /// <summary>
    /// AC-07.4: deletes an item that is not in use; otherwise <c>409</c> <c>item-in-use</c>.
    /// </summary>
    public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var item = await db.Items.FindAsync([id], cancellationToken) ?? throw NotFound(id);

            // AC-07.4. Checked and deleted in one write transaction, so nothing can start using the item in between.
            // (v1.1) AC-28.5, design 7.2: the message names sales order lines too, the fourth kind of use a user can see.
            if (await IsInUseAsync(id, cancellationToken))
            {
                throw new ConflictException(ItemInUseCode,
                    $"{item.Name} has movements, recipe lines, order lines or sales order lines and cannot be deleted.");
            }

            db.Items.Remove(item);
        }, cancellationToken);

    /// <summary>
    /// AC-07.3, AC-07.4: true when anything refers to the item. Architecture 3.2 (Item) lists every reference: a
    /// movement, a recipe line naming it as a raw material, its own recipe as a finished product, a production order
    /// line, a production order for it as the finished product, a complementary-order line, a return, or (v1.1) a sales
    /// order line in any status (AC-28.5).
    /// </summary>
    /// <remarks>
    /// The PRD's short form (AC-07.4) is "movements, recipe lines, order lines or sales order lines"; the other references
    /// count too, because each one would be left pointing at a different kind of item (or, for a delete, at nothing).
    /// Each check is a quick <c>EXISTS</c> query, and the first one that finds a row ends the search. A new foreign key
    /// to <c>Items</c> must be added here too; <c>ItemsTests.Item_in_use_check_covers_every_foreign_key_to_items</c>
    /// fails until it is.
    /// </remarks>
    private async Task<bool> IsInUseAsync(int itemId, CancellationToken cancellationToken) =>
        await db.Movements.AnyAsync(movement => movement.ItemId == itemId, cancellationToken)
        || await db.RecipeLines.AnyAsync(line => line.RawMaterialId == itemId, cancellationToken)
        || await db.Recipes.AnyAsync(recipe => recipe.FinishedProductId == itemId, cancellationToken)
        || await db.ProductionOrderLines.AnyAsync(line => line.RawMaterialId == itemId, cancellationToken)
        || await db.ProductionOrders.AnyAsync(order => order.FinishedProductId == itemId, cancellationToken)
        || await db.ComplementaryOrderLines.AnyAsync(line => line.RawMaterialId == itemId, cancellationToken)
        || await db.Returns.AnyAsync(leftoverReturn => leftoverReturn.ItemId == itemId, cancellationToken)
        // AC-28.5: whatever the sales order's status, so no status filter. A Cancelled sales order still shows the item
        // with its unit, so the unit must not change under it. Uses the SalesOrderLines.ItemId index.
        || await db.SalesOrderLines.AnyAsync(line => line.ItemId == itemId, cancellationToken);

    /// <summary>
    /// Design 2.2 step 4 for an item body: checks every field and reports all problems in one <c>400</c>
    /// (AC-07.2: missing name, unknown type, unknown unit; design 2.6: lengths).
    /// </summary>
    private async Task<ValidItem> ValidateAsync(ItemRequest request, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        // Design 2.6: trimmed first; a name of only spaces counts as missing.
        var name = Text.Clean(request.Name);
        if (name is null)
        {
            errors.Add("name", "Name is required.");
        }
        // Decision #116: both limits are counted in Unicode code points, so an emoji is one character (Text.Length).
        else if (Text.Length(name) > ColumnRules.ItemNameLength)
        {
            errors.Add("name", $"Name must be {ColumnRules.ItemNameLength} characters or fewer.");
        }

        var description = Text.Clean(request.Description);
        if (Text.Length(description) > ColumnRules.TextLength)
        {
            errors.Add("description", $"Description must be {ColumnRules.TextLength} characters or fewer.");
        }

        // Missing and unknown get the same design 7.2 message: "Type must be RawMaterial or FinishedProduct."
        var type = EnumParser.ParseRequired<ItemType>(request.Type, "type", "Type", errors);

        // Design 2.2 step 4 includes "unknown ids in the body": a unit that does not exist is 400, not 404.
        UnitOfMeasure? unit = null;
        if (request.UnitId is not { } unitId)
        {
            errors.Add("unitId", "Unit is required.");
        }
        else
        {
            unit = await db.Units.FindAsync([unitId], cancellationToken);
            if (unit is null)
            {
                errors.Add("unitId", $"Unit {unitId} does not exist.");
            }
        }

        errors.ThrowIfAny();
        return new ValidItem(name!, description, type!.Value, unit!);
    }

    /// <summary>
    /// AC-07.1, AC-07.2: <c>409</c> when another item already has this name, ignoring letter case. The unique index on
    /// <c>NameNormalized</c> is the database's backstop (decision #30).
    /// </summary>
    private async Task ThrowIfNameTakenAsync(string name, int? exceptId, CancellationToken cancellationToken)
    {
        var normalized = NameNormalizer.Normalize(name);
        var taken = await db.Items.AnyAsync(
            item => item.NameNormalized == normalized && item.Id != exceptId, cancellationToken);

        if (taken)
        {
            throw new ConflictException(ItemNameTakenCode, $"An item named '{name}' already exists.");
        }
    }

    private static NotFoundException NotFound(int id) => new($"Item {id} not found.");

    /// <summary>An item body that passed every field check: trimmed text, a parsed type and the loaded unit.</summary>
    private sealed record ValidItem(string Name, string? Description, ItemType Type, UnitOfMeasure Unit);
}
