using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Serilog.Debugging;
using Solve.Core.Application.Logging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 一个按日期命名的日志文件
/// </summary>
/// <param name="Date">文件记录的日期（本地日期）</param>
/// <param name="Sequence">同一天超过大小上限后的分卷号；首个文件为 0，之后为 1、2…（对应 _001、_002，超过 999 后为 _1000…）</param>
/// <param name="IsLegacy">升级前的旧命名（solve-20261118.log）</param>
public sealed record DailyLogFile(string Path, DateOnly Date, int Sequence, bool IsLegacy, long Length);

/// <summary>
/// 按日期命名的日志文件约定：命名、识别、列举与过期清理。写入（日志管线）、历史查看、导出共用这一处。
/// </summary>
/// <remarks>
/// <para>文件名为「年_月_日.log」（2026_11_18.log），年在前：资源管理器按名称排序即按时间排序，跨年也不乱。
/// 同一天超过大小上限时由 Serilog 续写为 2026_11_18_001.log、_002…；超过 999 卷后序号变为 4 位（_1000），同样识别，
/// 否则这些文件既不能查看、也不会被清理，会悄悄占满磁盘。</para>
/// <para>同时识别升级前的旧命名 solve-20261118(_001).log：旧文件照常可查看、可导出，到期后一并清理。</para>
/// <para>保存天数见 <see cref="LogRetentionPolicy"/>；只按天数删除，不按总大小删除。</para>
/// </remarks>
public static class DailyLogFiles
{
    public const string DateFormat = "yyyy_MM_dd";

    public const string Extension = ".log";

    // 序号 3～9 位：Serilog 用至少 3 位补零；上限 9 位保证能放进 int
    private static readonly Regex CurrentName = new(
        @"^(?<date>\d{4}_\d{2}_\d{2})(?:_(?<seq>\d{3,9}))?\.log$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyName = new(
        @"^solve-(?<date>\d{8})(?:_(?<seq>\d{3,9}))?\.log$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 清理可能同时来自两处：写盘线程打开新文件时、设置页保存后立即清理。串行执行，免得两边对同一批文件重复删除、互相报错
    private static readonly Lock PruneGate = new();

    public static string FileName(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;

    public static string PathFor(string directory, DateOnly date) => Path.Combine(directory, FileName(date));

    /// <summary>
    /// 按文件名识别日志文件；名称不符合约定或日期不合法时返回 false
    /// </summary>
    public static bool TryParse(string path, long length, out DailyLogFile file)
    {
        file = null!;
        var name = Path.GetFileName(path);

        var isLegacy = false;
        var match = CurrentName.Match(name);
        if (!match.Success)
        {
            match = LegacyName.Match(name);
            isLegacy = true;
        }

        if (!match.Success)
            return false;

        var format = isLegacy ? "yyyyMMdd" : DateFormat;
        if (!DateOnly.TryParseExact(match.Groups["date"].Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;

        var sequence = match.Groups["seq"].Success ? int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture) : 0;
        file = new DailyLogFile(path, date, sequence, isLegacy, length);
        return true;
    }

    /// <summary>
    /// 列出目录下的日志文件：按日期、同日旧命名在前（升级前写的）、分卷号排序。目录不存在时返回空列表。
    /// </summary>
    public static IReadOnlyList<DailyLogFile> List(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            return [];

        var files = new List<DailyLogFile>();
        foreach (var candidate in info.EnumerateFiles("*" + Extension))
        {
            if (TryParse(candidate.FullName, candidate.Length, out var file))
                files.Add(file);
        }

        return files
            .OrderBy(f => f.Date)
            .ThenBy(f => f.IsLegacy ? 0 : 1)
            .ThenBy(f => f.Sequence)
            .ToList();
    }

    /// <summary>
    /// 选出应删除的文件：只按保存天数，早于「今天往前 <paramref name="retentionDays"/> 天（含今天）」的都删，不看文件大小。
    /// <paramref name="protectedPath"/>（正在写入的文件）永不删除。纯函数，不做任何 I/O。
    /// </summary>
    /// <param name="files">按 <see cref="List"/> 的顺序（从旧到新）</param>
    public static IReadOnlyList<DailyLogFile> SelectForDeletion(
        IReadOnlyList<DailyLogFile> files,
        DateOnly today,
        int retentionDays,
        string? protectedPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        var oldestKept = today.AddDays(-(retentionDays - 1));
        return files.Where(f => f.Date < oldestKept && !IsProtected(f, protectedPath)).ToList();
    }

    /// <summary>
    /// 删除超过保存天数的日志文件。不抛出：删除失败（被占用、无权限）只记入 Serilog 自诊断，日志照常写入。
    /// </summary>
    /// <returns>实际删除的文件</returns>
    public static IReadOnlyList<DailyLogFile> Prune(string directory, DateOnly today, int retentionDays, string? protectedPath)
    {
        lock (PruneGate)
        {
            IReadOnlyList<DailyLogFile> candidates;
            try
            {
                candidates = SelectForDeletion(List(directory), today, retentionDays, protectedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("列举日志目录 {0} 失败，本次跳过清理：{1}", directory, ex.Message);
                return [];
            }

            var deleted = new List<DailyLogFile>(candidates.Count);
            foreach (var file in candidates)
            {
                try
                {
                    File.Delete(file.Path);
                    deleted.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SelfLog.WriteLine("删除过期日志 {0} 失败：{1}", file.Path, ex.Message);
                }
            }

            return deleted;
        }
    }

    /// <summary>
    /// 汇总一批日志文件：个数、合计大小、日期范围
    /// </summary>
    public static LogPrunePreview Summarize(IReadOnlyList<DailyLogFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            return LogPrunePreview.None;

        return new LogPrunePreview(files.Count, files.Sum(f => f.Length), files.Min(f => f.Date), files.Max(f => f.Date));
    }

    private static bool IsProtected(DailyLogFile file, string? protectedPath)
        => protectedPath is not null
           && string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(protectedPath), StringComparison.OrdinalIgnoreCase);
}
