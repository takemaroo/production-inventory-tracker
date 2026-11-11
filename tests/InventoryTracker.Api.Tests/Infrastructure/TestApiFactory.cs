using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Starts the real API in memory for integration tests, with its own empty SQLite database file.
/// </summary>
/// <remarks>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> runs Program.cs exactly as production does, but serves
/// requests through an in-memory test server instead of a network port. <c>CreateClient()</c> returns an
/// <see cref="HttpClient"/> wired to that server.
///
/// Each factory gets a fresh database file in the temp folder. A real file is used instead of SQLite's
/// in-memory mode because the concurrency tests (PRD section 5) need several connections and real
/// file locking, just like production. Tests in one class share a factory through xUnit's
/// <c>IClassFixture&lt;TestApiFactory&gt;</c>; a test that needs a clean database creates its own factory.
///
/// The "Testing" environment means user-secrets are not loaded, so a developer's local secrets
/// never leak into test runs. T-003 extends this class with the JWT key, ADMIN_EMAIL / ADMIN_PASSWORD and
/// helpers to create users and get tokens.
/// </remarks>
public class TestApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Full path of this factory's SQLite database file.</summary>
    public string DatabasePath { get; } =
        Path.Combine(Path.GetTempPath(), $"inventory-tests-{Guid.NewGuid():N}.db");

    /// <summary>Points the app at the temporary database and selects the Testing environment.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
    }

    /// <summary>Stops the app and deletes its database files.</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // SQLite keeps pooled connections open; release them so the files can be deleted.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(DatabasePath + suffix);
        }

        GC.SuppressFinalize(this);
    }
}
