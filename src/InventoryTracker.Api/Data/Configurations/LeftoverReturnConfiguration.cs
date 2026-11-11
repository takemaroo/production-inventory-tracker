using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="LeftoverReturn"/> (architecture 3.2).</summary>
public sealed class LeftoverReturnConfiguration : IEntityTypeConfiguration<LeftoverReturn>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LeftoverReturn> builder)
    {
        builder.ToTable("Returns");

        // Design 2.1: its own number sequence, used as the key.
        builder.HasKey(r => r.Number);
        builder.Property(r => r.Number).ValueGeneratedOnAdd();

        builder.Property(r => r.Status).IsStoredAsName();
        builder.Property(r => r.Quantity).IsQuantity();
        builder.Property(r => r.Reason).HasMaxLength(ColumnRules.TextLength);

        // D-4 and the send checks look up the Pending returns of one item.
        builder.HasIndex(r => new { r.Status, r.ItemId });

        builder.HasOne(r => r.Item)
            .WithMany()
            .HasForeignKey(r => r.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ProductionOrder)
            .WithMany()
            .HasForeignKey(r => r.ProductionOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-05.3: who instructed, received and cancelled the return.
        builder.HasOne(r => r.CreatedBy).WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.ReceivedBy).WithMany().HasForeignKey(r => r.ReceivedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.CancelledBy).WithMany().HasForeignKey(r => r.CancelledById).OnDelete(DeleteBehavior.Restrict);
    }
}
