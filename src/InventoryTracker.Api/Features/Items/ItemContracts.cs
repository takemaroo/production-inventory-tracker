using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Items;

/// <summary>
/// Body of <c>POST /api/v1/items</c> and <c>PUT /api/v1/items/{id}</c> (design 5.4). <c>PUT</c> is a full
/// replacement: all four fields are sent every time.
/// </summary>
/// <param name="Name">Required, trimmed, 1 to 100 characters, unique ignoring letter case (AC-07.1, design 2.6).</param>
/// <param name="Description">Optional, trimmed, up to 500 characters. Empty or blank is stored as no description.</param>
/// <param name="Type"><c>RawMaterial</c> or <c>FinishedProduct</c>, any letter case (design 2.3).</param>
/// <param name="UnitId">The id of an existing unit (AC-07.1).</param>
/// <remarks>
/// Every field is nullable, and <c>Type</c> is text rather than <see cref="ItemType"/>, so that a missing or unknown
/// value reaches <see cref="ItemService"/>, which answers with the design's own messages (architecture 4.1 item 3,
/// architecture 6, Validation).
/// </remarks>
public sealed record ItemRequest(string? Name, string? Description, string? Type, int? UnitId);

/// <summary>An item as the API returns it (design 5.4). AC-07.5: every item shows its type and unit.</summary>
/// <param name="Id">The item's id.</param>
/// <param name="Name">The name as stored: trimmed, in the letter case it was entered.</param>
/// <param name="Description">The description, or null when there is none.</param>
/// <param name="Type"><c>RawMaterial</c> or <c>FinishedProduct</c>.</param>
/// <param name="UnitId">The id of the item's unit.</param>
/// <param name="Unit">The unit's name, for example "kg" (design 2.4: the unit travels with the item).</param>
/// <param name="CreatedAt">When the item was created, UTC, whole seconds. Lists are ordered by it, newest first.</param>
public sealed record ItemResponse(
    int Id,
    string Name,
    string? Description,
    ItemType Type,
    int UnitId,
    string Unit,
    DateTime CreatedAt);
