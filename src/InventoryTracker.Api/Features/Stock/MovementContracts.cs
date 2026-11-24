using System.Linq.Expressions;
using InventoryTracker.Api.Common.Contracts;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Stock;

/// <summary>
/// A stock movement as the API returns it (design 5.6). The <c>201</c> body of <c>POST /receipts</c> (T-008) and
/// <c>POST /adjustments</c> (T-015), and the row of <c>GET /movements</c> (T-013).
/// </summary>
/// <remarks>
/// Design 2.3: every field is always present, with <c>null</c> for a link the movement does not have. So (v1.1)
/// <c>salesOrderNumber</c> appears on every movement, receipts and adjustments included, and is a number only on a
/// <c>ShippedToCustomer</c> movement (design 5.6, architecture 4.1 item 20). The JSON fields follow the order of the
/// parameters below, which is the order of the design 5.6 example.
/// </remarks>
/// <param name="Id">The movement's id; its URL is <c>/api/v1/movements/{id}</c>.</param>
/// <param name="Type">D-2: what kind of change it is.</param>
/// <param name="ItemId">The item whose stock changed.</param>
/// <param name="ItemName">The item's name.</param>
/// <param name="Unit">The item's unit name (design 2.4: the unit travels with the quantity).</param>
/// <param name="WarehouseChange">Signed change to the Warehouse, or null when the Warehouse is not touched.</param>
/// <param name="ProductionStorageChange">Signed change to Production storage, or null when it is not touched.</param>
/// <param name="OccurredAt">AC-05.3: when it happened, UTC, whole seconds.</param>
/// <param name="User">AC-05.3: who performed it.</param>
/// <param name="OrderNumber">The production order it belongs to; for a complementary send, the parent order.</param>
/// <param name="ComplementaryOrderNumber">The complementary order of a complementary send, otherwise null.</param>
/// <param name="ReturnNumber">The return a ReturnedToWarehouse movement completes, otherwise null.</param>
/// <param name="SalesOrderNumber">(v1.1) The sales order a ShippedToCustomer movement belongs to, otherwise null (AC-28.1).</param>
/// <param name="ReceiptDate">The delivery date of a Received movement, otherwise null (<c>"2026-11-11"</c>).</param>
/// <param name="Note">The receipt note, or null.</param>
/// <param name="Reason">The adjustment reason, or null.</param>
public sealed record MovementResponse(
    int Id,
    MovementType Type,
    int ItemId,
    string ItemName,
    string Unit,
    decimal? WarehouseChange,
    decimal? ProductionStorageChange,
    DateTime OccurredAt,
    PersonResponse User,
    int? OrderNumber,
    int? ComplementaryOrderNumber,
    int? ReturnNumber,
    int? SalesOrderNumber,
    DateOnly? ReceiptDate,
    string? Note,
    string? Reason);

/// <summary>
/// The one definition of how a movement becomes its response (design 5.6), shared by every endpoint that returns
/// movements, so the shape cannot drift between them.
/// </summary>
public static class MovementMapping
{
    /// <summary>
    /// Pass to <c>Select</c> on a movements query. It is an <see cref="Expression{TDelegate}"/>, so EF Core translates
    /// it into SQL that joins the item, its unit and the user and reads only the columns the response needs (the same
    /// technique as <c>ItemService</c>).
    /// </summary>
    public static readonly Expression<Func<Movement, MovementResponse>> AsResponse = movement => new MovementResponse(
        movement.Id,
        movement.Type,
        movement.ItemId,
        movement.Item.Name,
        movement.Item.Unit.Name,
        movement.WarehouseChange,
        movement.ProductionStorageChange,
        movement.OccurredAt,
        new PersonResponse(movement.User.Id, movement.User.Email!),
        movement.ProductionOrderNumber,
        movement.ComplementaryOrderNumber,
        movement.ReturnNumber,
        movement.SalesOrderNumber,
        movement.ReceiptDate,
        movement.Note,
        movement.Reason);
}
