using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// An EF Core command interceptor (like <see cref="AfterFirstReadInterceptor"/>) that, once armed, records every SQL
/// query the app runs, with its parameter values and its transaction, so a test can then ask SQLite how it would run
/// each one (<c>EXPLAIN QUERY PLAN</c>) or check which queries shared a transaction. Written for the sales order detail
/// fix after the T-031 review: it proves the detail and ship loads find a sales order's lines through their index
/// instead of reading every line in the database, and that the detail's queries share one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Register it through <c>TestApiFactory.ReplaceServices</c> with
/// <c>services.ConfigureDbContext&lt;AppDbContext&gt;(options =&gt; options.AddInterceptors(recorder))</c>, set up the
/// data, then call <see cref="Arm"/> just before the request under test, so the set-up's own queries are not recorded.
/// </para>
/// <para>
/// Why a query plan rather than a timing: a plan is the same on every machine and on a small test database. SQLite
/// chooses it from the shape of the SQL and the indexes, not from how many rows exist, as long as no statistics have
/// been gathered (<c>ANALYZE</c>), which neither the app nor the tests do. A timing would need a large database and
/// would still vary with the machine.
/// </para>
/// </remarks>
public sealed partial class QueryPlanRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<RecordedQuery> _queries = new();
    private int _armed;

    /// <summary>Every query recorded since <see cref="Arm"/>, in the order they ran.</summary>
    public IReadOnlyList<RecordedQuery> Queries => _queries.ToList();

    /// <summary>From now on, every query is recorded.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    /// <summary>
    /// The query plan of every recorded query that reads <c>SalesOrderLines</c>, after checking that none of them reads
    /// that whole table: no plan step may be <c>SCAN</c> of a <c>SalesOrderLines</c> alias. The T-031 review's single
    /// query had exactly that step (the lines, items and units built in full, then filtered to one sales order). Fails
    /// the test, showing the SQL and its plan, otherwise; also fails if no recorded query reads the table at all, so the
    /// check can never pass by looking at nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> AssertSalesOrderLinesNeverScannedAsync(
        TestApiFactory app, CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        foreach (var query in Queries)
        {
            var aliases = SalesOrderLinesAliases(query.Sql);
            if (aliases.Count == 0)
            {
                continue;
            }

            var plan = await ExplainAsync(app, query, cancellationToken);
            var fullScans = plan.Where(step => aliases.Any(alias =>
                step.StartsWith($"SCAN {alias}", StringComparison.Ordinal)
                && (step.Length == $"SCAN {alias}".Length || step[$"SCAN {alias}".Length] == ' '))).ToList();
            Assert.True(fullScans.Count == 0,
                $"SalesOrderLines is read in full ({string.Join("; ", fullScans)}).\nSQL:\n{query.Sql}\nPlan:\n{string.Join("\n", plan)}");
            steps.AddRange(plan);
        }

        Assert.True(steps.Count > 0, "No recorded query reads SalesOrderLines.");
        return steps;
    }

    private void Record(DbCommand command)
    {
        if (Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        // The values are copied now: EF Core may reuse the command object once it has run.
        var parameters = command.Parameters.Cast<DbParameter>()
            .Select(parameter => (parameter.ParameterName, parameter.Value))
            .ToList();
        _queries.Enqueue(new RecordedQuery(command.CommandText, parameters, command.Transaction));
    }

    /// <summary>
    /// The aliases a query gives the <c>SalesOrderLines</c> table (EF Core writes <c>"SalesOrderLines" AS "s0"</c>), or
    /// the table name itself where it has no alias. Empty when the query does not read that table.
    /// </summary>
    public static IReadOnlyList<string> SalesOrderLinesAliases(string sql) =>
        SalesOrderLinesTable().Matches(sql)
            .Select(match => match.Groups["alias"].Success ? match.Groups["alias"].Value : "SalesOrderLines")
            .Distinct()
            .ToList();

    /// <summary>
    /// Asks SQLite how it would run <paramref name="query"/> in <paramref name="app"/>'s database, and returns the plan's
    /// steps (the <c>detail</c> column), such as
    /// <c>SEARCH s0 USING INDEX IX_SalesOrderLines_SalesOrderNumber_ItemId (SalesOrderNumber=?)</c> or <c>SCAN s0</c>.
    /// EXPLAIN only plans the query; it reads no rows and changes nothing.
    /// </summary>
    /// <remarks>
    /// The connection comes from the app's own <c>AppDbContext</c> rather than a bare <c>SqliteConnection</c>, because EF
    /// Core adds its helper SQL functions (such as <c>ef_sum</c> for decimal sums) to every connection it opens, and SQLite
    /// refuses to plan a query that calls a function it does not know. The command is made directly on that connection,
    /// so it does not pass through EF Core's interceptors and is not recorded itself.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ExplainAsync(
        TestApiFactory app, RecordedQuery query, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
        foreach (var (name, value) in query.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var detail = reader.GetOrdinal("detail");
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(reader.GetString(detail));
        }

        return steps;
    }

    /// <summary><c>"SalesOrderLines"</c>, optionally followed by <c>AS "alias"</c>, as EF Core writes it.</summary>
    [GeneratedRegex("\"SalesOrderLines\"(?:\\s+AS\\s+\"(?<alias>\\w+)\")?")]
    private static partial Regex SalesOrderLinesTable();
}

/// <summary>
/// One SQL query the app ran, as <see cref="QueryPlanRecorder"/> recorded it: its text, its parameter values, and the
/// database transaction it ran in (<c>null</c> when none was open), so a test can check that several queries shared one.
/// </summary>
public sealed record RecordedQuery(
    string Sql, IReadOnlyList<(string Name, object? Value)> Parameters, DbTransaction? Transaction);
