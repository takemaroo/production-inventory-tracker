namespace InventoryTracker.Api.Domain;

/// <summary>
/// The two fixed stock locations (PRD section 3, decision #2). Neither may go below zero (D-2, AC-12.3).
/// General multi-warehouse support is on the PRD's "Later" list, so this is an enum rather than a table.
/// </summary>
public enum Location
{
    /// <summary>The main store: receipts arrive here and finished products are put away here.</summary>
    Warehouse,

    /// <summary>Storage at the production line, where sent material and leftover are kept between orders.</summary>
    ProductionStorage,
}
