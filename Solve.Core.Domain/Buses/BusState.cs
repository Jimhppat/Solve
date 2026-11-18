namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线生命周期状态。合法迁移只由 <see cref="BusStateMachine"/> 决定。
/// </summary>
/// <remarks>
/// 数值会被持久化/记入日志，已有成员的数值不得修改，新增成员只能追加。
/// </remarks>
public enum BusState
{
    /// <summary>
    /// 断开（初始状态，物理通道已释放）
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// 连接中（首次打开物理通道）
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// 已连接（允许收发）
    /// </summary>
    Connected = 2,

    /// <summary>
    /// 重连中（通信异常后按重连策略自动恢复）
    /// </summary>
    Reconnecting = 3,

    /// <summary>
    /// 故障（自动重连耗尽，物理通道已释放，需人工复位）
    /// </summary>
    Faulted = 4,

    /// <summary>
    /// 断开中（主动关闭，正在释放物理通道）
    /// </summary>
    Disconnecting = 5,

    /// <summary>
    /// 安全停止（看门狗、联锁或未处理异常触发的锁存态，发送通道锁定，需人工复位）
    /// </summary>
    SafeStop = 6,
}
