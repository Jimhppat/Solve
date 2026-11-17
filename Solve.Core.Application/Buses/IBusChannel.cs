using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 事务内的通道视图：只在 <see cref="BusRuntime.ExecuteAsync{T}"/> 的回调期间有效，回调返回后再使用会抛异常。
/// </summary>
/// <remarks>
/// 每个操作自动施加描述中的读/写超时，并在总线离开 Connected 时立即中止。
/// 返回的错误码：Bus.Timeout（单次操作或整个事务超时，不改变总线状态）、
/// Bus.ChannelLocked（总线已断开/跳闸）、Bus.Transport.*（链路故障，总线自动转入 Reconnecting）。
/// 只有调用方自己的取消才会以 <see cref="OperationCanceledException"/> 抛出。
/// </remarks>
public interface IBusChannel
{
    BusId BusId { get; }

    ValueTask<Result<int>> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken);
}
