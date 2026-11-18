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
        Category = LogCategories.FromLevel(entry.Level);
        LevelText = Category.DisplayName();
        SourceText = ShortSource(entry.Source);
        DetailText = BuildDetail(entry, local, LevelText);
    }

    public LogEntry Entry { get; }

    public LogLevel Level => Entry.Level;

    /// <summary>
    /// 界面上的五类级别之一，筛选按它进行
    /// </summary>
    public LogCategory Category { get; }

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

    public bool IsDebug => Category == LogCategory.Debug;

    public bool IsWarning => Category == LogCategory.Warning;

    /// <summary>
    /// 错误或致命：统计「错误」条数与危险色标签都按此判断
    /// </summary>
    public bool IsError => Category >= LogCategory.Error;

    /// <summary>
    /// 致命：在危险色基础上用实心标签，与普通错误区分
    /// </summary>
    public bool IsCritical => Category == LogCategory.Critical;

    /// <summary>
    /// 完整内容：日期时间、级别、完整来源、消息与异常堆栈。用于悬停提示和复制。
    /// </summary>
    public string DetailText { get; }

    /// <summary>
    /// 关键字是否出现在消息或来源中（不区分大小写）；关键字为空时总是匹配
    /// </summary>
    public bool MatchesSearch(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        var keyword = search.Trim();
        return Entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Entry.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

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
