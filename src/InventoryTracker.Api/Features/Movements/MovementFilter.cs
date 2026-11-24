using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Movements;

/// <summary>
/// The filters of <c>GET /api/v1/movements</c> (AC-11.2, design 5.6), read from the query string, for example
/// <c>?itemId=3&amp;type=Received&amp;from=2026-11-01&amp;to=2026-11-30</c>. Every filter is optional; the ones given
/// combine with AND (design 2.5).
/// </summary>
/// <remarks>
/// <para>
/// The response row is not defined here: movements have one shape everywhere, <c>MovementResponse</c> in
/// <c>Features/Stock/MovementContracts.cs</c> (design 5.6), which receipts and adjustments return too.
/// </para>
/// <para>
/// The ids and numbers are <c>int?</c>, so model binding converts them and refuses text such as <c>?itemId=abc</c>
/// with a <c>400</c> before the action runs, the same as <c>?page=abc</c>. A missing or empty
/// value stays null and means "no filter". An id or number that does not exist is not an error: it simply matches
/// nothing (design 2.5, AC-11.2). The enum and date filters arrive as text and are parsed by the service with the
/// design's own messages (architecture 4.1 item 3), so <c>?type=Sent</c> or <c>?from=yesterday</c> names the accepted
/// values.
/// </para>
/// <para>
/// <c>[FromQuery(Name = ...)]</c> on each property fixes the query key, so the C# property name can change without
/// changing the API.
/// </para>
/// </remarks>
public sealed class MovementFilter
{
    /// <summary>Only movements of this item.</summary>
    [FromQuery(Name = "itemId")]
    public int? ItemId { get; set; }

    /// <summary>
    /// <c>Warehouse</c> or <c>ProductionStorage</c>, any letter case: only movements that change that location
    /// (design 5.6). A transfer changes both, so it matches either.
    /// </summary>
    [FromQuery(Name = "location")]
    public string? Location { get; set; }

    /// <summary>One movement type (D-2), any letter case, for example <c>Received</c>.</summary>
    [FromQuery(Name = "type")]
    public string? Type { get; set; }

    /// <summary>
    /// Movements of this production order, including the sends of its complementary orders (design 5.6). (v1.1) Never a
    /// shipment: production orders and sales orders are numbered separately (D-10), so production order #1 and sales
    /// order #1 can both exist, and this filter only ever means the production order (AC-28.2).
    /// </summary>
    [FromQuery(Name = "orderNumber")]
    public int? OrderNumber { get; set; }

    /// <summary>Movements of this complementary order (its sends).</summary>
    [FromQuery(Name = "complementaryOrderNumber")]
    public int? ComplementaryOrderNumber { get; set; }

    /// <summary>
    /// (v1.1) Movements of this sales order: its <c>ShippedToCustomer</c> movements, one per line once it has shipped
    /// (AC-28.2, design 5.6). Production users may filter by it even though they cannot open the sales order (AC-28.7).
    /// </summary>
    [FromQuery(Name = "salesOrderNumber")]
    public int? SalesOrderNumber { get; set; }

    /// <summary>First day included, <c>yyyy-MM-dd</c>, a whole UTC day compared with <c>occurredAt</c>.</summary>
    [FromQuery(Name = "from")]
    public string? From { get; set; }

    /// <summary>Last day included, <c>yyyy-MM-dd</c>, a whole UTC day compared with <c>occurredAt</c>.</summary>
    [FromQuery(Name = "to")]
    public string? To { get; set; }
}
