using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.UI.TestSupport;

/// <summary>
/// 历史日志替身：内存中的若干天；可让某天的读取挂起（<see cref="Hold"/>）、失败或抛出，记录导出调用
/// </summary>
internal sealed class FakeLogArchive : ILogArchive
{
    private readonly Dictionary<DateOnly, TaskCompletionSource<Result<LogDay>>> _held = new();

    public Dictionary<DateOnly, LogDay> Days { get; } = new();

    public Error? ReadError { get; set; }

    public Exception? ReadException { get; set; }

    public Error? ExportError { get; set; }

    public List<DateOnly> Exported { get; } = [];

    public CancellationToken LastReadToken { get; private set; }

    public void AddDay(DateOnly date, IReadOnlyList<LogEntry> entries, int? totalCount = null)
        => Days[date] = new LogDay(date, entries, totalCount ?? entries.Count);

    /// <summary>
    /// 让该日的读取挂起，直到对返回的任务源调用 SetResult。不响应取消，用来模拟「切换后旧结果才迟迟到达」
    /// </summary>
    public TaskCompletionSource<Result<LogDay>> Hold(DateOnly date)
    {
        var source = new TaskCompletionSource<Result<LogDay>>();
        _held[date] = source;
        return source;
    }

    public Task<Result<IReadOnlyList<DateOnly>>> GetAvailableDatesAsync(CancellationToken cancellationToken)
        => Task.FromResult(Result<IReadOnlyList<DateOnly>>.Success(Days.Keys.OrderByDescending(d => d).ToList()));

    public Task<Result<LogDay>> ReadDayAsync(DateOnly date, int maxEntries, CancellationToken cancellationToken)
    {
        LastReadToken = cancellationToken;
        if (ReadException is not null)
            throw ReadException;
        if (_held.TryGetValue(date, out var held))
            return held.Task;
        if (ReadError is not null)
            return Task.FromResult(Result<LogDay>.Failure(ReadError));

        return Task.FromResult(Days.TryGetValue(date, out var day)
            ? Result<LogDay>.Success(day)
            : Result<LogDay>.Failure(new Error("Log.DayNotFound", "没有日志文件")));
    }

    public string SuggestExportFileName(DateOnly date) => $"{date:yyyy_MM_dd}.log";

    public async Task<Result> ExportDayAsync(DateOnly date, Stream destination, CancellationToken cancellationToken)
    {
        Exported.Add(date);
        if (ExportError is not null)
            return Result.Failure(ExportError);

        await destination.WriteAsync("exported"u8.ToArray(), cancellationToken);
        return Result.Success();
    }
}
