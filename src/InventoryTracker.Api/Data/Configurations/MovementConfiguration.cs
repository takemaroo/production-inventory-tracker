using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Movement"/> (architecture 3.2).</summary>
public sealed class MovementConfiguration : IEntityTypeConfiguration<Movement>
{
    /// <summary>(v1.1) Name of the index that allows one shipment movement per sales order line; tests refer to it.</summary>
    public const string OneShipmentPerLineIndexName = "IX_Movements_OneShipmentPerLine";

    /// <summary>
    /// (v1.2, T-035) Name of the covering index the stock sums read (decision #223); the query-plan and upgrade tests
    /// refer to it.
    /// </summary>
    public const string StockSumsIndexName = "IX_Movements_StockSums";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Movement> builder)
    {
        builder.ToTable("Movements");

        builder.Property(m => m.Type).IsStoredAsName();
        builder.Property(m => m.WarehouseChange).IsQuantity();
        builder.Property(m => m.ProductionStorageChange).IsQuantity();
        builder.Property(m => m.Note).HasMaxLength(ColumnRules.TextLength);
        builder.Property(m => m.Reason).HasMaxLength(ColumnRules.TextLength);

        // Indexes for the movement history filters (US-11). IX_Movements_ItemId stays for the movement list filtered by
        // item, whose rows need every column anyway; the stock sums use the covering index below.
        builder.HasIndex(m => m.Type);
        builder.HasIndex(m => m.ItemId);
        builder.HasIndex(m => m.OccurredAt);
        builder.HasIndex(m => m.ProductionOrderNumber);
        builder.HasIndex(m => m.ComplementaryOrderNumber);

        // (v1.2) T-035, SEC-24, decision #223: the covering index for the stock sums (D-2, architecture 5.1).
        //
        // What "covering" means. An index is a separate, sorted copy of some columns, each entry pointing at its row
        // in the table. A query that needs only columns held in the index is "covered": SQLite answers it from the
        // index alone and never opens the table. COUNT(*) per item was already covered by IX_Movements_ItemId. A stock
        // SUM was not: for every movement SQLite had to follow the pointer and fetch the row to read its change,
        // rows scattered across a table (about 17 MB at 100,000 movements) far bigger than SQLite's 2 MB page cache.
        // This index holds the item and both changes, sorted by item, so a sum reads one item's movements side by
        // side in the index.
        //
        // Measured by the tech lead, 500 items and 100,000 movements: a grouped sum 330 to 370 ms without it, about
        // 60 ms with it; 3.9 s against about 350 ms at 1,000,000 movements. The cost is a little more disk space and a
        // little more work per insert, which movements (insert-only, AC-11.3) can afford.
        builder.HasIndex(m => new { m.ItemId, m.WarehouseChange, m.ProductionStorageChange }, StockSumsIndexName);

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
