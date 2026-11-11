namespace InventoryTracker.Api.Domain;

/// <summary>
/// PLACEHOLDER from before the PRD. Task T-002 replaces it with the Item entity in
/// docs/03-architecture.md section 3 (Id, Name, NameNormalized, Description, Type, UnitId, CreatedAt).
/// It is not mapped to the database yet.
/// </summary>
public class Item
{
    /// <summary>Primary key.</summary>
    public int ItemId { get; set; }

    /// <summary>The unit the item is measured in.</summary>
    public int UnitOfMeasureId { get; set; }

    /// <summary>Navigation property to the unit.</summary>
    public UnitOfMeasure? UnitOfMeasure { get; set; }

    /// <summary>Item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Raw material or finished product.</summary>
    public ItemType Type { get; set; }
}
