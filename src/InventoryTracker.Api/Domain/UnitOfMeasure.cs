namespace InventoryTracker.Api.Domain;

/// <summary>
/// PLACEHOLDER from before the PRD. Task T-002 replaces it with the UnitOfMeasure entity in
/// docs/03-architecture.md section 3 (Id, Name, NameNormalized). The PRD gives a unit only a name,
/// so Abbreviation is removed there. It is not mapped to the database yet.
/// </summary>
public class UnitOfMeasure
{
    /// <summary>Primary key.</summary>
    public int UnitOfMeasureId { get; set; }

    /// <summary>Unit name, for example "kg" or "Bag".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Not in the PRD; removed by T-002.</summary>
    public string Abbreviation { get; set; } = string.Empty;
}
