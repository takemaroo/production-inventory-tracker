namespace InventoryTracker.Api.Domain;

/// <summary>
/// The life of a complementary order, a request for extra material during a run (design 3.1, US-16 to US-18).
/// </summary>
/// <remarks>Stored in the database as text (the enum name), exactly as listed in design 2.3.</remarks>
public enum ComplementaryOrderStatus
{
    /// <summary>Requested with a reason; waiting for a Manager (US-16).</summary>
    PendingApproval,

    /// <summary>Approved by a Manager (US-17); the Warehouse can now send it.</summary>
    Approved,

    /// <summary>Rejected by a Manager, optionally with a note (US-17). Final.</summary>
    Rejected,

    /// <summary>The material has been sent (US-18). Final.</summary>
    Sent,

    /// <summary>Cancelled automatically because the parent order was completed or cancelled (AC-19.6, AC-21.4). Final.</summary>
    Cancelled,
}
