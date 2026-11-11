using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Data;

/// <summary>
/// The application's single EF Core database context.
/// </summary>
/// <remarks>
/// <para>
/// A <c>DbContext</c> is EF Core's unit of work. It tracks the objects you load or add, and
/// <c>SaveChangesAsync</c> turns those changes into SQL. One instance is created per HTTP request
/// (a "scoped" service), so requests never share tracked objects.
/// </para>
/// <para>
/// It inherits from <see cref="IdentityDbContext{TUser}"/>, which adds the ASP.NET Core Identity tables
/// (AspNetUsers, AspNetRoles, AspNetUserRoles and so on) for sign-in and roles (decision #8).
/// </para>
/// <para>
/// Each entity's table rules live in their own <c>IEntityTypeConfiguration</c> class in Data/Configurations
/// and are picked up automatically below, so feature tasks never need to edit this file.
/// Every request that changes data goes through <see cref="WriteTransaction.InWriteTransactionAsync{T}"/>
/// (architecture 5.2).
/// </para>
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    // A DbSet<T> is a queryable view of one table: db.Items.Where(...) becomes a SELECT on Items, and
    // db.Items.Add(item) makes the next SaveChangesAsync insert a row. "=> Set<T>()" asks EF Core for it.

    /// <summary>Units of measure (US-06).</summary>
    public DbSet<UnitOfMeasure> Units => Set<UnitOfMeasure>();

    /// <summary>Raw materials and finished products (US-07).</summary>
    public DbSet<Item> Items => Set<Item>();

    /// <summary>Recipes, one per finished product (US-08).</summary>
    public DbSet<Recipe> Recipes => Set<Recipe>();

    /// <summary>Recipe lines.</summary>
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();

    /// <summary>Stock movements (D-2). Insert-only (AC-11.3); add them through <c>StockLedger</c> (T-008).</summary>
    public DbSet<Movement> Movements => Set<Movement>();

    /// <summary>Production orders (US-13).</summary>
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();

    /// <summary>Production order lines.</summary>
    public DbSet<ProductionOrderLine> ProductionOrderLines => Set<ProductionOrderLine>();

    /// <summary>Complementary orders (US-16).</summary>
    public DbSet<ComplementaryOrder> ComplementaryOrders => Set<ComplementaryOrder>();

    /// <summary>Complementary order lines.</summary>
    public DbSet<ComplementaryOrderLine> ComplementaryOrderLines => Set<ComplementaryOrderLine>();

    /// <summary>Leftover returns (US-22).</summary>
    public DbSet<LeftoverReturn> Returns => Set<LeftoverReturn>();

    /// <summary>Notifications for Managers (US-23).</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Which Manager has read which notification.</summary>
    public DbSet<NotificationRead> NotificationReads => Set<NotificationRead>();

    /// <summary>Builds the database model: Identity's tables first, then every configuration class in this assembly.</summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity's own table definitions must be applied first; our configurations may refine them.
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    /// <summary>
    /// Model-wide rules that apply to every property of a type, before the per-entity configurations run.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Architecture 3.1, decision #27: SQLite forgets DateTimeKind, so every DateTime (including the
        // nullable ones and Identity's AspNetUsers.CreatedAt) is read back marked as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <summary>Saves tracked changes, after refusing any change to an existing movement (AC-11.3).</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseMovementEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Saves tracked changes, after refusing any change to an existing movement (AC-11.3).</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseMovementEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// AC-11.3: movements are history, so they are only ever inserted. The API has no endpoint that edits or
    /// deletes one; this check also stops a programming mistake from doing it through EF Core.
    /// </summary>
    /// <remarks>
    /// The other SaveChanges overloads call the two overridden above, so every save passes through here.
    /// Bulk statements (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>) bypass change tracking; they are not used on movements.
    /// </remarks>
    private void RefuseMovementEdits()
    {
        var edited = ChangeTracker.Entries<Movement>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (edited)
        {
            throw new InvalidOperationException("Movements are insert-only (AC-11.3); an existing movement cannot be changed or deleted.");
        }
    }
}
