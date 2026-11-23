using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>(v1.1) Table rules for <see cref="SalesOrder"/> (architecture 3.2).</summary>
public sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.ToTable("SalesOrders");

        // D-10: the sales order number is the key, numbered by the database from 1 in a sequence of its own. On SQLite
        // this is INTEGER PRIMARY KEY AUTOINCREMENT, so a committed number is never handed out again (decision #162).
        builder.HasKey(o => o.Number);
        builder.Property(o => o.Number).ValueGeneratedOnAdd();

        builder.Property(o => o.Status).IsStoredAsName();
        builder.Property(o => o.CustomerReference).HasMaxLength(ColumnRules.CustomerReferenceLength);
        builder.Property(o => o.ShipmentNote).HasMaxLength(ColumnRules.TextLength);
        builder.Property(o => o.CancellationReason).HasMaxLength(ColumnRules.TextLength);

        // Design 2.5: the list is newest first.
        builder.HasIndex(o => o.CreatedAt);

        // Architecture 3.2: one index serves the reports (Shipped, then a range of shipment times, D-13), open demand
        // (Created, D-12) and the list's status filter, because each of them starts with the status.
        builder.HasIndex(o => new { o.Status, o.ShippedAt });

        // The customer filter of the list and the "has sales orders" check of a customer delete (AC-29.5).
        builder.HasIndex(o => o.CustomerId);

        // AC-24.1. Restrict is the database backstop of AC-29.5 and AC-29.8: a customer that a sales order names
        // cannot be deleted, even if the delete and the new sales order race each other.
        builder.HasOne(o => o.Customer)
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-05.3: who created, shipped and cancelled the sales order.
        builder.HasOne(o => o.CreatedBy).WithMany().HasForeignKey(o => o.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.ShippedBy).WithMany().HasForeignKey(o => o.ShippedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.CancelledBy).WithMany().HasForeignKey(o => o.CancelledById).OnDelete(DeleteBehavior.Restrict);
    }
}
