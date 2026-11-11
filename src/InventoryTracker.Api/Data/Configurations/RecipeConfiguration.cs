using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Recipe"/> (architecture 3.2).</summary>
public sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes");

        // One recipe per finished product: the product's id is the key, so it is set by us, never generated.
        builder.HasKey(r => r.FinishedProductId);
        builder.Property(r => r.FinishedProductId).ValueGeneratedNever();

        builder.HasOne(r => r.FinishedProduct)
            .WithOne()
            .HasForeignKey<Recipe>(r => r.FinishedProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.UpdatedBy)
            .WithMany()
            .HasForeignKey(r => r.UpdatedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
