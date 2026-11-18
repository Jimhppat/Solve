using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 运行日志页的一行。不可变：显示用的文本在创建时算好，滚动时不再重复格式化。
/// </summary>
public sealed class LogEntryItem
{
    public LogEntryItem(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;

        var local = entry.Timestamp.ToLocalTime();
        TimeText = local.ToString("HH:mm:ss.fff");
        ShortTimeText = local.ToString("HH:mm:ss");
        LevelText = LevelName(entry.Level);
        SourceText = ShortSource(entry.Source);
        DetailText = BuildDetail(entry, local, LevelText);
    }

    public LogEntry Entry { get; }

    public LogLevel Level => Entry.Level;

    public string Message => Entry.Message;

    /// <summary>
    /// 来源的完整名称（悬停提示中显示）
    /// </summary>
    public string Source => Entry.Source;

    public string TimeText { get; }

    /// <summary>
    /// 精确到秒，用于主控台右栏等窄区域
    /// </summary>
    public string ShortTimeText { get; }

    public string LevelText { get; }

    /// <summary>
    /// 来源的类名部分，例如 Solve.Core.Application.Buses.BusRuntime 显示为 BusRuntime
    /// </summary>
    public string SourceText { get; }

    public bool HasException => Entry.Exception is not null;

    public bool IsDebug => Entry.Level <= LogLevel.Debug;

    public bool IsWarning => Entry.Level == LogLevel.Warning;

    public bool IsError => Entry.Level >= LogLevel.Error;

    /// <summary>
    /// 完整内容：日期时间、级别、完整来源、消息与异常堆栈。用于悬停提示和复制。
    /// </summary>
    public string DetailText { get; }

    public bool Matches(LogLevel minimum, string search)
    {
        if (Entry.Level < minimum)
            return false;

        if (string.IsNullOrWhiteSpace(search))
            return true;

        var keyword = search.Trim();
        return Entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Entry.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "跟踪",
        LogLevel.Debug => "调试",
        LogLevel.Information => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        LogLevel.Critical => "致命",
        _ => level.ToString(),
    };

    private static string ShortSource(string source)
    {
        var dot = source.LastIndexOf('.');
        return dot >= 0 && dot < source.Length - 1 ? source[(dot + 1)..] : source;
    }

    private static string BuildDetail(LogEntry entry, DateTimeOffset local, string levelText)
    {
        var header = $"{local:yyyy-MM-dd HH:mm:ss.fff} [{levelText}] {entry.Source}";
        return entry.Exception is null
            ? $"{header}{Environment.NewLine}{entry.Message}"
            : $"{header}{Environment.NewLine}{entry.Message}{Environment.NewLine}{entry.Exception}";
    }
}
