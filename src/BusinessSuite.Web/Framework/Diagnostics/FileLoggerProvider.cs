using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace BusinessSuite.Web.Framework.Diagnostics;

/// <summary>
/// Writes log lines to a daily text file (<c>logs/app-yyyyMMdd.log</c> next to the application) in addition to the console,
/// so a problem such as an access denial can be inspected afterwards. Dependency-free; levels are filtered by the
/// <c>Logging:File:LogLevel</c> section of the configuration (provider alias <c>File</c>).
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            var path = Path.Combine(_directory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (_lock)
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // Logging must never break a request: a locked or full disk just drops the line.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'"))
                .Append(" [").Append(logLevel.ToString()[..3].ToUpperInvariant()).Append("] ")
                .Append(category).Append(": ").Append(formatter(state, exception));

            if (exception is not null)
            {
                text.AppendLine().Append(exception);
            }

            provider.Write(text.ToString());
        }
    }
}
