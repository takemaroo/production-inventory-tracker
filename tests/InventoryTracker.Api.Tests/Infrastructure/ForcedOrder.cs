using InventoryTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A factory of its own whose DbContexts carry an <see cref="AfterFirstReadInterceptor"/>, and <see cref="RunAsync"/>,
/// which makes one request run strictly before another that arrives while it is in progress. First written for the T-016
/// cancellation races; shared since T-017's complementary order races use it too (decision #101: one copy).
/// </summary>
/// <remarks>
/// <para>
/// <b>How the order is forced.</b> Every request that changes data begins with <c>InWriteTransactionAsync</c>, which on
/// SQLite takes the database's single write lock (<c>BEGIN IMMEDIATE</c>, architecture 5.2), and then reads the record
/// named in its URL. The interceptor fires on the first read inside a transaction that mentions the table given to the
/// constructor (<c>ProductionOrders</c> for send, complete, cancel and the complementary request;
/// <c>ComplementaryOrders</c> for approve and reject), so at that moment the first request holds the lock. It then starts
/// the second request and waits 1.5 seconds. The second request cannot begin its own write transaction until the first
/// commits, so it is still unfinished when the wait ends; that is checked, which proves it really queued behind the lock
/// rather than running first or alongside. When the first request commits, the second continues with the committed data.
/// </para>
/// <para>
/// The interceptor is created before the factory, because the factory's services are configured with it, but the requests
/// it starts exist only once the test has set up its users. So the interceptor calls a field,
/// <c>_whileFirstHoldsTheLock</c>, that <see cref="RunAsync"/> fills in just before arming it.
/// </para>
/// </remarks>
public sealed class ForcedOrder : IAsyncDisposable
{
    private readonly string _table;
    private Func<Task> _whileFirstHoldsTheLock = () => Task.CompletedTask;

    /// <summary>Creates the factory with the interceptor on every DbContext it builds.</summary>
    /// <param name="table">The table whose first read inside a transaction marks "the first request holds the lock".</param>
    public ForcedOrder(string table = "ProductionOrders")
    {
        _table = table;
        Interceptor = new AfterFirstReadInterceptor(
            command => command.Transaction is not null
                && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal),
            () => _whileFirstHoldsTheLock());
        App = new TestApiFactory
        {
            ReplaceServices = services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Interceptor)),
        };
    }

    /// <summary>The app under test.</summary>
    public TestApiFactory App { get; }

    /// <summary>The interceptor that opens the gap; armed only by <see cref="RunAsync"/>, so set-up never triggers it.</summary>
    public AfterFirstReadInterceptor Interceptor { get; }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs <paramref name="first"/>; once it holds the write lock, starts <paramref name="second"/>, and checks that the
    /// second was still waiting 1.5 seconds later. Returns both responses.
    /// </summary>
    public async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RunAsync(
        Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        Task<HttpResponseMessage>? secondTask = null;
        var secondWasWaiting = false;
        _whileFirstHoldsTheLock = async () =>
        {
            secondTask = second();
            await Task.Delay(TimeSpan.FromSeconds(1.5), Cancel);
            secondWasWaiting = !secondTask.IsCompleted;
        };

        Interceptor.Arm();
        var firstResponse = await first();
        Assert.True(Interceptor.HasRun, $"The first request never read {_table} inside a transaction.");
        var secondResponse = await secondTask!;
        Assert.True(secondWasWaiting, "The second request finished while the first still held the write lock.");
        return (firstResponse, secondResponse);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => App.DisposeAsync();
}
