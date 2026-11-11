using InventoryTracker.Api.Auth;

namespace InventoryTracker.Api.Domain;

/// <summary>
/// The bill of materials for one finished product: how much of each raw material one unit needs (US-08).
/// Table <c>Recipes</c>.
/// </summary>
/// <remarks>
/// A finished product has at most one recipe, so the finished product's id is the primary key (there is no
/// separate <c>Id</c>). A finished product "has a recipe" when this row exists, and a recipe always has at least
/// one line (AC-08.2). Saving a recipe replaces all its lines (AC-08.1). Orders copy the lines when they are
/// created (AC-08.4), so changing a recipe never changes an existing order.
/// </remarks>
public class Recipe
{
    /// <summary>Primary key and foreign key to the finished product this recipe makes.</summary>
    public int FinishedProductId { get; set; }

    /// <summary>The finished product, when loaded.</summary>
    public Item FinishedProduct { get; set; } = null!;

    /// <summary>Design 5.4 <c>updatedAt</c>: when the recipe was last saved (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Foreign key to the user who last saved the recipe.</summary>
    public string UpdatedById { get; set; } = string.Empty;

    /// <summary>The user who last saved the recipe, when loaded.</summary>
    public AppUser UpdatedBy { get; set; } = null!;

    /// <summary>The raw materials, in the order they were submitted. Deleted together with the recipe.</summary>
    public List<RecipeLine> Lines { get; set; } = [];
}
