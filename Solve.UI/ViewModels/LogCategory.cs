using Microsoft.Extensions.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 界面上的五类日志级别，按严重程度从低到高。
/// </summary>
/// <remarks>
/// 跟踪（Trace）归入调试：两者都是开发排查用的细节，现场按一类看即可；日志文件中仍保留原始级别。
/// </remarks>
public enum LogCategory
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4,
}

public static class LogCategories
{
    /// <summary>
    /// 全部五类，按严重程度从低到高
    /// </summary>
    public static readonly IReadOnlyList<LogCategory> All =
        [LogCategory.Debug, LogCategory.Information, LogCategory.Warning, LogCategory.Error, LogCategory.Critical];

    /// <summary>
    /// 需要操作员关注的异常：警告、错误、致命
    /// </summary>
    public static readonly IReadOnlyList<LogCategory> Problems =
        [LogCategory.Warning, LogCategory.Error, LogCategory.Critical];

    /// <summary>
    /// 除调试以外的四类：主控台的默认显示，现场把某个来源调到 Debug 排查时不会刷满主控台
    /// </summary>
    public static readonly IReadOnlyList<LogCategory> Operational =
        [LogCategory.Information, LogCategory.Warning, LogCategory.Error, LogCategory.Critical];

    public static LogCategory FromLevel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => LogCategory.Debug,
        LogLevel.Information => LogCategory.Information,
        LogLevel.Warning => LogCategory.Warning,
        LogLevel.Error => LogCategory.Error,
        _ => LogCategory.Critical,
    };

    public static string DisplayName(this LogCategory category) => category switch
    {
        LogCategory.Debug => "调试",
        LogCategory.Information => "信息",
        LogCategory.Warning => "警告",
        LogCategory.Error => "错误",
        _ => "致命",
    };
}
