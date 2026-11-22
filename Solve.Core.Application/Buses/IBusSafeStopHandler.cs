using Solve.Core.Domain.Buses;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 安全跳闸时、关闭物理通道之前，把总线上的设备带到安全状态（负载卸载、电源关闭输出）。
/// 总线层不认识设备，由设备层实现并经 <see cref="BusManager.AttachSafeStopHandler"/> 挂接。
/// </summary>
/// <remarks>
/// <para>调用方是总线的监督任务（或释放流程），调用时：总线已锁存 SafeStop，其他事务一律被拒；
/// 调用方持有通道锁，<paramref name="channel"/> 是一个特许通道——只在本次调用期间有效，锁存态下仍可读写。</para>
/// <para>时限：<paramref name="cancellationToken"/> 在 <see cref="BusRuntimeOptions.SafeStopTimeout"/> 后取消，到时未完成的部分放弃，
/// 通道随后照常关闭。实现必须响应取消；单个设备失败时记录后继续下一个，抛出的异常由调用方记为严重日志，都不会阻止跳闸。</para>
/// </remarks>
public interface IBusSafeStopHandler
{
    /// <param name="busId">跳闸的总线</param>
    /// <param name="descriptor">总线当前配置（帧间隔计算等）</param>
    /// <param name="channel">特许通道，只在本次调用期间有效</param>
    /// <param name="cancellationToken">安全停止的总时限</param>
    Task RunAsync(BusId busId, BusDescriptor descriptor, IBusChannel channel, CancellationToken cancellationToken);
}
