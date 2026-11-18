using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;

namespace Solve.Core.Tests.UI.TestSupport;

/// <summary>
/// 日志源替身：订阅时返回固定的"最近日志"，之后可用 <see cref="Publish"/> 推送新日志
/// </summary>
internal sealed class FakeLogFeed(IReadOnlyList<LogEntry> recent) : ILogFeed
{
    private readonly EventBroadcaster<LogEntry> _broadcaster = new(16);

    public int SubscriberCount => _broadcaster.SubscriberCount;

    public EventSubscription<LogEntry> Subscribe(out IReadOnlyList<LogEntry> snapshot)
    {
        snapshot = recent;
        return _broadcaster.Subscribe();
    }

    public void Publish(LogEntry entry) => _broadcaster.Publish(entry);
}
