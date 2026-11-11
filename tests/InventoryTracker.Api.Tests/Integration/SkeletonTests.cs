using System.Net;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// Proves the project skeleton works end to end: the app starts, creates and migrates its database,
/// answers the health check, returns ProblemDetails for unknown routes and reads the tolerance setting.
/// </summary>
/// <remarks>
/// <c>IClassFixture</c> makes xUnit create one <see cref="TestApiFactory"/> for all tests in this class
/// and pass it to the constructor.
/// </remarks>
public class SkeletonTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>Skeleton done-criterion: GET /health answers 200 "Healthy" once the database is reachable.</summary>
    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Skeleton done-criterion: start-up creates the SQLite file and applies every migration.</summary>
    [Fact]
    public async Task Startup_creates_and_migrates_the_database()
    {
        factory.CreateClient(); // starts the app, which runs the migrations

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(File.Exists(factory.DatabasePath));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Contains(db.Database.GetAppliedMigrations(), name => name.EndsWith("_InitialIdentity"));
    }

    /// <summary>
    /// Design section 6: errors, including unknown routes, are ProblemDetails bodies. Signed in, because without a
    /// token every path gives 401 first (design 2.2, T-003).
    /// </summary>
    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        var client = await factory.CreateClientAsAsync(Roles.Warehouse);

        var response = await client.GetAsync("/api/v1/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>D-1 and PRD section 5: the tolerance comes from appsettings.json, default 2%.</summary>
    [Fact]
    public void Tolerance_setting_defaults_to_two_percent()
    {
        var options = factory.Services.GetRequiredService<IOptions<InventoryOptions>>().Value;

        Assert.Equal(2m, options.TolerancePercent);
    }
}
