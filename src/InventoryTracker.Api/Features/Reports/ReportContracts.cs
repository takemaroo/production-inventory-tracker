using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Reports;

/// <summary>
/// (v1.1) One row of <c>GET /api/v1/reports/shipped-by-item</c> (US-32, AC-32.1, design 5.13): how much of one item was
/// shipped in the period, and on how many sales orders, for example
/// <c>{ "itemId": 7, "itemName": "Shredded cheese 1 kg bag", "itemType": "FinishedProduct", "unit": "Bag",
/// "shippedQuantity": 1201, "salesOrderCount": 3 }</c>.
/// </summary>
/// <remarks>
/// A total is calculated when it is asked for, never stored (decision #160), so there is nothing that could drift away
/// from the sales orders it sums. A row exists only for an item with something shipped in the period, so
/// <c>shippedQuantity</c> is always above zero (design 5.13).
/// </remarks>
/// <param name="ItemId">The item's id.</param>
/// <param name="ItemName">The item's current name.</param>
/// <param name="ItemType">RawMaterial or FinishedProduct: either can be sold (decision #144).</param>
/// <param name="Unit">The item's unit name, the unit of <paramref name="ShippedQuantity"/>.</param>
/// <param name="ShippedQuantity">
/// D-13: the sum of the item's line quantities on sales orders shipped in the period. It equals the sum of the item's
/// <c>ShippedToCustomer</c> movements in the period with the sign reversed (AC-32.4).
/// </param>
/// <param name="SalesOrderCount">How many of those sales orders have a line for the item (a sales order has an item at most once).</param>
public sealed record ShippedByItemResponse(
    int ItemId,
    string ItemName,
    ItemType ItemType,
    string Unit,
    decimal ShippedQuantity,
    int SalesOrderCount);

/// <summary>
/// (v1.1) One row of <c>GET /api/v1/reports/shipped-by-customer</c> (US-33, AC-33.1, design 5.13): how much of one item
/// was shipped to one customer in the period, and on how many of that customer's sales orders. There is no total per
/// customer across items, because items have different units.
/// </summary>
/// <param name="Customer">The customer's id, current name and active status, read now (AC-29.3, AC-33.4).</param>
/// <param name="ItemId">The item's id.</param>
/// <param name="ItemName">The item's current name.</param>
/// <param name="ItemType">RawMaterial or FinishedProduct.</param>
/// <param name="Unit">The item's unit name, the unit of <paramref name="ShippedQuantity"/>.</param>
/// <param name="ShippedQuantity">D-13: the item's line quantities on this customer's sales orders shipped in the period.</param>
/// <param name="SalesOrderCount">How many of this customer's sales orders shipped in the period have a line for the item.</param>
public sealed record ShippedByCustomerResponse(
    CustomerSummary Customer,
    int ItemId,
    string ItemName,
    ItemType ItemType,
    string Unit,
    decimal ShippedQuantity,
    int SalesOrderCount);
