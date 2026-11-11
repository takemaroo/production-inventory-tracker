using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-001 foundations that need a database or the running app: paging a real query, and the clock as the app
/// registers it.
/// </summary>
public class CommonFoundationsTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// PRD 4.1 and design 2.5: ToPagedResultAsync returns the requested page of an ordered query with the total count;
    /// a page past the end is empty but still reports the total.
    /// </summary>
    [Fact]
    public async Task ToPagedResultAsync_returns_one_page_and_the_total()
    {
        // SQLite in memory lives as long as its connection is open, which is enough for a quick query test.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Cancel);
        await using var db = new NumbersDbContext(new DbContextOptionsBuilder<NumbersDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Cancel);
        db.Numbers.AddRange(Enumerable.Range(1, 7).Select(value => new NumberRow { Id = value }));
        await db.SaveChangesAsync(Cancel);

        // Newest first: highest id first.
        var ordered = db.Numbers.OrderByDescending(row => row.Id).Select(row => row.Id);

        var second = await ordered.ToPagedResultAsync(new PageQuery { Page = 2, PageSize = 3 }, Cancel);
        var pastEnd = await ordered.ToPagedResultAsync(new PageQuery { Page = 4, PageSize = 3 }, Cancel);

        Assert.Equal([4, 3, 2], second.Items);
        Assert.Equal((2, 3, 7), (second.Page, second.PageSize, second.TotalCount));
        Assert.Empty(pastEnd.Items);
        Assert.Equal(7, pastEnd.TotalCount);
    }

    /// <summary>Architecture 6, Time: the running app registers one Clock, built on the system TimeProvider.</summary>
    [Fact]
    public async Task App_registers_a_clock_on_the_system_time()
    {
        await using var factory = new TestApiFactory();

        var clock = factory.Services.GetRequiredService<Clock>();

        Assert.Same(TimeProvider.System, factory.Services.GetRequiredService<TimeProvider>());
        Assert.Same(clock, factory.Services.GetRequiredService<Clock>()); // singleton: one shared instance
        Assert.InRange(DateTime.UtcNow - clock.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Architecture 6, Time: when a test registers its own TimeProvider, the app's Clock uses it. This is how T-003
    /// and later tasks move time forward (token expiry, lock-out, newest-first ordering).
    /// </summary>
    [Fact]
    public async Task Clock_uses_a_replacement_time_provider()
    {
        var frozen = new DateTimeOffset(2026, 11, 11, 14, 5, 7, TimeSpan.Zero).AddMilliseconds(600);
        await using var factory = new TestApiFactory();
        var withFakeTime = factory.WithWebHostBuilder(builder =>
            // ConfigureTestServices runs after Program.cs, so this registration is the last one and wins.
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(frozen))));

        var clock = withFakeTime.Services.GetRequiredService<Clock>();

        Assert.Equal(new DateTime(2026, 11, 11, 14, 5, 7, DateTimeKind.Utc), clock.UtcNow);
    }

    /// <summary>A TimeProvider frozen at one instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A minimal table for the paging query test.</summary>
    public sealed class NumberRow
    {
        /// <summary>Primary key, also the value that is paged.</summary>
        public int Id { get; set; }
    }

    /// <summary>A throw-away DbContext with one table, independent of the application's own model.</summary>
    public sealed class NumbersDbContext(DbContextOptions<NumbersDbContext> options) : DbContext(options)
    {
        /// <summary>The rows to page.</summary>
        public DbSet<NumberRow> Numbers => Set<NumberRow>();
    }
}
