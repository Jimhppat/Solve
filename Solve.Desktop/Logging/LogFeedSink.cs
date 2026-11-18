using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 把 Serilog 日志事件转成 <see cref="LogEntry"/> 推给界面日志面板，并缓存最近若干条，供晚于启动打开的面板补看。
/// </summary>
/// <remarks>
/// <para>挂在日志管线的异步队列之后，在后台写盘线程上执行，不占用产生日志的业务线程；
/// 推送用 <see cref="EventBroadcaster{T}"/>，订阅者慢时只丢弃它自己最旧的条目。</para>
/// <para>不推送 Avalonia 框架日志：面板本身由 Avalonia 渲染，渲染出错产生的日志若再推回面板，可能形成反馈循环。
/// 这些日志仍完整写入日志文件。</para>
/// </remarks>
public sealed class LogFeedSink : ILogEventSink, ILogFeed
{
    /// <summary>
    /// 缓存的最近条数：覆盖启动阶段的全部日志，面板打开后能看到程序是怎么启动的
    /// </summary>
    public const int DefaultRecentCapacity = 500;

    /// <summary>
    /// 每个订阅者的待处理队列容量；界面按批取走，正常情况下远达不到
    /// </summary>
    public const int DefaultSubscriberCapacity = 5_000;

    // 与日志文件一致：字符串属性不加引号
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:l}");

    private readonly int _recentCapacity;
    private readonly Queue<LogEntry> _recent;
    private readonly EventBroadcaster<LogEntry> _broadcaster;
    private readonly Lock _gate = new();

    public LogFeedSink(int recentCapacity = DefaultRecentCapacity, int subscriberCapacity = DefaultSubscriberCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recentCapacity, 1);
        _recentCapacity = recentCapacity;
        _recent = new Queue<LogEntry>(recentCapacity);
        _broadcaster = new EventBroadcaster<LogEntry>(subscriberCapacity);
    }

    public void Emit(LogEvent logEvent)
    {
        var source = SourceOf(logEvent);
        if (IsAvalonia(source))
            return;

        var entry = new LogEntry(logEvent.Timestamp, Map(logEvent.Level), source, Render(logEvent), logEvent.Exception?.ToString());

        // 缓存与推送在同一把锁内：与 Subscribe 的"取快照 + 订阅"互斥，保证快照与后续推送首尾衔接
        lock (_gate)
        {
            if (_recent.Count == _recentCapacity)
                _recent.Dequeue();
            _recent.Enqueue(entry);
            _broadcaster.Publish(entry);
        }
    }

    public EventSubscription<LogEntry> Subscribe(out IReadOnlyList<LogEntry> recent)
    {
        lock (_gate)
        {
            recent = _recent.ToArray();
            return _broadcaster.Subscribe();
        }
    }

    /// <summary>
    /// 日志管线关闭后调用：结束全部订阅，面板的读取循环随之退出。幂等。
    /// </summary>
    public void Complete() => _broadcaster.Complete();

    private static string SourceOf(LogEvent logEvent)
        => logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
           && value is ScalarValue { Value: string source }
            ? source
            : string.Empty;

    private static bool IsAvalonia(string source)
        => source.StartsWith(AvaloniaLogSink.SourceContextPrefix, StringComparison.Ordinal)
           && (source.Length == AvaloniaLogSink.SourceContextPrefix.Length
               || source[AvaloniaLogSink.SourceContextPrefix.Length] == '.');

    private static string Render(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        MessageFormatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogLevel Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };
}
