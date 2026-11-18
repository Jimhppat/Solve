using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Composition;

/// <summary>
/// 不读取任何联锁信号的安全联锁：SafeStop 的人工复位操作本身即视为人工确认，始终允许。
/// </summary>
/// <remarks>
/// 当前阶段按要求不接入信号。接入急停/门禁等物理信号后，必须在组合根中替换为读取真实信号的实现。
/// 每次复位都以 Warning 记入黑匣子，便于事后追溯"是谁在什么时候解除了安全停止"。
/// 该方法在 BusRuntime 状态锁内被调用；Serilog 异步日志只入队不写盘，不会阻塞。
/// </remarks>
public sealed class NoSignalSafetyInterlock(ILogger logger) : IBusSafetyInterlock
{
    public Result CanReset(BusId busId)
    {
        logger.LogWarning("总线 {BusId} 从 SafeStop 人工复位：未接入物理联锁信号，复位操作视为人工确认", busId);
        return Result.Success();
    }
}
