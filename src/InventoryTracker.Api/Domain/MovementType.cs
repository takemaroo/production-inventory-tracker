namespace InventoryTracker.Api.Domain;

/// <summary>
/// The seven kinds of stock movement (PRD D-2). Stock is never stored as a number someone edits:
/// it is always the sum of movements, so every change has a type, a user and a time on record.
/// </summary>
/// <remarks>
/// <para>
/// The comment on each value shows which location it changes: W = Warehouse, P = Production storage.
/// Stored in the database as text (the enum name).
/// </para>
/// <para>
/// (v1.1) The order of the values is the design 2.3 order, and it matters beyond tidiness: messages that list the
/// accepted values ("type must be Received, ..., Adjustment or ShippedToCustomer.") are built from this enum in
/// declaration order (<c>EnumParser.AllowedValuesMessage</c>). That is why <see cref="ShippedToCustomer"/> comes last.
/// Adding a value changes no stored row, because rows hold names, not numbers.
/// </para>
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

    /// <summary>
    /// (v1.1) W− only. Goods leave the Warehouse for a customer when a sales order ships (US-26, AC-28.1). Any item can
    /// be shipped, raw material or finished product (AC-24.8). It always names its sales order and never touches
    /// production storage (D-11), so a sale can be told apart from a write-off in the history (US-28).
    /// </summary>
    ShippedToCustomer,
}
