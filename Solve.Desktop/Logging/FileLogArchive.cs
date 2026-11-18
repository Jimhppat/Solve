using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 日志目录中按日期保存的文件（见 <see cref="DailyLogFiles"/>）作为历史日志来源。
/// </summary>
/// <remarks>
/// <para>读写都在线程池上进行，按读写共享打开：当天的文件正在被日志管线写入，读到的是打开那一刻已写盘的内容。</para>
/// <para>只读不改：读取与导出都不会修改、移动或删除日志文件。</para>
/// </remarks>
public sealed class FileLogArchive(string directory) : ILogArchive
{
    private const int ReadBufferSize = 64 * 1024;

    // 每读这么多行检查一次取消，既能及时响应切换日期，又不在每行上付出检查开销
    private const int CancellationCheckInterval = 1_000;

    public Task<Result<IReadOnlyList<DateOnly>>> GetAvailableDatesAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DateOnly> dates = DailyLogFiles.List(directory)
                    .Select(f => f.Date)
                    .Distinct()
                    .OrderByDescending(d => d)
                    .ToList();
                return Result<IReadOnlyList<DateOnly>>.Success(dates);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<IReadOnlyList<DateOnly>>.Failure(new Error("Log.ListFailed", $"无法读取日志目录：{ex.Message}"));
            }
        }, cancellationToken);

    public Task<Result<LogDay>> ReadDayAsync(DateOnly date, int maxEntries, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        return Task.Run(() => ReadDay(date, maxEntries, cancellationToken), cancellationToken);
    }

    public string SuggestExportFileName(DateOnly date) => DailyLogFiles.FileName(date);

    public Task<Result> ExportDayAsync(DateOnly date, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(async () =>
        {
            var files = FilesOf(date);
            if (files.IsFailure)
                return Result.Failure(files.Error);

            try
            {
                foreach (var file in files.Value)
                {
                    await using var source = OpenShared(file.Path);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure(new Error("Log.ExportFailed", $"导出 {date:yyyy-MM-dd} 的日志失败：{ex.Message}"));
            }
        }, cancellationToken);
    }

    private Result<LogDay> ReadDay(DateOnly date, int maxEntries, CancellationToken cancellationToken)
    {
        var files = FilesOf(date);
        if (files.IsFailure)
            return Result<LogDay>.Failure(files.Error);

        var parser = new LogTextParser();
        var recent = new Queue<LogEntry>(Math.Min(maxEntries, 4096));
        var total = 0;

        void Keep(LogEntry? entry)
        {
            if (entry is null)
                return;

            total++;
            if (recent.Count == maxEntries)
                recent.Dequeue();
            recent.Enqueue(entry);
        }

        try
        {
            var lines = 0;
            foreach (var file in files.Value)
            {
                using var reader = new StreamReader(OpenShared(file.Path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (++lines % CancellationCheckInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    Keep(parser.Feed(line));
                }

                // 一条日志不会跨文件：每个文件结束时收尾
                Keep(parser.Flush());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<LogDay>.Failure(new Error("Log.ReadFailed", $"读取 {date:yyyy-MM-dd} 的日志失败：{ex.Message}"));
        }

        return Result<LogDay>.Success(new LogDay(date, recent.ToArray(), total));
    }

    private Result<IReadOnlyList<DailyLogFile>> FilesOf(DateOnly date)
    {
        try
        {
            IReadOnlyList<DailyLogFile> files = DailyLogFiles.List(directory).Where(f => f.Date == date).ToList();
            return files.Count == 0
                ? Result<IReadOnlyList<DailyLogFile>>.Failure(new Error("Log.DayNotFound", $"{date:yyyy-MM-dd} 没有日志文件，可能已过保留期被清理。"))
                : Result<IReadOnlyList<DailyLogFile>>.Success(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<DailyLogFile>>.Failure(new Error("Log.ListFailed", $"无法读取日志目录：{ex.Message}"));
        }
    }

    // 当天的文件正被日志管线写入：必须允许对方继续写；清理线程可能同时删除过期文件，也允许删除
    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferSize, FileOptions.SequentialScan);
}
