using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging.TestSupport;

/// <summary>
/// Serilog 的 SelfLog 是进程级全局状态，每创建一条日志管线都会把它指向自己的目录。
/// 会创建管线（含 AppHost）的测试类放进同一集合串行执行，避免互相改写 SelfLog 输出位置。
/// </summary>
[CollectionDefinition(Name)]
public sealed class LoggingPipelineCollection
{
    public const string Name = "LoggingPipeline";
}

internal static class LogFiles
{
    /// <summary>
    /// 按时间顺序读取目录下全部日志文件。按读写共享打开：日志管线未释放时文件仍被写入端占用。
    /// </summary>
    public static string ReadAll(string logDirectory)
        => string.Concat(DailyLogFiles.List(logDirectory).Select(f => Read(f.Path)));

    public static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 反复执行 <paramref name="probe"/> 直到条件满足或超时。用于等待异步写盘、配置文件热更新等最终一致的结果。
    /// </summary>
    public static async Task<bool> EventuallyAsync(Func<bool> probe, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
                return true;
            await Task.Delay(50);
        }

        return probe();
    }
}
