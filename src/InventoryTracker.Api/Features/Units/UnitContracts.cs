namespace InventoryTracker.Api.Features.Units;

/// <summary>Body of <c>POST /api/v1/units</c> and <c>PUT /api/v1/units/{id}</c> (design 5.3).</summary>
/// <param name="Name">The unit's name, for example "kg". Trimmed; 1 to 50 characters (AC-06.1).</param>
/// <remarks>
/// <c>Name</c> is nullable so that a missing name reaches <see cref="UnitService"/>, which answers with the design's
/// own message "Name is required." instead of being refused by MVC first (architecture 6, Validation).
/// </remarks>
public sealed record UnitRequest(string? Name);

/// <summary>A unit of measure as the API returns it (design 5.3): <c>{ "id": 1, "name": "kg" }</c>.</summary>
/// <param name="Id">The unit's id, used in URLs and as <c>unitId</c> on items.</param>
/// <param name="Name">The name as stored: trimmed, in the letter case it was entered.</param>
public sealed record UnitResponse(int Id, string Name);
