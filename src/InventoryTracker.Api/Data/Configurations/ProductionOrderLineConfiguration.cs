using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="ProductionOrderLine"/> (architecture 3.2).</summary>
public sealed class ProductionOrderLineConfiguration : IEntityTypeConfiguration<ProductionOrderLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductionOrderLine> builder)
    {
        builder.ToTable("ProductionOrderLines");

        builder.Property(l => l.AmountPerUnit).IsFineDecimal();
        builder.Property(l => l.PlannedAmount).IsQuantity();
        builder.Property(l => l.LeftoverApplied).IsQuantity();
        builder.Property(l => l.WarehouseSend).IsQuantity();
        builder.Property(l => l.CarriedLeftover).IsQuantity();
        builder.Property(l => l.ComplementarySends).IsQuantity();
        builder.Property(l => l.LeftoverEntered).IsQuantity();
        builder.Property(l => l.LeftoverRemaining).IsQuantity();
        builder.Property(l => l.ActualUsage).IsQuantity();
        builder.Property(l => l.RequiredForActualOutput).IsQuantity();
        builder.Property(l => l.UsageVariance).IsQuantity();
        // Decision #82: a percentage column, decimal(28,6); see ColumnRules.IsPercentage for why it is wider.
        builder.Property(l => l.UsageVariancePercent).IsPercentage();

        // A raw material appears at most once per order, as on the recipe it was copied from (AC-08.2).
        builder.HasIndex(l => new { l.OrderNumber, l.RawMaterialId }).IsUnique();

        // Restrict, not Cascade: orders are never deleted (PRD section 5), so their lines must not be either.
        builder.HasOne(l => l.Order)
            .WithMany(o => o.Lines)
            .HasForeignKey(l => l.OrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.RawMaterial)
            .WithMany()
            .HasForeignKey(l => l.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
