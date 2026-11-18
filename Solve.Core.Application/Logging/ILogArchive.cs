using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 某一天的历史日志
/// </summary>
/// <param name="Entries">按时间顺序；条数超过读取上限时只保留最后若干条</param>
/// <param name="TotalCount">该天日志的总条数</param>
public sealed record LogDay(DateOnly Date, IReadOnlyList<LogEntry> Entries, int TotalCount)
{
    public bool IsTruncated => TotalCount > Entries.Count;
}

/// <summary>
/// 按日期保存的历史日志：列出有日志的日期、读取某天的日志、导出某天的日志文件。
/// </summary>
/// <remarks>
/// 界面只通过它访问历史日志，不关心文件命名与行格式。所有方法都在后台线程执行磁盘读取；
/// 可预期的失败（文件被删除、无权限、格式无法识别）以 <see cref="Result"/> 返回，取消与超时抛出
/// <see cref="OperationCanceledException"/>。
/// </remarks>
public interface ILogArchive
{
    /// <summary>
    /// 有日志文件的日期，从新到旧
    /// </summary>
    Task<Result<IReadOnlyList<DateOnly>>> GetAvailableDatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 读取某天的全部日志（含同日所有分卷），只保留最后 <paramref name="maxEntries"/> 条
    /// </summary>
    Task<Result<LogDay>> ReadDayAsync(DateOnly date, int maxEntries, CancellationToken cancellationToken);

    /// <summary>
    /// 导出时建议的文件名
    /// </summary>
    string SuggestExportFileName(DateOnly date);

    /// <summary>
    /// 把某天的日志文件（同日分卷按顺序拼接）原样写入 <paramref name="destination"/>
    /// </summary>
    Task<Result> ExportDayAsync(DateOnly date, Stream destination, CancellationToken cancellationToken);
}
