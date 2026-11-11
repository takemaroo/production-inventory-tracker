namespace InventoryTracker.Api.Domain;

/// <summary>
/// The life of a leftover return from production storage to the Warehouse (design 3.1, US-22, PRD section 6 row 3).
/// </summary>
/// <remarks>Stored in the database as text (the enum name), exactly as listed in design 2.3.</remarks>
public enum ReturnStatus
{
    /// <summary>Instructed by a Manager. D-4: the quantity no longer counts as free leftover.</summary>
    Pending,

    /// <summary>The Warehouse recorded receiving it, which writes a ReturnedToWarehouse movement (AC-22.4). Final.</summary>
    Received,

    /// <summary>Cancelled by a Manager before it was received. Final.</summary>
    Cancelled,
}
