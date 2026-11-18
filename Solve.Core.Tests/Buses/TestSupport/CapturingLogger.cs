using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Solve.Core.Tests.Buses.TestSupport;

/// <summary>
/// 记录日志级别与渲染后消息的 Logger 替身，用于断言“必须告警”的路径
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));
}
