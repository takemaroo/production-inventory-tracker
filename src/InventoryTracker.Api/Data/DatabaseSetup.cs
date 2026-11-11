using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Data;

/// <summary>
/// Registers the database with dependency injection and brings it up to date at start-up.
/// Kept out of Program.cs so the database rules are in one place.
/// </summary>
public static class DatabaseSetup
{
    /// <summary>
    /// Registers <see cref="AppDbContext"/> using the SQLite connection string <c>ConnectionStrings:Default</c>.
    /// </summary>
    /// <remarks>
    /// A relative <c>Data Source</c> (the default is <c>inventory.db</c>) is resolved against the project's
    /// content root, so the database file ends up in the same place however the app is started.
    /// </remarks>
    public static IServiceCollection AddInventoryDatabase(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Start-up stopped: ConnectionStrings:Default is not set.");

        var connection = new SqliteConnectionStringBuilder(raw);
        if (!Path.IsPathRooted(connection.DataSource) && connection.DataSource != ":memory:")
        {
            connection.DataSource = Path.Combine(environment.ContentRootPath, connection.DataSource);
        }

        // AddDbContext registers AppDbContext as a scoped service: one instance per HTTP request.
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection.ToString()));
        return services;
    }

    /// <summary>
    /// Applies any pending EF Core migrations, creating the database file if it does not exist yet.
    /// </summary>
    /// <remarks>
    /// A migration is a generated C# class (in Data/Migrations) that describes one change to the schema.
    /// <c>MigrateAsync</c> runs the ones this database has not seen yet, in order, and records them in the
    /// <c>__EFMigrationsHistory</c> table. Applying them at start-up suits a single-machine SQLite app,
    /// because the owner never has to run a separate database step.
    /// </remarks>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        // DbContext is scoped and there is no HTTP request at start-up, so we create a scope by hand.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // `await` frees the thread while the database works instead of blocking it (async/await).
        await db.Database.MigrateAsync();

        // Write-ahead logging (WAL) lets read-only requests keep reading while another request writes.
        // The setting is stored in the database file, so running it on every start is harmless.
        // It is SQLite-specific; a PostgreSQL provider (PRD "Later") would skip this.
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
    }
}
