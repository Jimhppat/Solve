namespace Solve.Core.Application.Buses;

/// <summary>
/// 运行时（与具体总线无关）的并发与关停参数。默认值为工业现场常用量级，可按需调整。
/// </summary>
public sealed record BusRuntimeOptions
{
    public static BusRuntimeOptions Default { get; } = new();

    /// <summary>
    /// 事务等待通道锁的上限，超时返回 Bus.ChannelBusy（默认 5 s）
    /// </summary>
    public TimeSpan ChannelLockTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 单个事务（可能包含多次读写）的总时长上限，超时返回 Bus.Timeout（默认 10 s）
    /// </summary>
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 断开与释放时等待监督任务/在途事务退出的上限（默认 5 s）。超过后强制关闭物理通道。
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 监督循环遇到未处理异常后的冷却时间，防止故障时空转占满 CPU（默认 1 s）
    /// </summary>
    public TimeSpan FaultCooldown { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 安全跳闸后、关闭物理通道前，向设备下发安全状态（卸载、关输出）的总时限（默认 3 s），含等待在途事务让出通道锁。
    /// 到时未完成的模块放弃并记严重日志，通道照常关闭：跳闸不能被设备拖住。
    /// </summary>
    public TimeSpan SafeStopTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 状态事件队列容量。满时丢弃最旧事件（UI 只关心最新状态；完整轨迹以日志为准）。
    /// </summary>
    public int StateEventCapacity { get; init; } = 64;

    internal void EnsureValid()
    {
        EnsurePositive(ChannelLockTimeout, nameof(ChannelLockTimeout));
        EnsurePositive(TransactionTimeout, nameof(TransactionTimeout));
        EnsurePositive(ShutdownTimeout, nameof(ShutdownTimeout));
        EnsurePositive(FaultCooldown, nameof(FaultCooldown));
        EnsurePositive(SafeStopTimeout, nameof(SafeStopTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(StateEventCapacity, 1, nameof(StateEventCapacity));
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, value, $"{name} 必须大于 0。");
    }
}
