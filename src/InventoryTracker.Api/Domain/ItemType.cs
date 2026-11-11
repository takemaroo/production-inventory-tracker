namespace InventoryTracker.Api.Domain;

/// <summary>
/// The two kinds of item the business handles (PRD AC-07.1). Raw materials are bought and consumed;
/// finished products are made by production orders. Many rules depend on this, for example
/// receipts are for raw materials only (AC-09.3) and recipes are for finished products only (AC-08.2).
/// </summary>
/// <remarks>Stored in the database as text (the enum name), so the values can be reordered safely.</remarks>
public enum ItemType
{
    /// <summary>Material bought from suppliers and used up in production, for example Cheese (kg).</summary>
    RawMaterial,

    /// <summary>Product made from raw materials by a production order, for example a 1 kg bag of shredded cheese.</summary>
    FinishedProduct,
}
