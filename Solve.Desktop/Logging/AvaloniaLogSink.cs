using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Logging;
using Serilog.Core;
using AvaloniaLevel = Avalonia.Logging.LogEventLevel;
using SerilogLevel = Serilog.Events.LogEventLevel;

namespace Solve.Desktop.Logging;

/// <summary>
/// 把 Avalonia 框架日志（绑定错误、布局异常、平台层告警等）转入黑匣子。
/// </summary>
/// <remarks>
/// <para>Avalonia 12 没有维护中的 Serilog 桥接包，这里只做转发：Avalonia 的消息模板与 Serilog 同语法，直接交给 Serilog 解析。</para>
/// <para>来源上下文为 "Avalonia.{区域}"（如 Avalonia.Binding），级别由配置文件的 Override:Avalonia 控制，默认 Warning。</para>
/// <para><see cref="IsEnabled"/> 在框架热路径上被频繁调用：按区域缓存子日志器，判断时不分配内存。</para>
/// </remarks>
public sealed class AvaloniaLogSink(Serilog.ILogger logger) : ILogSink
{
    public const string SourceContextPrefix = "Avalonia";

    private readonly ConcurrentDictionary<string, Serilog.ILogger> _areas = new();

    public bool IsEnabled(AvaloniaLevel level, string area) => ForArea(area).IsEnabled(Map(level));

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate)
        => Write(level, area, source, messageTemplate, []);

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        => Write(level, area, source, messageTemplate, propertyValues);

    private void Write(AvaloniaLevel level, string area, object? source, string messageTemplate, object?[] propertyValues)
    {
        var target = ForArea(area);
        var serilogLevel = Map(level);
        if (!target.IsEnabled(serilogLevel))
            return;

        if (source is null)
        {
            target.Write(serilogLevel, messageTemplate, propertyValues);
            return;
        }

        // 前置来源控件类型，便于定位是哪个控件出错；放在模板最前面，不影响原模板属性的按位绑定
        target.Write(serilogLevel, "[{AvaloniaSource}] " + messageTemplate, [source.GetType().Name, .. propertyValues]);
    }

    private Serilog.ILogger ForArea(string area)
        => _areas.GetOrAdd(
            area,
            static (key, root) => root.ForContext(Constants.SourceContextPropertyName, $"{SourceContextPrefix}.{key}"),
            logger);

    private static SerilogLevel Map(AvaloniaLevel level) => level switch
    {
        AvaloniaLevel.Verbose => SerilogLevel.Verbose,
        AvaloniaLevel.Debug => SerilogLevel.Debug,
        AvaloniaLevel.Information => SerilogLevel.Information,
        AvaloniaLevel.Warning => SerilogLevel.Warning,
        AvaloniaLevel.Error => SerilogLevel.Error,
        _ => SerilogLevel.Fatal,
    };
}

public static class AvaloniaLoggingExtensions
{
    /// <summary>
    /// 用 Serilog 替代 LogToTrace 接收 Avalonia 框架日志
    /// </summary>
    public static AppBuilder LogToSerilog(this AppBuilder builder, Serilog.ILogger logger)
    {
        Avalonia.Logging.Logger.Sink = new AvaloniaLogSink(logger);
        return builder;
    }
}
