using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A logger provider that keeps every log entry in memory, so a test can assert that something was logged
/// (for example that an unhandled exception reached the log with its stack trace).
/// </summary>
/// <remarks>
/// ASP.NET Core sends each log call to every registered <see cref="ILoggerProvider"/>; the console provider
/// prints it, this one stores it. Register it with <c>services.AddSingleton&lt;ILoggerProvider&gt;(provider)</c>.
/// A concurrent queue is used because requests in a test may log from several threads at once.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Everything logged so far, oldest first.</summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>One captured log call.</summary>
    /// <param name="Category">The logger's category, normally the full name of the class that logged.</param>
    /// <param name="Level">Severity, for example Error.</param>
    /// <param name="Message">The formatted message.</param>
    /// <param name="Exception">The exception passed to the log call, if any.</param>
    public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
