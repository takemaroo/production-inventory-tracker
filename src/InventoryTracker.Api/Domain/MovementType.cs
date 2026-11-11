namespace InventoryTracker.Api.Domain;

/// <summary>
/// The six kinds of stock movement (PRD D-2). Stock is never stored as a number someone edits:
/// it is always the sum of movements, so every change has a type, a user and a time on record.
/// </summary>
/// <remarks>
/// The comment on each value shows which location it changes: W = Warehouse, P = Production storage.
/// Stored in the database as text (the enum name).
/// </remarks>
public enum MovementType
{
    /// <summary>W+. Raw material arrives from a supplier (US-09).</summary>
    Received,

    /// <summary>W− and P+ by the same quantity. Material sent for an order or complementary order (US-15, US-18).</summary>
    SentToProduction,

    /// <summary>P−. Material used up, recorded when an order is completed (AC-19.2).</summary>
    ConsumedInProduction,

    /// <summary>W+. Finished product made by a completed order (AC-19.2).</summary>
    ProducedOutput,

    /// <summary>W+ and P− by the same quantity. Leftover returned from production storage (AC-22.4).</summary>
    ReturnedToWarehouse,

    /// <summary>W or P, + or −. A Manager's correction or write-off, always with a reason (US-12).</summary>
    Adjustment,
}
