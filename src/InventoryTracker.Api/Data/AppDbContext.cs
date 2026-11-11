using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Data;

/// <summary>
/// The application's single EF Core database context.
/// </summary>
/// <remarks>
/// A <c>DbContext</c> is EF Core's unit of work. It tracks the objects you load or add, and
/// <c>SaveChangesAsync</c> turns those changes into SQL. One instance is created per HTTP request
/// (a "scoped" service), so requests never share tracked objects.
///
/// It inherits from <see cref="IdentityDbContext{TUser}"/>, which adds the ASP.NET Core Identity tables
/// (AspNetUsers, AspNetRoles, AspNetUserRoles and so on) for sign-in and roles (decision #8).
///
/// Task T-002 adds the domain tables (units, items, recipes, movements, orders, returns, notifications).
/// Each entity's table rules live in their own <c>IEntityTypeConfiguration</c> class in Data/Configurations
/// and are picked up automatically below, so feature tasks never need to edit this file.
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    /// <summary>Builds the database model: Identity's tables first, then every configuration class in this assembly.</summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity's own table definitions must be applied first; our configurations may refine them.
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
