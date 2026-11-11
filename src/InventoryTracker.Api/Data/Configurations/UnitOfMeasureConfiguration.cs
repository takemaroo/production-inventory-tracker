using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Api.Data.Configurations;

/// <summary>
/// Table rules for <see cref="UnitOfMeasure"/> (architecture 3.2).
/// </summary>
/// <remarks>
/// <para>
/// An <c>IEntityTypeConfiguration&lt;T&gt;</c> holds the database rules for one entity: table name, column sizes,
/// indexes and relationships. EF Core calls <see cref="Configure"/> once, when it builds its model of the
/// database. <c>AppDbContext</c> finds every such class in this assembly automatically
/// (<c>ApplyConfigurationsFromAssembly</c>), so adding an entity never means editing the DbContext.
/// </para>
/// <para>
/// Each configuration class configures the foreign keys of its own table (the "dependent" side), so every
/// relationship is described once, next to the column that holds it.
/// </para>
/// <para>
/// A non-nullable <c>string</c> property becomes a required (NOT NULL) column by convention, because nullable
/// reference types are enabled in this project; a <c>string?</c> property becomes a nullable column.
/// </para>
/// </remarks>
public sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        builder.ToTable("Units");

        builder.Property(u => u.Name).HasMaxLength(ColumnRules.UnitNameLength);
        builder.Property(u => u.NameNormalized).HasMaxLength(ColumnRules.UnitNameLength);

        // AC-06.2: names are unique without regard to case. The database refuses a duplicate even if two
        // requests race past the service's own check.
        builder.HasIndex(u => u.NameNormalized).IsUnique();
    }
}
