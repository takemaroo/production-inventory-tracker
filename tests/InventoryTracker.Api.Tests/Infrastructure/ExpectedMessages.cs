namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// The exact message texts that tests of many endpoints check: the sign-in and role refusals (<c>401</c> and
/// <c>403</c>, design 7.1), the list of known roles (design 7.2), the input maximums (design 7.2, decision #80) and
/// (v1.1) the PRD's customer and sales order sentences (design 7.2, Customers and Sales orders).
/// Kept in one place so every endpoint's tests compare against the same wording.
/// </summary>
/// <remarks>
/// <para>
/// The texts are typed out here rather than taken from the API's own helpers (such as <c>Quantity.AboveMaxMessage</c>),
/// so a wrong message in the API fails the tests instead of being copied into them.
/// </para>
/// <para>
/// These are <c>const</c>, not <c>static readonly</c>, so they can also be used inside attributes such as
/// <c>[InlineData(...)]</c>, which only accept values known when the code is compiled.
/// </para>
/// </remarks>
public static class ExpectedMessages
{
    /// <summary>AC-01.5: <c>401</c> <c>not-authenticated</c>, for a missing, expired or otherwise invalid token.</summary>
    public const string NotAuthenticated = "Your sign-in is missing or has expired. Sign in again.";

    /// <summary>AC-01.4: <c>401</c> <c>account-locked</c>, after five wrong passwords in a row.</summary>
    public const string AccountLocked = "Account temporarily locked. Try again later.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action only a Manager may do.</summary>
    public const string ManagerOnly = "Your roles do not allow this action. It needs the Manager role.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action for the Warehouse or Manager role.</summary>
    public const string WarehouseOrManager = "Your roles do not allow this action. It needs the Warehouse or Manager role.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action for the Production or Manager role.</summary>
    public const string ProductionOrManager = "Your roles do not allow this action. It needs the Production or Manager role.";

    /// <summary>
    /// (v1.1) Design 7.1, AC-30.6: <c>403</c> <c>forbidden</c> on a read for Warehouse, Production or Manager (recipes,
    /// movements, production orders, complementary orders, returns), for example to a Seller-only user.
    /// </summary>
    public const string WarehouseProductionOrManager =
        "Your roles do not allow this action. It needs the Warehouse, Production or Manager role.";

    /// <summary>
    /// (v1.1) Design 7.1, AC-29.7, AC-25.4: <c>403</c> <c>forbidden</c> on a read for Warehouse, Manager or Seller
    /// (customers, sales orders, the shipped-totals reports), for example to a Production-only user.
    /// </summary>
    public const string WarehouseManagerOrSeller =
        "Your roles do not allow this action. It needs the Warehouse, Manager or Seller role.";

    /// <summary>
    /// (v1.1) Design 7.1, AC-29.7: <c>403</c> <c>forbidden</c> on a customer change or a sales order create or cancel,
    /// for example to a Warehouse-only user.
    /// </summary>
    public const string ManagerOrSeller = "Your roles do not allow this action. It needs the Manager or Seller role.";

    /// <summary>
    /// (v1.1) AC-30.1, design 7.2: the end of the <c>400</c> message for an unknown role name, which now lists four
    /// roles. The whole message is "Unknown role '{name}'. " followed by this.
    /// </summary>
    public const string RolesAre = "Roles are Warehouse, Production, Manager and Seller.";

    /// <summary>
    /// Decision #80, design 7.2: <c>400</c> <c>validation</c> for a field labelled "Quantity" (receipts, sends,
    /// complementary sends, adjustments, returns) above 999,999,999.999.
    /// </summary>
    public const string QuantityAboveMax = "Quantity must be 999,999,999.999 or less.";

    /// <summary>Decision #80, design 7.2: <c>400</c> <c>validation</c> for a recipe amount per unit above 999,999.999999.</summary>
    public const string AmountPerUnitAboveMax = "Amount per unit must be 999,999.999999 or less.";

    /// <summary>
    /// (v1.1) AC-29.2, PRD example: <c>409</c> <c>customer-name-taken</c> when another customer is already called
    /// "Deli Norte", ignoring case. The name quoted is the one sent, trimmed.
    /// </summary>
    public const string DeliNorteNameTaken = "A customer named 'Deli Norte' already exists.";

    /// <summary>
    /// (v1.1) AC-29.5, PRD example with decision #152's next step: <c>409</c> <c>customer-has-sales-orders</c> on deleting
    /// "Deli Norte", which has a sales order in some status.
    /// </summary>
    public const string DeliNorteHasSalesOrders = "Deli Norte has sales orders and cannot be deleted. Deactivate it instead.";

    /// <summary>
    /// (v1.1) Design 7.2, architecture 4.1 item 19: <c>400</c> <c>validation</c> under <c>isActive</c> for a value other
    /// than <c>true</c> or <c>false</c> on <c>GET /customers</c>.
    /// </summary>
    public const string IsActiveMustBeBoolean = "isActive must be true or false.";

    /// <summary>
    /// (v1.1) AC-24.4, PRD example with decision #152's next step: <c>409</c> <c>customer-inactive</c> on a new sales order
    /// for "Deli Norte" after it was deactivated.
    /// </summary>
    public const string DeliNorteInactive =
        "Customer Deli Norte is deactivated. Reactivate the customer or choose another one.";

    /// <summary>(v1.1) AC-24.5, design 7.2: <c>400</c> <c>too-many-lines</c> under <c>lines</c>, the same on every line-carrying body.</summary>
    public const string TooManyLines = "At most 500 lines are allowed.";

    /// <summary>
    /// (v1.1) Design 7.2, architecture 4.1 item 19: <c>400</c> <c>validation</c> under <c>status</c> for an unknown value
    /// on <c>GET /sales-orders</c> (AC-25.1).
    /// </summary>
    public const string SalesOrderStatusValues = "status must be Created, Shipped or Cancelled.";

    /// <summary>
    /// (v1.1) AC-26.7, AC-26.10, design 7.2: <c>409</c> <c>already-shipped</c> on shipping a sales order that is already
    /// Shipped, including the loser of two shipments at the same moment.
    /// </summary>
    public const string AlreadyShipped = "This sales order has already been shipped.";

    /// <summary>
    /// (v1.1) AC-26.7, AC-27.3, design 7.2: <c>409</c> <c>sales-order-cancelled</c> on shipping a Cancelled sales order.
    /// </summary>
    public const string ShipCancelled = "This sales order is cancelled.";
}
