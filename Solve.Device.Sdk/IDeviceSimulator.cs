namespace Solve.Device.Sdk;

/// <summary>
/// 驱动的可选能力：为仿真模式提供设备仿真器。由实现 <see cref="IDeviceDriver"/> 的同一个类型选择性实现。
/// </summary>
/// <remarks>
/// <para>仿真模式下内核不触碰任何真实设备，总线上发出的请求交给各模块的仿真设备应答，
/// 这样不接设备也能让模块上线、跑通命令的完整流程。协议知识只在驱动包里：内核只转交字节，不解析报文。</para>
/// <para>不实现本接口的驱动在仿真模式下没有设备应答，模块按响应超时转为通信异常。
/// 实机模式下内核绝不创建、也绝不调用仿真设备。</para>
/// <para>与 <see cref="IDriverPackage"/> 一样，<see cref="CreateSimulator"/> 只做构造，不得访问文件、网络、串口或启动线程。</para>
/// </remarks>
public interface IDeviceSimulatorProvider
{
    /// <summary>
    /// 为总线上的一个模块创建仿真设备。<paramref name="context"/> 与同一模块的 <see cref="IDeviceDriver.CreateSession"/> 收到的相同。
    /// 模块或所在总线的配置变化后会重新创建，仿真设备内的状态（如输出开关）随之从头开始。
    /// </summary>
    ISimulatedDevice CreateSimulator(DeviceContext context);
}

/// <summary>
/// 一个模块的仿真设备："收到请求字节 → 回应答字节"。
/// </summary>
/// <remarks>
/// <para>分派：内核把总线上发出的每一帧请求交给这条总线上全部模块的仿真设备，由仿真设备按自己的协议判断请求是不是发给自己的。
/// 恰好一台返回应答时，应答送回总线；没有设备应答时总线上就没有响应（由内核按超时处理）；
/// 两台以上同时应答视为总线冲突，本次不应答并告警（实际总线上的冲突同样得不到有效响应）。</para>
/// <para>实现要求与 <see cref="IExchange"/> 相同：纯计算，必须很快返回（毫秒级以内），不得阻塞、等待或访问外部资源。
/// 抛出的异常只让这一次请求没有应答并记入日志，不影响总线与其他模块。</para>
/// <para>线程：内核保证同一仿真设备的调用串行进行，内部不需要加锁。</para>
/// </remarks>
public interface ISimulatedDevice
{
    /// <summary>
    /// 一次应答的最大字节数。超出的应答视为仿真设备缺陷，本次不应答并记入日志
    /// </summary>
    public const int MaxResponseLength = 64 * 1024;

    /// <summary>
    /// 处理总线上发出的一帧请求（即某次交互中 <see cref="ExchangeStep.Send.Request"/> 的完整内容）。
    /// </summary>
    /// <param name="request">完整的请求报文</param>
    /// <returns>本设备的应答字节（完整一帧）；请求不是发给本设备的、无法识别，或按协议本就不应答（如设置命令、广播）时返回 null</returns>
    byte[]? Respond(ReadOnlySpan<byte> request);
}
