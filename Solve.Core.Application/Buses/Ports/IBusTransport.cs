using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses.Ports;

/// <summary>
/// 物理通道出站端口：只负责字节流的打开、关闭与收发，不认识任何协议帧。
/// 由硬件适配器实现（串口、TCP、仿真）。
/// </summary>
/// <remarks>
/// 实现约定：
/// <list type="bullet">
/// <item>线程模型：I/O 方法非线程安全，由 BusRuntime 用通道锁串行化调用；实现内部不得再假设并发读写。</item>
/// <item>超时：由调用方通过 <see cref="CancellationToken"/> 施加。实现必须及时响应取消并抛出
/// <see cref="OperationCanceledException"/>；底层 API 不支持取消时（如部分串口驱动），须在取消回调中关闭句柄以解除阻塞。</item>
/// <item>错误：可预期的通信失败（端口占用、对端拒绝、链路断开）返回失败的 Result，错误码见 BusErrors.Transport；
/// 只有编程错误（已 Dispose 后调用、参数非法）才抛异常。</item>
/// <item>生命周期：Open/Close 可循环调用；DisposeAsync 后不可再用。Close 与 Dispose 必须幂等。</item>
/// </list>
/// </remarks>
public interface IBusTransport : IAsyncDisposable
{
    /// <summary>
    /// 物理通道当前是否处于打开状态（仅供诊断，状态判定以 BusStateMachine 为准）
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// 打开物理通道。已打开时返回 Bus.Transport.AlreadyOpen。
    /// </summary>
    ValueTask<Result> OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 关闭物理通道并释放底层句柄，幂等。正在等待的读操作必须立即以 ConnectionClosed 返回。
    /// </summary>
    ValueTask CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 读取至少 1 个字节，写入 <paramref name="buffer"/>，返回实际读取的字节数（总是 &gt; 0）。
    /// 对端关闭连接时返回 Bus.Transport.ConnectionClosed，而不是返回 0。
    /// </summary>
    ValueTask<Result<int>> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// 写入全部字节，部分写入视为失败。
    /// </summary>
    ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// 丢弃接收缓冲区中尚未读取的字节。协议层在发送新请求前调用，清除上一事务超时后迟到的残留数据。
    /// </summary>
    ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken);
}
