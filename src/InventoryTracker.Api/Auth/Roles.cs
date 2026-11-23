namespace InventoryTracker.Api.Auth;

/// <summary>
/// The four roles of PRD section 4.2 (Warehouse, Production, Manager and, from v1.1, Seller), and the role combinations
/// that actions use in <c>[Authorize(Roles = ...)]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>[Authorize]</c> is an attribute that ASP.NET Core's authorization middleware reads before an action runs.
/// <c>[Authorize(Roles = "Warehouse,Manager")]</c> means "the signed-in user must hold <em>at least one</em> of these
/// roles": the comma means "or" (AC-05.2: a user with several roles may do everything any of them allows).
/// Using these constants instead of typing the strings keeps a typo from silently locking everyone out.
/// </para>
/// <para>
/// <b>Two attributes mean "and", not "or"</b> (architecture 4.1 item 13, decision #156). Inside one attribute the comma
/// is "or", but when an endpoint carries several <c>[Authorize]</c> attributes, for example one on the controller class
/// and one on the action, ASP.NET Core turns each into its own requirement and the user must pass <em>all</em> of them.
/// So a class-level <c>[Authorize(Roles = WarehouseProductionOrManager)]</c> plus an action-level
/// <c>[Authorize(Roles = Manager)]</c> would let only Managers in, and a Seller's <c>403</c> would name the roles of
/// both failed requirements. That is why every action names its own roles, and a controller-level attribute is used
/// only when every action of the controller shares it (<c>UsersController</c>, <c>NotificationsController</c>).
/// </para>
/// <para>
/// <b>The order inside each combination matters.</b> <see cref="ForbiddenResponseHandler.Message"/> names the roles in
/// the order the requirement lists them, so each constant is written in <see cref="All"/> order (Warehouse,
/// Production, Manager, Seller) and its <c>403</c> sentence then reads exactly as design 7.1 says.
/// </para>
/// <para>
/// Every endpoint without an <c>[Authorize]</c> attribute still needs a signed-in user, because of the fallback
/// policy set up in <see cref="AuthSetup"/>. So "All" in the design's endpoint table (design 4) needs no attribute:
/// units, items, stock and change-password (AC-30.7). Every user holds at least one role, so those never answer
/// <c>403</c>.
/// </para>
/// </remarks>
public static class Roles
{
    /// <summary>Warehouse staff: receipts, sends, receiving returns, (v1.1) shipping sales orders.</summary>
    public const string Warehouse = "Warehouse";

    /// <summary>Production line staff: completing orders, requesting complementary orders.</summary>
    public const string Production = "Production";

    /// <summary>The production and inventory manager: everything, including master data, users and notifications.</summary>
    public const string Manager = "Manager";

    /// <summary>
    /// (v1.1, US-30) Sales staff: customers and sales orders, and reading units, items, stock and the shipped-totals
    /// reports. A Seller does not ship, and cannot read recipes, movements, production or complementary orders,
    /// returns, users or notifications (AC-30.4, AC-30.6, decision #147).
    /// </summary>
    public const string Seller = "Seller";

    /// <summary>PRD 4.2: receipts, sends and receiving returns ((v1.1) and shipping) are allowed to Warehouse and Manager.</summary>
    public const string WarehouseOrManager = Warehouse + "," + Manager;

    /// <summary>PRD 4.2: completing an order and requesting a complementary order are allowed to Production and Manager.</summary>
    public const string ProductionOrManager = Production + "," + Manager;

    /// <summary>
    /// (v1.1) PRD 4.2, "View recipes, movement history, orders, complementary orders, returns": the three v1 roles, so a
    /// Seller-only user gets <c>403</c> on these reads (AC-30.6).
    /// </summary>
    public const string WarehouseProductionOrManager = Warehouse + "," + Production + "," + Manager;

    /// <summary>
    /// (v1.1) PRD 4.2, "View customers, sales orders and shipped-totals reports": a Production-only user gets <c>403</c>
    /// (AC-25.4, AC-29.7, AC-32.6, AC-33.2).
    /// </summary>
    public const string WarehouseManagerOrSeller = Warehouse + "," + Manager + "," + Seller;

    /// <summary>
    /// (v1.1) PRD 4.2, "Create, edit, deactivate, reactivate or delete a customer" and "Create or cancel a sales order".
    /// </summary>
    public const string ManagerOrSeller = Manager + "," + Seller;

    /// <summary>
    /// All roles, in the order the design lists them (design 2.3). Responses and tokens list a user's roles in this
    /// order (decision #56), role names in requests are matched against it, and start-up creates each one that is
    /// missing, which is how an existing database gains Seller on its first v1.1 start (AC-30.3, decision #155).
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Warehouse, Production, Manager, Seller];

    /// <summary>Puts <paramref name="roles"/> in the order of <see cref="All"/>, so responses are always the same.</summary>
    public static IReadOnlyList<string> InStandardOrder(IEnumerable<string> roles)
    {
        var held = roles.ToHashSet(StringComparer.Ordinal);
        return All.Where(held.Contains).ToList();
    }
}
