using System.Collections.Concurrent;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// Makes sign-in attempts for the <em>same</em> account run one at a time, while attempts for different accounts run
/// side by side (AC-01.4, decision #61).
/// </summary>
/// <remarks>
/// <para>
/// Why it is needed: Identity records a failed sign-in by reading the user's row, adding one to the failure count
/// and writing the row back. Each write also checks a "concurrency stamp", and a write based on an out-of-date copy
/// is refused. When 20 wrong passwords arrive at the same moment, all 20 read a count of 0, one write wins, and the
/// others are refused, so the count never reaches 5 and the account never locks. Running each account's attempts one
/// after another means every attempt reads the count the previous one wrote.
/// </para>
/// <para>
/// How it works: a <see cref="SemaphoreSlim"/> is a lock that can be awaited, so a waiting request frees its thread
/// instead of blocking it. There is one per account, found by the normalized email (upper-case, as Identity stores
/// it), in a <see cref="ConcurrentDictionary{TKey,TValue}"/>, which is safe to use from many requests at once. There
/// is deliberately no single lock for everyone: a flood of attempts on one account must not delay anyone else.
/// </para>
/// <para>
/// Memory: an entry exists only while some request holds or waits for it. Each entry counts its current users; the
/// last one to leave removes it, so unknown emails sent by an attacker leave nothing behind.
/// </para>
/// <para>
/// This works because the API runs as one process (SQLite, architecture 1). Several instances behind a load balancer
/// would need the lock in the database instead (decision #61).
/// </para>
/// </remarks>
public sealed class AccountLocks
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Diagnostic only, for tests: the number of accounts that currently have a request holding or waiting for their
    /// lock. The app itself never reads it; a test uses it to prove that no entries are left behind.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Waits until no other request is signing in to the account <paramref name="normalizedEmail"/>, then returns a
    /// handle. Disposing the handle (<c>using</c>) lets the next request for that account in.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string normalizedEmail)
    {
        var entry = Join(normalizedEmail);
        await entry.Gate.WaitAsync();
        return new Handle(this, normalizedEmail, entry);
    }

    /// <summary>Finds or creates the entry for the account and counts this request as one of its users.</summary>
    private Entry Join(string key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                // An entry whose last user has just removed it is marked closed; take a fresh one instead.
                if (!entry.Closed)
                {
                    entry.Users++;
                    return entry;
                }
            }
        }
    }

    /// <summary>Lets the next waiting request in and, when nobody else needs the entry, removes it.</summary>
    private void Leave(string key, Entry entry)
    {
        entry.Gate.Release();
        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Closed = true;
                // Removes the entry only if it is still this one, never a fresh one created since.
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Requests holding or waiting for <see cref="Gate"/>. Changed only inside <c>lock (entry)</c>.</summary>
        public int Users { get; set; }

        /// <summary>True once the entry has been removed; it must not be used again.</summary>
        public bool Closed { get; set; }
    }

    private sealed class Handle(AccountLocks owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Releasing twice would let two requests in at once, so a second Dispose does nothing.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Leave(key, entry);
            }
        }
    }
}
