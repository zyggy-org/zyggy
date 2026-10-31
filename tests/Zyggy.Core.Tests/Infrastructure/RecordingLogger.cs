using Microsoft.Extensions.Logging;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>An <see cref="ILogger{T}"/> that records every entry (level, rendered message, active scopes) for the §11 "once per state change" counts.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    // Concurrent tests (JobDispatcherTests.AdmitAsync_Concurrent_*) log and open scopes from parallel callers.
    private readonly Lock _gate = new();

    private readonly List<object?> _scopes = [];

    public List<LogEntry> Entries { get; } = [];

    public IEnumerable<LogEntry> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (_gate)
        {
            _scopes.Add(state);
        }

        return new Scope(this, state);
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        string message = formatter(state, exception);
        lock (_gate)
        {
            Entries.Add(new LogEntry(logLevel, message, exception, [.. _scopes]));
        }
    }

    private sealed class Scope(RecordingLogger<T> owner, object? state) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._scopes.Remove(state);
            }
        }
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<object?> Scopes);
