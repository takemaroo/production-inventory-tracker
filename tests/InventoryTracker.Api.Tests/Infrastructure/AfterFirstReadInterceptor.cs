using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// An EF Core command interceptor (like <see cref="BeforeSaveInterceptor"/>, but called around each SQL command): once
/// armed, it runs <c>work</c> the first time a command that <c>matches</c> has started returning rows, before the next
/// query of the same request runs. Tests use it to commit another request's write in the gap between two queries, to
/// prove a read takes all its figures from one snapshot (decision #85).
/// </summary>
/// <remarks>
/// Register it through <c>TestApiFactory.ReplaceServices</c> with
/// <c>services.ConfigureDbContext&lt;AppDbContext&gt;(options =&gt; options.AddInterceptors(interceptor))</c>, set up the
/// data, then call <see cref="Arm"/> just before the request under test, so the set-up's own queries do not trigger it.
/// First written for the T-008 stock tests; shared since T-009's order-estimate test uses it too.
/// </remarks>
public sealed class AfterFirstReadInterceptor(Func<DbCommand, bool> matches, Func<Task> work) : DbCommandInterceptor
{
    private int _armed;
    private int _hasRun;

    /// <summary>True once <c>work</c> has run.</summary>
    public bool HasRun => Volatile.Read(ref _hasRun) == 1;

    /// <summary>From now on, the next matching command triggers <c>work</c> (once).</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <inheritdoc />
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        // The flag is set before work runs, so work's own commands (which pass through here too) do not re-enter.
        if (Volatile.Read(ref _armed) == 1 && matches(command) && Interlocked.Exchange(ref _hasRun, 1) == 0)
        {
            await work();
        }

        return result;
    }
}
