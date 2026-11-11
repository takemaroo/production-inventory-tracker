using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="Notification"/> (architecture 3.2).</summary>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.Property(n => n.Type).IsStoredAsName();
        builder.Property(n => n.Message).HasMaxLength(ColumnRules.MessageLength);
        // DataJson has no length limit: it is a TEXT column holding the design 5.10 "data" object.

        builder.HasIndex(n => n.CreatedAt);

        builder.HasOne(n => n.ProductionOrder)
            .WithMany()
            .HasForeignKey(n => n.ProductionOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(n => n.ComplementaryOrder)
            .WithMany()
            .HasForeignKey(n => n.ComplementaryOrderNumber)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
