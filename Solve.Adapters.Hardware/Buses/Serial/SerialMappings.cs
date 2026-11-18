using System.IO.Ports;
using Solve.Core.Domain.Buses;

namespace Solve.Adapters.Hardware.Buses.Serial;

/// <summary>
/// 领域串口参数 → System.IO.Ports 枚举。显式逐项映射，不依赖两边枚举数值碰巧一致。
/// </summary>
public static class SerialMappings
{
    public static Parity ToPortParity(SerialParity parity) => parity switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(parity), parity, "未定义的校验位。"),
    };

    public static StopBits ToPortStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.Two => StopBits.Two,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        _ => throw new ArgumentOutOfRangeException(nameof(stopBits), stopBits, "未定义的停止位。"),
    };
}
