namespace InventoryTracker.Api.Domain;

/// <summary>
/// (v1.1) The life of a sales order (D-10, design 3.13): Created, then either Shipped (US-26) or Cancelled (US-27).
/// Shipped and Cancelled are final, so each status is reached at most once.
/// </summary>
/// <remarks>
/// Stored in the database as text (the enum name), exactly as listed in design 2.3. The order of the values is the
/// order used wherever they are named in a message ("status must be Created, Shipped or Cancelled.").
/// </remarks>
public enum SalesOrderStatus
{
    /// <summary>Recorded by a Seller or Manager (US-24); nothing has left the Warehouse. Counts as open demand (D-12).</summary>
    Created,

    /// <summary>Every line has left the Warehouse in one shipment (AC-26.1). Final.</summary>
    Shipped,

    /// <summary>Cancelled with a reason before it was shipped (AC-27.1). No movement is written. Final.</summary>
    Cancelled,
}
