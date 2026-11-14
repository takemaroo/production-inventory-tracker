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
        // Decision #63, architecture 3.2: neither Message nor DataJson has a maximum length. Both are composed by the
        // system from other fields (every material of an order, names of up to 100 characters, a reason of up to 500),
        // so no fixed limit is safe, and truncating would drop facts. A string property without HasMaxLength is mapped
        // to an unlimited text column: TEXT on SQLite, text on PostgreSQL. The migration NotificationMessageUnbounded
        // (T-011) removed the 1,000-character limit the DomainModel migration had given Message.
        builder.Property(n => n.Message).IsRequired();
        builder.Property(n => n.DataJson).IsRequired();

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
