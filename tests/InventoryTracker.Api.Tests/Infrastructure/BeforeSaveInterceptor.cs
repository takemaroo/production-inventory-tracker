using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// An EF Core <em>interceptor</em>: an object EF Core calls at fixed points of its work, here just before
/// <c>SaveChanges</c> sends its statements. It runs <c>work</c> once, on the request's own DbContext and so inside
/// its open write transaction, the first time <c>shouldRun</c> is true. Tests use it to change the database between
/// a service's checks and its save, which is the only way to reach the unique-index fallback (decision #46).
/// </summary>
/// <remarks>
/// Register it through <c>TestApiFactory.ReplaceServices</c> with
/// <c>services.ConfigureDbContext&lt;AppDbContext&gt;(options =&gt; options.AddInterceptors(interceptor))</c>.
/// <c>shouldRun</c> is needed because the same DbContext type also saves users and sign-ins while a test sets up.
/// First used by the T-006 units tests; meant for the same kind of test in T-010 and T-020.
/// </remarks>
public sealed class BeforeSaveInterceptor(
    Func<DbContext, bool> shouldRun, Func<DbContext, CancellationToken, Task> work) : SaveChangesInterceptor
{
    private int _hasRun;

    /// <summary>True once <c>work</c> has run.</summary>
    public bool HasRun => Volatile.Read(ref _hasRun) == 1;

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && shouldRun(context) && Interlocked.Exchange(ref _hasRun, 1) == 0)
        {
            // The interceptor exists to act inside the request's transaction; fail loudly if there is none.
            if (context.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("BeforeSaveInterceptor expected an open write transaction.");
            }

            await work(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
