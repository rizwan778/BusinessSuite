using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>One log line written by the application during a test.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message);

/// <summary>
/// Collects the application's log lines in memory (registered by <see cref="BusinessSuiteWebApplicationFactory"/>), so a
/// test can check what an operation logged: its level, and that it names ids but never names or e-mail addresses.
/// </summary>
public sealed class TestLogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Everything logged so far, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    internal void Add(LogEntry entry) => _entries.Enqueue(entry);
}

/// <summary>Feeds every <see cref="ILogger"/> of the application into a <see cref="TestLogSink"/>.</summary>
internal sealed class TestLogSinkProvider(TestLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class SinkLogger(string category, TestLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            sink.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
