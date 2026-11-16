using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Tests;

/// <summary>Keeps every line the service logs, so a test can read what an operator would.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Writer(Lines);

    public void Dispose()
    {
    }

    private sealed class Writer(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}
