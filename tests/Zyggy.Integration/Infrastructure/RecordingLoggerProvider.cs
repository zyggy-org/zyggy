using Microsoft.Extensions.Logging;

namespace Zyggy.Integration.Infrastructure;

/// <summary>An <see cref="ILoggerProvider"/> that records every entry of every category (level, category, rendered message) for the §11 counts and the secret sweep.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object>? Scope)> Entries { get; } = [];

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public IEnumerable<(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object>? Scope)> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerProvider owner, string category) : ILogger
    {
        private readonly AsyncLocal<IReadOnlyDictionary<string, object>?> _scope = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            var previous = _scope.Value;
            _scope.Value = state as IReadOnlyDictionary<string, object>;
            return new Scope(() => _scope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((logLevel, category, formatter(state, exception), _scope.Value));
            }
        }
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
