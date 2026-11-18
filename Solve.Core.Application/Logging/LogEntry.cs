using Microsoft.Extensions.Logging;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 一条供界面展示的运行日志。消息已渲染为文本，界面层不依赖具体日志库。
/// </summary>
/// <param name="Timestamp">产生时间</param>
/// <param name="Level">级别</param>
/// <param name="Source">来源（日志器类别名，通常是类的完整名称）；未知时为空字符串</param>
/// <param name="Message">渲染后的消息文本</param>
/// <param name="Exception">附带的异常（含堆栈）；没有时为 null</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,
    string Message,
    string? Exception);
