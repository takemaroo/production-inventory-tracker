using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="ProductionOrder"/> (architecture 3.2).</summary>
public sealed class ProductionOrderConfiguration : IEntityTypeConfiguration<ProductionOrder>
{
    /// <summary>Name of the index that allows at most one InProduction order; tests and error handling can refer to it.</summary>
    public const string OneInProductionIndexName = "IX_ProductionOrders_OneInProduction";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductionOrder> builder)
    {
        builder.ToTable("ProductionOrders");

        // Design 2.1, decision #22: the order number is the key, numbered by the database from 1.
        builder.HasKey(o => o.Number);
        builder.Property(o => o.Number).ValueGeneratedOnAdd();

        builder.Property(o => o.Status).IsStoredAsName();
        builder.Property(o => o.OrderedQuantity).IsQuantity();
        builder.Property(o => o.ProducedQuantity).IsQuantity();
        builder.Property(o => o.OutputVariance).IsQuantity();
        // Decision #82: percentages are decimal(28,6), wider than the (18,6) of amounts per unit, because a variance
        // divided by a tiny ordered quantity (0.001) can reach about 1e14 % (see ColumnRules.IsPercentage).
        builder.Property(o => o.OutputVariancePercent).IsPercentage();
        builder.Property(o => o.ToleranceAtCompletionPercent).IsPercentage();
        builder.Property(o => o.CompletionReason).HasMaxLength(ColumnRules.TextLength);
        builder.Property(o => o.CancellationReason).HasMaxLength(ColumnRules.TextLength);

        builder.HasIndex(o => o.CreatedAt);
        builder.HasIndex(o => o.Status);

        // AC-15.10 backstop (architecture 5.2): a unique index that only contains rows whose status is
        // InProduction. Two such rows would have the same indexed value, so the database refuses the second,
        // while any number of Created, Completed or Cancelled orders are allowed because they are not in the index.
        builder.HasIndex(o => o.Status, OneInProductionIndexName)
            .IsUnique()
            .HasFilter($"\"Status\" = '{nameof(OrderStatus.InProduction)}'");

        builder.HasOne(o => o.FinishedProduct)
            .WithMany()
            .HasForeignKey(o => o.FinishedProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-05.3: who created, sent, completed and cancelled the order.
        builder.HasOne(o => o.CreatedBy).WithMany().HasForeignKey(o => o.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.SentBy).WithMany().HasForeignKey(o => o.SentById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.CompletedBy).WithMany().HasForeignKey(o => o.CompletedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.CancelledBy).WithMany().HasForeignKey(o => o.CancelledById).OnDelete(DeleteBehavior.Restrict);
    }
}
