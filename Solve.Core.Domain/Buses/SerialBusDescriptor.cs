using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 串口校验位。领域层自有枚举，由硬件适配器映射到 System.IO.Ports，避免领域层依赖该包。
/// </summary>
public enum SerialParity
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4,
}

/// <summary>
/// 串口停止位。从 1 开始编号，使 default 值成为非法值，未赋值会被校验拦截。
/// </summary>
public enum SerialStopBits
{
    One = 1,
    Two = 2,
    OnePointFive = 3,
}

/// <summary>
/// 串口（RS-232 / RS-485）总线描述。默认 9600 8N1。
/// </summary>
/// <param name="PortName">端口名，如 Windows 的 "COM3" 或 Linux 的 "/dev/ttyUSB0"</param>
public sealed record SerialBusDescriptor(BusId Id, string Name, string PortName) : BusDescriptor(Id, Name)
{
    public const int MinBaudRate = 50;
    public const int MaxBaudRate = 4_000_000;

    /// <summary>
    /// 波特率，单位 bit/s。不限定标准档位，部分设备使用 250000 等非标波特率。
    /// </summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>
    /// 数据位，5–8
    /// </summary>
    public int DataBits { get; init; } = 8;

    public SerialParity Parity { get; init; } = SerialParity.None;

    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;

    protected override void CollectLinkErrors(List<Error> errors)
    {
        if (string.IsNullOrWhiteSpace(PortName))
            errors.Add(BusErrors.InvalidField("PortName", "串口端口名不能为空。"));

        if (BaudRate is < MinBaudRate or > MaxBaudRate)
            errors.Add(BusErrors.InvalidField("BaudRate", $"波特率必须在 [{MinBaudRate}, {MaxBaudRate}] 之间，当前为 {BaudRate}。"));

        if (DataBits is < 5 or > 8)
            errors.Add(BusErrors.InvalidField("DataBits", $"数据位必须在 [5, 8] 之间，当前为 {DataBits}。"));

        if (!Enum.IsDefined(Parity))
            errors.Add(BusErrors.InvalidField("Parity", $"未定义的校验位：{(int)Parity}。"));

        if (!Enum.IsDefined(StopBits))
        {
            errors.Add(BusErrors.InvalidField("StopBits", $"未定义的停止位：{(int)StopBits}。"));
            return;
        }

        // UART/Win32 DCB 硬件约束：1.5 停止位只能配 5 数据位；5 数据位不能配 2 停止位。
        // 在配置阶段拦截，否则要到打开串口时才由驱动报出含糊的参数错误。
        if (StopBits == SerialStopBits.OnePointFive && DataBits != 5)
            errors.Add(BusErrors.InvalidField("StopBits", "1.5 停止位只能与 5 数据位搭配。"));

        if (StopBits == SerialStopBits.Two && DataBits == 5)
            errors.Add(BusErrors.InvalidField("StopBits", "5 数据位不能与 2 停止位搭配。"));
    }
}
