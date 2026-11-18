using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses.Ports;

/// <summary>
/// 安全联锁出站端口：决定 SafeStop 锁存态能否被人工复位。
/// </summary>
/// <remarks>
/// 在 BusRuntime 的状态锁内调用，以保证"检查联锁"与"执行复位"之间不会插入新的安全跳闸。
/// 因此实现必须非阻塞、不做 I/O：只读取内存中最新的联锁信号快照（急停、门禁、上层故障等）。
/// 联锁未解除时返回 Bus.InterlockActive。
/// </remarks>
public interface IBusSafetyInterlock
{
    Result CanReset(BusId busId);
}
