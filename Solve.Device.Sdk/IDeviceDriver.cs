namespace Solve.Device.Sdk;

/// <summary>
/// 驱动包入口。package.json 的 entryType 指向实现本接口的公开类型，该类型须有公开的无参构造函数。
/// </summary>
/// <remarks>
/// 构造函数与 <see cref="Drivers"/> 只做声明，不得访问文件、网络、串口或启动线程：设备库在程序启动时调用它们。
/// </remarks>
public interface IDriverPackage
{
    IReadOnlyList<IDeviceDriver> Drivers { get; }
}

/// <summary>
/// 一种设备的驱动
/// </summary>
public interface IDeviceDriver
{
    DriverInfo Info { get; }

    /// <summary>
    /// 为总线上的一个模块创建会话。每个模块一个会话，会话可保存该模块的协议状态（如序号、分段读取进度）。
    /// </summary>
    IDeviceSession CreateSession(DeviceContext context);
}

/// <summary>
/// 模块所在总线的物理链路类型
/// </summary>
public enum LinkKind
{
    /// <summary>
    /// 串口（RS-232 / RS-485）
    /// </summary>
    Serial = 1,

    /// <summary>
    /// TCP 客户端（直连设备，或经串口服务器转发）
    /// </summary>
    Tcp = 2,
}

/// <summary>
/// 模块的上下文
/// </summary>
/// <param name="Address">模块在总线上的地址，已按 <see cref="DriverInfo.MinAddress"/>–<see cref="DriverInfo.MaxAddress"/> 校验</param>
/// <param name="Link">所在总线的链路类型。只表示物理链路：经串口服务器转发的 RTU 也是 <see cref="LinkKind.Tcp"/>，
/// 报文封装方式应由驱动声明的参数决定，链路类型只适合作为该参数的默认值参考</param>
/// <param name="Parameters">模块参数值，已按 <see cref="DriverInfo.Parameters"/> 校验并补全默认值</param>
public sealed record DeviceContext(int Address, LinkKind Link, DeviceParameters Parameters);

/// <summary>
/// 发给模块的一条命令
/// </summary>
/// <param name="Key">对应 <see cref="CommandInfo.Key"/></param>
/// <param name="Arguments">参数（按 <see cref="PointInfo.Unit"/> 换算后的工程值）</param>
public sealed record DeviceCommand(string Key, IReadOnlyDictionary<string, double> Arguments)
{
    /// <summary>
    /// 通道号：通道级命令（<see cref="CommandInfo.Group"/> 非空）为 1..通道数；模块级命令为 0
    /// </summary>
    public int Channel { get; init; }
}

/// <summary>
/// 一个模块的协议会话。会话只编解码报文，不做任何 I/O：
/// 发送、接收、超时、重试、通道互斥、仿真模式与安全检查都由内核完成。
/// </summary>
/// <remarks>
/// 线程：内核保证同一会话的调用串行进行，会话内部不需要加锁。
/// </remarks>
public interface IDeviceSession
{
    /// <summary>
    /// 开始一次轮询，读取 <see cref="DriverInfo.Points"/> 中的点位
    /// </summary>
    IExchange BeginPoll();

    /// <summary>
    /// 开始执行一条命令。命令不在 <see cref="DriverInfo.Commands"/> 中或参数无效时，返回的交互第一步即为 <see cref="ExchangeStep.Failed"/>。
    /// </summary>
    IExchange BeginCommand(DeviceCommand command);
}
