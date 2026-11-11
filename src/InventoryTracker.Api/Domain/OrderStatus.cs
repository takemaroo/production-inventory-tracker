namespace InventoryTracker.Api.Domain;

/// <summary>
/// The life of a production order (design 3.1): Created, then InProduction when materials are sent, then
/// Completed; or Cancelled from Created or InProduction. Each status is reached at most once.
/// </summary>
/// <remarks>Stored in the database as text (the enum name), exactly as listed in design 2.3.</remarks>
public enum OrderStatus
{
    /// <summary>Created by a Manager (US-13); nothing has been sent yet.</summary>
    Created,

    /// <summary>Materials have been sent (US-15). AC-15.10: at most one order can be in this status at a time.</summary>
    InProduction,

    /// <summary>Output and leftover have been registered (US-19). Final.</summary>
    Completed,

    /// <summary>Cancelled by a Manager with a reason (US-21). Final.</summary>
    Cancelled,
}
