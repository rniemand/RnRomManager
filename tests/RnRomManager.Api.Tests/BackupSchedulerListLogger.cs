using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Api.Tests;

internal sealed class BackupSchedulerListLogger : ILogger<BackupScheduler>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
