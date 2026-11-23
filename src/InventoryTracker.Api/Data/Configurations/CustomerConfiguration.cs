using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>(v1.1) Table rules for <see cref="Customer"/> (architecture 3.2).</summary>
public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");

        // The integer key needs no setting: EF Core's SQLite provider makes it INTEGER PRIMARY KEY AUTOINCREMENT, so a
        // committed id, even a deleted customer's, is never given to a new customer (AC-29.5, decision #162).

        builder.Property(c => c.Name).HasMaxLength(ColumnRules.CustomerNameLength);
        builder.Property(c => c.NameNormalized).HasMaxLength(ColumnRules.CustomerNameLength);
        builder.Property(c => c.Notes).HasMaxLength(ColumnRules.TextLength);

        // AC-29.2: customer names are unique without regard to case. The database refuses a duplicate even if two
        // requests race past the service's own check.
        builder.HasIndex(c => c.NameNormalized).IsUnique();

        // AC-29.6: the list is newest first.
        builder.HasIndex(c => c.CreatedAt);

        // AC-05.3: who created the customer.
        builder.HasOne(c => c.CreatedBy).WithMany().HasForeignKey(c => c.CreatedById).OnDelete(DeleteBehavior.Restrict);
    }
}
