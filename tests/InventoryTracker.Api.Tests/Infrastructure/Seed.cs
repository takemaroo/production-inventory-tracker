using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Creates the master data a test needs before it can test something else (units and items), through the real API,
/// so the data passes the same checks as a client's would. Also makes the unique names and emails that let tests share
/// one database without getting in each other's way.
/// </summary>
/// <remarks>
/// Users are not created here: use <see cref="TestApiFactory.CreateUserAsync"/>, or
/// <see cref="TestApiFactory.CreateClientAsAsync"/>, which also signs the new user in. Each method here asserts that the
/// API accepted the data, so a broken set-up fails at the line that caused it rather than later in the test.
/// </remarks>
public static class Seed
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A name no other test uses: <paramref name="prefix"/>, a dash and 32 hexadecimal digits.</summary>
    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>An email address no other test uses, such as <c>users-1f2e…@inventory.test</c>.</summary>
    public static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@inventory.test";

    /// <summary>
    /// Creates a unit through <c>POST /api/v1/units</c> and checks it got <c>201</c>. <paramref name="manager"/> must
    /// send a Manager's token. Without a <paramref name="name"/>, the unit gets a unique one.
    /// </summary>
    public static async Task<CreatedUnit> CreateUnitAsync(HttpClient manager, string? name = null)
    {
        var response = await manager.PostAsJsonAsync("/api/v1/units", new { name = name ?? UniqueName("unit") }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        return new CreatedUnit(body.GetProperty("id").GetInt32(), body.GetProperty("name").GetString()!);
    }

    /// <summary>
    /// Creates an item with a unique name through <c>POST /api/v1/items</c> and checks it got <c>201</c>. Without a
    /// <paramref name="unit"/>, a new unit is created for it first.
    /// </summary>
    public static async Task<CreatedItem> CreateItemAsync(
        HttpClient manager, ItemType type, CreatedUnit? unit = null, string? description = null)
    {
        unit ??= await CreateUnitAsync(manager);
        var response = await manager.PostAsJsonAsync("/api/v1/items",
            new { name = UniqueName(type.ToString()), description, type = type.ToString(), unitId = unit.Id }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        return new CreatedItem(
            body.GetProperty("id").GetInt32(),
            body.GetProperty("name").GetString()!,
            unit.Id,
            unit.Name,
            body.GetProperty("createdAt").GetString()!);
    }
}

/// <summary>A unit created through the API by <see cref="Seed.CreateUnitAsync"/>.</summary>
public sealed record CreatedUnit(int Id, string Name);

/// <summary>
/// An item created through the API by <see cref="Seed.CreateItemAsync"/>, with the fields tests compare against.
/// </summary>
public sealed record CreatedItem(int Id, string Name, int UnitId, string UnitName, string CreatedAt);
