using Solve.Core.Application.Common;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 运行日志的实时推送，供界面的日志面板订阅。
/// </summary>
/// <remarks>
/// 只用于展示：订阅者处理慢时只丢弃自己最旧的条目，不影响写盘，也不会阻塞产生日志的线程。完整记录以日志文件为准。
/// </remarks>
public interface ILogFeed
{
    /// <summary>
    /// 订阅之后产生的日志。<paramref name="recent"/> 为订阅时刻缓存的最近若干条（按时间顺序），
    /// 与订阅收到的条目首尾衔接、不重不漏。
    /// </summary>
    EventSubscription<LogEntry> Subscribe(out IReadOnlyList<LogEntry> recent);
}
