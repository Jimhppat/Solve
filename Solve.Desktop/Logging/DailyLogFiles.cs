using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog.Debugging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 一个按日期命名的日志文件
/// </summary>
/// <param name="Date">文件记录的日期（本地日期）</param>
/// <param name="Sequence">同一天超过大小上限后的分卷号；首个文件为 0，之后为 1、2…（对应 _001、_002）</param>
/// <param name="IsLegacy">升级前的旧命名（solve-20261118.log）</param>
public sealed record DailyLogFile(string Path, DateOnly Date, int Sequence, bool IsLegacy, long Length);

/// <summary>
/// 按日期命名的日志文件约定：命名、识别、列举与过期清理。写入（日志管线）、历史查看、导出共用这一处。
/// </summary>
/// <remarks>
/// <para>文件名为「年_月_日.log」（2026_11_18.log），年在前：资源管理器按名称排序即按时间排序，跨年也不乱。
/// 同一天超过大小上限时由 Serilog 续写为 2026_11_18_001.log、_002…</para>
/// <para>同时识别升级前的旧命名 solve-20261118(_001).log：旧文件照常可查看、可导出，到期后一并清理。</para>
/// </remarks>
public static class DailyLogFiles
{
    public const string DateFormat = "yyyy_MM_dd";

    public const string Extension = ".log";

    /// <summary>
    /// 保留最近多少天（含今天）
    /// </summary>
    public const int RetentionDays = 30;

    /// <summary>
    /// 全部日志文件的总大小上限。超出时从最旧的文件开始删除，防止日志异常暴增时占满磁盘（配置库也在同一磁盘上）
    /// </summary>
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private static readonly Regex CurrentName = new(
        @"^(?<date>\d{4}_\d{2}_\d{2})(?:_(?<seq>\d{3}))?\.log$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyName = new(
        @"^solve-(?<date>\d{8})(?:_(?<seq>\d{3}))?\.log$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
    /// 选出应删除的文件：先删超过保留天数的，再在总大小超限时从最旧的开始删。
    /// <paramref name="protectedPath"/>（正在写入的文件）永不删除。纯函数，不做任何 I/O。
    /// </summary>
    /// <param name="files">按 <see cref="List"/> 的顺序（从旧到新）</param>
    public static IReadOnlyList<DailyLogFile> SelectForDeletion(
        IReadOnlyList<DailyLogFile> files,
        DateOnly today,
        int retentionDays,
        long maxTotalBytes,
        string? protectedPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        var oldestKept = today.AddDays(-(retentionDays - 1));
        var deletions = new List<DailyLogFile>();
        var kept = new List<DailyLogFile>();

        foreach (var file in files)
        {
            if (file.Date < oldestKept && !IsProtected(file, protectedPath))
                deletions.Add(file);
            else
                kept.Add(file);
        }

        var total = kept.Sum(f => f.Length);
        foreach (var file in kept)
        {
            if (total <= maxTotalBytes)
                break;
            if (IsProtected(file, protectedPath))
                continue;

            deletions.Add(file);
            total -= file.Length;
        }

        return deletions;
    }

    /// <summary>
    /// 清理过期与超限的日志文件。不抛出：删除失败（被占用、无权限）只记入 Serilog 自诊断，日志照常写入。
    /// </summary>
    /// <returns>实际删除的文件数</returns>
    public static int Prune(string directory, DateOnly today, string? protectedPath)
    {
        IReadOnlyList<DailyLogFile> candidates;
        try
        {
            candidates = SelectForDeletion(List(directory), today, RetentionDays, MaxTotalBytes, protectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SelfLog.WriteLine("列举日志目录 {0} 失败，本次跳过清理：{1}", directory, ex.Message);
            return 0;
        }

        var deleted = 0;
        foreach (var file in candidates)
        {
            try
            {
                File.Delete(file.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("删除过期日志 {0} 失败：{1}", file.Path, ex.Message);
            }
        }

        return deleted;
    }

    private static bool IsProtected(DailyLogFile file, string? protectedPath)
        => protectedPath is not null
           && string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(protectedPath), StringComparison.OrdinalIgnoreCase);
}
