using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>Table rules for <see cref="NotificationRead"/> (architecture 3.2).</summary>
public sealed class NotificationReadConfiguration : IEntityTypeConfiguration<NotificationRead>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationRead> builder)
    {
        builder.ToTable("NotificationReads");

        // A composite key: the pair (notification, user) identifies the row, so each Manager has at most
        // one "read" record per notification.
        builder.HasKey(r => new { r.NotificationId, r.UserId });

        builder.HasOne(r => r.Notification)
            .WithMany(n => n.Reads)
            .HasForeignKey(r => r.NotificationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
