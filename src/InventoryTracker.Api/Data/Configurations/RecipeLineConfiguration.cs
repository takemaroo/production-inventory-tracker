using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="RecipeLine"/> (architecture 3.2).</summary>
public sealed class RecipeLineConfiguration : IEntityTypeConfiguration<RecipeLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RecipeLine> builder)
    {
        builder.ToTable("RecipeLines");

        builder.Property(l => l.AmountPerUnit).IsFineDecimal();

        // AC-08.2: the same raw material cannot appear on two lines of one recipe.
        builder.HasIndex(l => new { l.FinishedProductId, l.RawMaterialId }).IsUnique();

        // The one Cascade in the model (architecture 3.1): lines belong to their recipe, and saving a recipe
        // replaces its lines (AC-08.1), so deleting the recipe row deletes its lines with it.
        builder.HasOne(l => l.Recipe)
            .WithMany(r => r.Lines)
            .HasForeignKey(l => l.FinishedProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.RawMaterial)
            .WithMany()
            .HasForeignKey(l => l.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
