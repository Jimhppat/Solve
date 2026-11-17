using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 一次已生效的状态迁移，供运行时写黑匣子日志和发布状态事件。
/// </summary>
public readonly record struct BusTransition(BusState From, BusTrigger Trigger, BusState To)
{
    /// <summary>
    /// 是否为自迁移（如 Reconnecting 下再次 OpenFailed、SafeStop 下再次 SafetyTrip）。
    /// 运行时可据此决定是否重复发布状态事件。
    /// </summary>
    public bool IsSelfTransition => From == To;
}

/// <summary>
/// 总线错误码
/// </summary>
public static class BusErrors
{
    public const string InvalidTransitionCode = "Bus.InvalidTransition";

    public static Error InvalidTransition(BusState from, BusTrigger trigger)
        => new(InvalidTransitionCode, $"总线状态 {from} 不接受触发 {trigger}。");
}

/// <summary>
/// 单条总线的生命周期状态机。只负责"能不能迁移、迁到哪"，不做 I/O、不计时。
/// </summary>
/// <remarks>
/// 线程安全：监督线程（重连/心跳）、I/O 调用方、UI 命令可能并发触发，Fire 内部加锁保证读-判-写原子。
/// 初始状态固定为 Disconnected：进程重启后物理通道必然已关闭，不从持久化恢复运行态。
/// </remarks>
public sealed class BusStateMachine
{
    private readonly Lock _gate = new();
    private BusState _state = BusState.Disconnected;

    public BusState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 触发一次迁移。非法迁移返回 Bus.InvalidTransition，状态保持不变。
    /// </summary>
    public Result<BusTransition> Fire(BusTrigger trigger)
    {
        lock (_gate)
        {
            if (!TryGetNext(_state, trigger, out var next))
                return Result<BusTransition>.Failure(BusErrors.InvalidTransition(_state, trigger));

            var transition = new BusTransition(_state, trigger, next);
            _state = next;
            return Result<BusTransition>.Success(transition);
        }
    }

    /// <summary>
    /// 迁移表（纯函数）。表外的组合（含未定义的枚举值）一律拒绝。
    /// </summary>
    public static bool TryGetNext(BusState from, BusTrigger trigger, out BusState to)
    {
        BusState? next = (from, trigger) switch
        {
            // 安全跳闸优先于一切：任何状态都锁存到 SafeStop；SafeStop 下重复跳闸为幂等自迁移
            (_, BusTrigger.SafetyTrip) when Enum.IsDefined(from) => BusState.SafeStop,

            (BusState.Disconnected, BusTrigger.Connect) => BusState.Connecting,

            (BusState.Connecting, BusTrigger.OpenSucceeded) => BusState.Connected,
            (BusState.Connecting, BusTrigger.OpenFailed) => BusState.Reconnecting,
            (BusState.Connecting, BusTrigger.Disconnect) => BusState.Disconnecting,

            (BusState.Connected, BusTrigger.IoFailed) => BusState.Reconnecting,
            (BusState.Connected, BusTrigger.HeartbeatLost) => BusState.Reconnecting,
            (BusState.Connected, BusTrigger.Disconnect) => BusState.Disconnecting,

            (BusState.Reconnecting, BusTrigger.OpenSucceeded) => BusState.Connected,
            // 单次重连失败保持 Reconnecting，是否耗尽由重连策略判定后触发 RetryExhausted
            (BusState.Reconnecting, BusTrigger.OpenFailed) => BusState.Reconnecting,
            (BusState.Reconnecting, BusTrigger.RetryExhausted) => BusState.Faulted,
            (BusState.Reconnecting, BusTrigger.Disconnect) => BusState.Disconnecting,

            // 关闭过程中的错误不改变去向：运行时必须在 finally 中释放资源后触发 Closed
            (BusState.Disconnecting, BusTrigger.Closed) => BusState.Disconnected,

            // 故障与安全停止都是锁存态，只能人工复位，禁止直接 Connect 绕过
            (BusState.Faulted, BusTrigger.Reset) => BusState.Disconnected,
            (BusState.SafeStop, BusTrigger.Reset) => BusState.Disconnected,

            _ => null,
        };

        to = next.GetValueOrDefault();
        return next.HasValue;
    }

    /// <summary>
    /// 当前状态是否允许业务收发。只有 Connected 允许，Faulted / SafeStop 下发送通道锁定。
    /// </summary>
    public static bool AllowsIo(BusState state) => state == BusState.Connected;
}
