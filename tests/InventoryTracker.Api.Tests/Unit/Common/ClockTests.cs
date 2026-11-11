using InventoryTracker.Api.Common.Time;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>Design 2.3 and architecture 6, Time: the application's clock.</summary>
public class ClockTests
{
    /// <summary>T-001 AC 7: UtcNow is UTC and truncated to whole seconds (not rounded up).</summary>
    [Fact]
    public void UtcNow_is_utc_with_whole_seconds()
    {
        // 14:05:00.9999999 in UTC+02:00, which is 12:05:00.9999999 UTC.
        var instant = new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeSpan.FromHours(2)).AddTicks(9_999_999);
        var clock = new Clock(new FixedTimeProvider(instant));

        var now = clock.UtcNow;

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.Equal(new DateTime(2026, 11, 11, 12, 5, 0, DateTimeKind.Utc), now);
        Assert.Equal(0, now.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>US-09: "today" is the UTC date, even when the local date is already the next day.</summary>
    [Fact]
    public void UtcToday_is_the_utc_date()
    {
        // 00:30 on 12 November in UTC+02:00 is still 11 November in UTC.
        var clock = new Clock(new FixedTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 30, 0, TimeSpan.FromHours(2))));

        Assert.Equal(new DateOnly(2026, 11, 11), clock.UtcToday);
    }

    /// <summary>
    /// Architecture 6, Time: a Clock built on .NET's system TimeProvider reports the real time. (That the running
    /// app registers exactly this, and uses a replacement when a test registers one, is tested in
    /// <c>Integration/CommonFoundationsTests</c>.)
    /// </summary>
    [Fact]
    public void Clock_on_the_system_time_provider_is_close_to_real_time()
    {
        var clock = new Clock(TimeProvider.System);

        var difference = DateTime.UtcNow - clock.UtcNow;

        Assert.InRange(difference, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>A TimeProvider frozen at one instant, enough for these tests (T-003 adds FakeTimeProvider for moving time).</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
