namespace InventoryTracker.Api.Domain;

/// <summary>
/// PLACEHOLDER from before the PRD. Task T-002 replaces it with the Movement entity in
/// docs/03-architecture.md section 3, which adds one signed change per location, the user,
/// and the links to orders, complementary orders and returns (D-2). It is not mapped to the database yet.
/// </summary>
public class Movement
{
    /// <summary>Primary key.</summary>
    public int MovementId { get; set; }

    /// <summary>The item whose stock changed.</summary>
    public int ItemId { get; set; }

    /// <summary>Navigation property to the item.</summary>
    public Item? Item { get; set; }

    /// <summary>When the movement happened (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Signed quantity.</summary>
    public decimal Quantity { get; set; }

    /// <summary>What kind of movement this is.</summary>
    public MovementType Type { get; set; }
}
