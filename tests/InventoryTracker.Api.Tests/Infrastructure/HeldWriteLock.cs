using Microsoft.Data.Sqlite;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// SQLite's single write lock, taken from a connection of the test's own and held until released. While it is held,
/// any request that begins a write transaction waits (architecture 5.2); reads carry on, because the database is in WAL
/// mode. Used to prove that a request is refused <em>before</em> its write transaction (SEC-01, decision #125): such a
/// request is still answered while the lock is held, so it can never make another writer wait.
/// </summary>
/// <remarks>
/// Shared by <c>RequestLinesLimitTests</c> and <c>RequestRolesLimitTests</c> (decision #101: one copy of a test helper).
/// It runs <c>BEGIN IMMEDIATE</c>, exactly what <c>InWriteTransactionAsync</c> runs, and later <c>ROLLBACK</c>, so it
/// never changes the data.
/// </remarks>
public sealed class HeldWriteLock : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private bool _released;

    private HeldWriteLock(SqliteConnection connection) => _connection = connection;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Opens a connection to <paramref name="databasePath"/> (a <see cref="TestApiFactory.DatabasePath"/>) and runs
    /// <c>BEGIN IMMEDIATE</c> on it. The app must have started first, so its database exists.
    /// </summary>
    public static async Task<HeldWriteLock> TakeAsync(string databasePath)
    {
        // Pooling=False: this connection is closed for real afterwards, and never handed to the app by the pool.
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        try
        {
            await connection.OpenAsync(Cancel);
            await using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(Cancel);
            return new HeldWriteLock(connection);
        }
        catch
        {
            // No lock was taken (for example the database was busy past the timeout), so nobody will release this
            // connection; close it here rather than leave the file open.
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Ends the transaction (nothing was written) and closes the connection, letting waiting writers in.</summary>
    public async Task ReleaseAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        await using (var rollback = _connection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(Cancel);
        }

        await _connection.DisposeAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
