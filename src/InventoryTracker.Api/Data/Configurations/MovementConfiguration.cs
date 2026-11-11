using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Movement"/> (architecture 3.2).</summary>
public sealed class MovementConfiguration : IEntityTypeConfiguration<Movement>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Movement> builder)
    {
        builder.ToTable("Movements");

        builder.Property(m => m.Type).IsStoredAsName();
        builder.Property(m => m.WarehouseChange).IsQuantity();
        builder.Property(m => m.ProductionStorageChange).IsQuantity();
        builder.Property(m => m.Note).HasMaxLength(ColumnRules.TextLength);
        builder.Property(m => m.Reason).HasMaxLength(ColumnRules.TextLength);

        // Indexes for the movement history filters (US-11) and the per-item stock sums (D-2, architecture 5.1).
        builder.HasIndex(m => m.Type);
        builder.HasIndex(m => m.ItemId);
        builder.HasIndex(m => m.OccurredAt);
        builder.HasIndex(m => m.ProductionOrderNumber);
        builder.HasIndex(m => m.ComplementaryOrderNumber);

        builder.HasOne(m => m.Item)
            .WithMany()
            .HasForeignKey(m => m.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-05.3: who performed the movement.
        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.ProductionOrder)
            .WithMany()
            .HasForeignKey(m => m.ProductionOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.ComplementaryOrder)
            .WithMany()
            .HasForeignKey(m => m.ComplementaryOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.Return)
            .WithMany()
            .HasForeignKey(m => m.ReturnNumber)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
