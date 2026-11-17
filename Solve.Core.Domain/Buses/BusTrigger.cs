namespace Solve.Core.Domain.Buses;

/// <summary>
/// 驱动总线状态迁移的事件。由运行时（BusRuntime）根据 I/O 结果和用户操作触发。
/// </summary>
/// <remarks>
/// 数值会记入日志，已有成员的数值不得修改，新增成员只能追加。
/// </remarks>
public enum BusTrigger
{
    /// <summary>
    /// 用户请求连接
    /// </summary>
    Connect = 0,

    /// <summary>
    /// 物理通道打开成功
    /// </summary>
    OpenSucceeded = 1,

    /// <summary>
    /// 物理通道打开失败（超时、端口占用、对端拒绝等）
    /// </summary>
    OpenFailed = 2,

    /// <summary>
    /// 已连接状态下读写失败
    /// </summary>
    IoFailed = 3,

    /// <summary>
    /// 心跳超时
    /// </summary>
    HeartbeatLost = 4,

    /// <summary>
    /// 自动重连次数耗尽
    /// </summary>
    RetryExhausted = 5,

    /// <summary>
    /// 用户请求断开
    /// </summary>
    Disconnect = 6,

    /// <summary>
    /// 物理通道已关闭、资源已释放
    /// </summary>
    Closed = 7,

    /// <summary>
    /// 安全跳闸（看门狗超时、上层联锁、未处理异常），任何状态均可触发
    /// </summary>
    SafetyTrip = 8,

    /// <summary>
    /// 人工复位，只能从 Faulted / SafeStop 回到 Disconnected
    /// </summary>
    Reset = 9,
}
