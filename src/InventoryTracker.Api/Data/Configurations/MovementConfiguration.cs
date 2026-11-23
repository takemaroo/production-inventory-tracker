using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Movement"/> (architecture 3.2).</summary>
public sealed class MovementConfiguration : IEntityTypeConfiguration<Movement>
{
    /// <summary>(v1.1) Name of the index that allows one shipment movement per sales order line; tests refer to it.</summary>
    public const string OneShipmentPerLineIndexName = "IX_Movements_OneShipmentPerLine";

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

        // (v1.1) Decision #157, AC-26.10 backstop: a unique index that only contains movements with a sales order
        // number (the filter), on (sales order, item). A sales order has each item at most once, so this means
        // "at most one shipment movement per line": a second ship of the same sales order is refused by the database
        // even if two requests race past the status check. Movements without a sales order are not in the index, so
        // any number of them are allowed. It is also the index the movement list's salesOrderNumber filter uses
        // (SQLite uses a filtered index when "SalesOrderNumber = 5" implies its filter), and it covers the foreign key
        // below, so EF Core adds no separate index for it.
        builder.HasIndex(m => new { m.SalesOrderNumber, m.ItemId }, OneShipmentPerLineIndexName)
            .IsUnique()
            .HasFilter("\"SalesOrderNumber\" IS NOT NULL");

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

        // (v1.1) AC-28.1: a shipment names its sales order. The foreign key means it can only name one that exists.
        // The SalesOrders migration adds this column in place (architecture 3.4); see the comment there.
        builder.HasOne(m => m.SalesOrder)
            .WithMany()
            .HasForeignKey(m => m.SalesOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
