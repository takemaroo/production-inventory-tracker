namespace InventoryTracker.Api.Auth;

/// <summary>
/// The three roles of PRD section 4.2, and the role combinations that actions use in
/// <c>[Authorize(Roles = ...)]</c>.
/// </summary>
/// <remarks>
/// <c>[Authorize]</c> is an attribute that ASP.NET Core's authorization middleware reads before an action runs.
/// <c>[Authorize(Roles = "Warehouse,Manager")]</c> means "the signed-in user must hold <em>at least one</em> of these
/// roles": the comma means "or" (AC-05.2: a user with several roles may do everything any of them allows).
/// Using these constants instead of typing the strings keeps a typo from silently locking everyone out.
/// <para>
/// Every endpoint without an <c>[Authorize]</c> attribute still needs a signed-in user, because of the fallback
/// policy set up in <see cref="AuthSetup"/>. So "All" in the design's endpoint table (design 4) needs no attribute.
/// </para>
/// </remarks>
public static class Roles
{
    /// <summary>Warehouse staff: receipts, sends, receiving returns.</summary>
    public const string Warehouse = "Warehouse";

    /// <summary>Production line staff: completing orders, requesting complementary orders.</summary>
    public const string Production = "Production";

    /// <summary>The production and inventory manager: everything, including master data, users and notifications.</summary>
    public const string Manager = "Manager";

    /// <summary>PRD 4.2: receipts, sends and receiving returns are allowed to Warehouse and Manager.</summary>
    public const string WarehouseOrManager = Warehouse + "," + Manager;

    /// <summary>PRD 4.2: completing an order and requesting a complementary order are allowed to Production and Manager.</summary>
    public const string ProductionOrManager = Production + "," + Manager;

    /// <summary>All roles, in the order the design lists them (design 2.3). Responses list a user's roles in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Warehouse, Production, Manager];

    /// <summary>Puts <paramref name="roles"/> in the order of <see cref="All"/>, so responses are always the same.</summary>
    public static IReadOnlyList<string> InStandardOrder(IEnumerable<string> roles)
    {
        var held = roles.ToHashSet(StringComparer.Ordinal);
        return All.Where(held.Contains).ToList();
    }
}
