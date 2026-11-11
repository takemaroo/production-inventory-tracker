namespace InventoryTracker.Api.Common.Time;

/// <summary>
/// The application's single source of "now". Services ask for a <see cref="Clock"/> in their constructor
/// instead of calling <c>DateTime.UtcNow</c> (architecture 6, Time).
/// </summary>
/// <remarks>
/// <see cref="TimeProvider"/> is .NET's built-in abstraction of the system clock. Production uses
/// <c>TimeProvider.System</c>, registered in Program.cs; tests register a fake one that they can move forward
/// on demand, which is how token expiry (AC-01.2), lock-out (AC-01.4) and newest-first ordering are tested
/// without waiting for real time to pass.
/// <para>
/// Times are UTC and truncated to whole seconds (design 2.3, architecture 4.1 item 9), so a value stored in
/// the database, returned as JSON (<c>2026-11-11T14:05:00Z</c>) and compared in a test is always the same.
/// </para>
/// </remarks>
public sealed class Clock(TimeProvider timeProvider)
{
    /// <summary>The current UTC time, truncated to the whole second, with <see cref="DateTimeKind.Utc"/>.</summary>
    public DateTime UtcNow
    {
        get
        {
            var ticks = timeProvider.GetUtcNow().UtcTicks;

            // Drop everything below one second (a tick is 100 nanoseconds). Truncating, not rounding, so the
            // clock never reports a second that has not started yet.
            return new DateTime(ticks - (ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Today's date in UTC, for rules about whole days such as "Receipt date cannot be later than today (UTC)."
    /// (US-09).
    /// </summary>
    public DateOnly UtcToday => DateOnly.FromDateTime(UtcNow);
}
