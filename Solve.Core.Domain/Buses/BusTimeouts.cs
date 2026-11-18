using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线 I/O 超时。所有物理通道操作都必须有确定的上限，禁止无限等待。
/// </summary>
/// <param name="Connect">打开物理通道（串口打开 / TCP 握手）的超时</param>
/// <param name="Read">单次读操作的超时</param>
/// <param name="Write">单次写操作的超时</param>
public sealed record BusTimeouts(TimeSpan Connect, TimeSpan Read, TimeSpan Write)
{
    /// <summary>
    /// 单项超时上限。超过 60 s 的等待在现场通常意味着链路已死，应交给重连策略处理而不是继续阻塞。
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 默认值：连接 3 s，读写各 1 s（Modbus RTU/TCP 现场常用量级，可按设备调整）
    /// </summary>
    public static BusTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1));

    internal void CollectErrors(List<Error> errors)
    {
        CheckTimeout(errors, "Timeouts.Connect", Connect);
        CheckTimeout(errors, "Timeouts.Read", Read);
        CheckTimeout(errors, "Timeouts.Write", Write);
    }

    // Timeout.InfiniteTimeSpan 为 -1 ms，会被"必须大于 0"一并拒绝
    private static void CheckTimeout(List<Error> errors, string field, TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > MaxTimeout)
            errors.Add(BusErrors.InvalidField(field, $"{field} 必须在 (0, {MaxTimeout.TotalSeconds} s] 之间，当前为 {value}。"));
    }
}
