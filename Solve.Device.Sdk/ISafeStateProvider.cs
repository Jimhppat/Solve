namespace Solve.Device.Sdk;

/// <summary>
/// 可选能力（SDK 1.3 新增）：会话能给出一次"把设备带到安全状态"的交互，如电子负载卸载、电源关闭输出。
/// 由 <see cref="IDeviceSession"/> 的实现类一并实现；内核用类型检查发现它，没有实现的会话保持原行为。
/// </summary>
/// <remarks>
/// <para>内核在两种场合调用，走同一条路径：</para>
/// <list type="bullet">
/// <item>总线安全跳闸（操作员急停、驱动异常、监督循环异常）：先锁存安全停止、拒绝一切其他报文，再在关闭物理通道之前、
/// 有限时限内对该总线上每个实现了本接口的模块执行整机安全状态（<c>group = null, channel = 0</c>）。
/// 下发失败或超时不会阻止跳闸，内核只记录严重日志。</item>
/// <item>操作员按位置执行"安全停止"（单路或整机）：这是安全方向的专用操作，内核免除停用位置联锁与实机确认，
/// 但模块就绪、总线已连接、接线唯一照常检查。普通的卸载 / 关输出命令仍按其安全等级受联锁约束。</item>
/// </list>
/// <para>安全状态是"尽力而为"的补充，不能替代设备自身的硬件保护与通信看门狗。</para>
/// <para>实现要求：</para>
/// <list type="bullet">
/// <item>只能把设备带向更安全的方向（卸载、关闭输出、断开继电器），不得打开任何输出、不得改变目标之外的通道。</item>
/// <item>不依赖会话里可能已经失效的状态：上一次交互可能刚以超时或异常结束，设备的实际状态未知。
/// 例如不要先确认型号再关闭——确认失败会让关闭也发不出去。</item>
/// <item>步数尽量少、响应超时尽量短：跳闸时同一条总线上所有模块共用一个时限，排在后面的模块可能来不及执行。</item>
/// <item>不支持的目标（未声明的通道组、越界的通道号、设备不能单路关闭）返回第一步即为
/// <see cref="ExchangeStep.Failed"/>（<see cref="FailureKind.Configuration"/>）的交互，不发送任何字节；整机安全状态必须支持。</item>
/// <item>与 <see cref="IExchange"/> 的其他约定相同：纯计算、不阻塞、可预期的问题用 <see cref="ExchangeStep.Failed"/> 表达。
/// 内核同样保证与该会话的其他调用串行。</item>
/// </list>
/// </remarks>
public interface ISafeStateProvider
{
    /// <summary>
    /// 开始一次把设备带到安全状态的交互
    /// </summary>
    /// <param name="group">通道组（<see cref="ChannelGroupInfo.Key"/>）；为 null 表示整台模块</param>
    /// <param name="channel">通道号：<paramref name="group"/> 非空时为该组内的 1..数量（与 <see cref="DeviceCommand.Channel"/> 相同）；
    /// <paramref name="group"/> 为 null 时必须为 0，表示全部通道</param>
    IExchange BeginSafeState(string? group, int channel);
}
