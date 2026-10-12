using Microsoft.Extensions.Logging;

namespace Zyggy.Integration.Infrastructure;

/// <summary>An <see cref="ILoggerProvider"/> that records every entry of every category (level, category, rendered message) for the §11 counts and the secret sweep.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Entries { get; } = [];

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public IEnumerable<(LogLevel Level, string Category, string Message)> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((logLevel, category, formatter(state, exception)));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
