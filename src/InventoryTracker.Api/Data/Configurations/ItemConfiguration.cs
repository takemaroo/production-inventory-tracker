using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Item"/> (architecture 3.2).</summary>
public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("Items");

        builder.Property(i => i.Name).HasMaxLength(ColumnRules.ItemNameLength);
        builder.Property(i => i.NameNormalized).HasMaxLength(ColumnRules.ItemNameLength);
        builder.Property(i => i.Description).HasMaxLength(ColumnRules.TextLength);
        builder.Property(i => i.Type).IsStoredAsName();

        // AC-07.1: item names are unique without regard to case.
        builder.HasIndex(i => i.NameNormalized).IsUnique();

        // AC-06.4: Restrict means the database refuses to delete a unit that an item still uses.
        // (EF Core's default for a required relationship is Cascade, which would delete the items too.)
        builder.HasOne(i => i.Unit)
            .WithMany()
            .HasForeignKey(i => i.UnitId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
