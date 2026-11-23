using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>(v1.1) Table rules for <see cref="SalesOrderLine"/> (architecture 3.2).</summary>
public sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SalesOrderLine> builder)
    {
        builder.ToTable("SalesOrderLines");

        builder.Property(l => l.Quantity).IsQuantity();

        // AC-24.3: an item appears at most once per sales order (decision #162). The service checks it with its own
        // message; this index makes the database refuse a second line as a backstop.
        builder.HasIndex(l => new { l.SalesOrderNumber, l.ItemId }).IsUnique();

        // Architecture 3.2: open demand (D-12), the list's item filter and the item in-use check (AC-28.5) look lines
        // up by item. (The unique index above starts with the sales order number, so it cannot serve them.)
        builder.HasIndex(l => l.ItemId);

        // Restrict, not Cascade: sales orders are never deleted (PRD section 5), so their lines must not be either.
        builder.HasOne(l => l.SalesOrder)
            .WithMany(o => o.Lines)
            .HasForeignKey(l => l.SalesOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        // AC-28.5: an item on any sales order line is in use; Restrict makes the database refuse to delete it.
        builder.HasOne(l => l.Item)
            .WithMany()
            .HasForeignKey(l => l.ItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
