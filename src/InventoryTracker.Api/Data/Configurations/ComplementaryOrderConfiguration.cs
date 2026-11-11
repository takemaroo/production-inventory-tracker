using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="ComplementaryOrder"/> (architecture 3.2).</summary>
public sealed class ComplementaryOrderConfiguration : IEntityTypeConfiguration<ComplementaryOrder>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ComplementaryOrder> builder)
    {
        builder.ToTable("ComplementaryOrders");

        // Design 2.1: its own number sequence, used as the key.
        builder.HasKey(c => c.Number);
        builder.Property(c => c.Number).ValueGeneratedOnAdd();

        builder.Property(c => c.Status).IsStoredAsName();
        builder.Property(c => c.Reason).HasMaxLength(ColumnRules.TextLength);
        builder.Property(c => c.DecisionNote).HasMaxLength(ColumnRules.TextLength);
        builder.Property(c => c.CancellationNote).HasMaxLength(ColumnRules.CancellationNoteLength);

        builder.HasIndex(c => c.ParentOrderNumber);
        builder.HasIndex(c => c.Status);

        builder.HasOne(c => c.ParentOrder)
            .WithMany()
            .HasForeignKey(c => c.ParentOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-05.3: who requested, approved, rejected, sent and (automatically) cancelled it.
        builder.HasOne(c => c.RequestedBy).WithMany().HasForeignKey(c => c.RequestedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.ApprovedBy).WithMany().HasForeignKey(c => c.ApprovedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.RejectedBy).WithMany().HasForeignKey(c => c.RejectedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.SentBy).WithMany().HasForeignKey(c => c.SentById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.CancelledBy).WithMany().HasForeignKey(c => c.CancelledById).OnDelete(DeleteBehavior.Restrict);
    }
}
