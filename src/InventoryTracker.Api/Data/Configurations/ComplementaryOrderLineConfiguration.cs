using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="ComplementaryOrderLine"/> (architecture 3.2).</summary>
public sealed class ComplementaryOrderLineConfiguration : IEntityTypeConfiguration<ComplementaryOrderLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ComplementaryOrderLine> builder)
    {
        builder.ToTable("ComplementaryOrderLines");

        builder.Property(l => l.RequestedAmount).IsQuantity();
        builder.Property(l => l.SentQuantity).IsQuantity();

        // A raw material appears at most once per complementary order.
        builder.HasIndex(l => new { l.ComplementaryOrderNumber, l.RawMaterialId }).IsUnique();

        builder.HasOne(l => l.ComplementaryOrder)
            .WithMany(c => c.Lines)
            .HasForeignKey(l => l.ComplementaryOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.RawMaterial)
            .WithMany()
            .HasForeignKey(l => l.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
