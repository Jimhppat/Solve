namespace Solve.Device.Sdk;

/// <summary>
/// 一次报文交互（一次轮询或一条命令），由内核逐步推进的小状态机：
/// <code>
/// step = exchange.Start();
/// while (step is Send send)
/// {
///     内核：清空接收缓冲 → 按 send.GapBefore 保证通道空闲 → 发送 send.Request
///           → 不等待响应时：等待 send.DelayAfter，step = exchange.OnFrame(空)
///           → 等待响应时：在超时内接收，每收到一块字节就调用 Match，
///             直到 Complete（取出完整一帧）或 Invalid / 超时（交互失败）；然后等待 send.DelayAfter；
///     step = exchange.OnFrame(帧);
/// }
/// // step 为 Completed 或 Failed，交互结束
/// </code>
/// </summary>
/// <remarks>
/// <para>实现要求：所有方法都是纯计算，必须很快返回（毫秒级以内），不得阻塞、等待或访问外部资源。
/// 可预期的问题（校验错误、设备返回异常码、数值越界）用 <see cref="ExchangeStep.Failed"/> 表达，不要抛异常；
/// 抛出的异常会被视为通信状态未知，内核会对整条总线执行安全停止。</para>
/// <para>多余字节（粘包）：<see cref="Match"/> 返回 Complete 时，<c>frameLength</c> 之后已收到的字节被内核丢弃，
/// 并作为"响应后多余数据"记入报文追溯日志，不交给驱动。</para>
/// <para>主动上报：SDK 1 只支持主站问、从站答。每次发送前内核都会清空接收缓冲，请求之外到达的数据一律丢弃；
/// 需要设备主动上报的协议目前不受支持。</para>
/// </remarks>
public interface IExchange
{
    ExchangeStep Start();

    /// <summary>
    /// 判断已收到的字节（从本次请求后收到的第一个字节起）是否已构成完整的一帧响应。
    /// </summary>
    /// <param name="received">本次请求后累计收到的全部字节</param>
    /// <param name="frameLength">返回 <see cref="FrameStatus.Complete"/> 时为一帧的字节数（从 0 开始），其余情况为 0</param>
    FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength);

    /// <summary>
    /// 处理一帧完整响应（<see cref="ExchangeStep.Send.ExpectsResponse"/> 为 false 时传入空帧），给出下一步
    /// </summary>
    ExchangeStep OnFrame(ReadOnlySpan<byte> frame);
}

/// <summary>
/// <see cref="IExchange.Match"/> 的判断结果
/// </summary>
public enum FrameStatus
{
    /// <summary>
    /// 还不够一帧，继续接收
    /// </summary>
    NeedMoreData = 1,

    /// <summary>
    /// 已收到完整一帧（校验通过）
    /// </summary>
    Complete = 2,

    /// <summary>
    /// 收到的数据不可能构成有效响应（如 CRC 错误、地址不符），本次交互失败，按 <see cref="FailureKind.Transient"/> 处理
    /// </summary>
    Invalid = 3,
}

/// <summary>
/// 交互失败的分类，决定内核如何重试与报警
/// </summary>
public enum FailureKind
{
    /// <summary>
    /// 暂时性错误（干扰、设备忙、偶发超时）：按重试策略重试，连续失败后模块转为通信异常。
    /// 内核自己判定的超时、<see cref="FrameStatus.Invalid"/> 也按此类处理。
    /// </summary>
    Transient = 1,

    /// <summary>
    /// 配置错误（地址或参数与设备不符、设备不支持该功能）：不自动重试，模块标记为配置错误，直到其配置被修改
    /// </summary>
    Configuration = 2,

    /// <summary>
    /// 设备报告自身故障（设备返回故障码、自检失败）：产生报警，按较长间隔继续轮询以便观察恢复
    /// </summary>
    Device = 3,
}

/// <summary>
/// 发送前要求的通道空闲时间。串口链路取 max(字符数 × 一个字符的传输时间, 最小时长)，
/// 字符时间由内核按总线的波特率、数据位、校验位、停止位计算；TCP 链路只取 <see cref="Minimum"/>。
/// </summary>
/// <param name="Characters">以字符计的空闲时间，如 Modbus RTU 的 3.5</param>
/// <param name="Minimum">最小空闲时长，如 Modbus RTU 在 19200 bit/s 以上建议的固定 1.75 ms</param>
public readonly record struct FrameGap(double Characters, TimeSpan Minimum)
{
    /// <summary>
    /// Modbus RTU 帧间隔：3.5 个字符，且不少于 1.75 ms
    /// </summary>
    public static FrameGap ModbusRtu { get; } = new(3.5, TimeSpan.FromMilliseconds(1.75));
}

/// <summary>
/// 内核对交互步骤时间参数的上限。超出的步骤按 <see cref="FailureKind.Configuration"/> 失败，防止驱动错误地长时间占用总线。
/// </summary>
public static class ExchangeLimits
{
    public const double MaxGapCharacters = 100;

    public static TimeSpan MaxGapMinimum { get; } = TimeSpan.FromSeconds(1);

    public static TimeSpan MaxDelayAfter { get; } = TimeSpan.FromSeconds(10);

    public static TimeSpan MaxResponseTimeout { get; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 交互的下一步
/// </summary>
public abstract record ExchangeStep
{
    private ExchangeStep()
    {
    }

    /// <summary>
    /// 发送一帧请求
    /// </summary>
    /// <param name="Request">完整的请求报文</param>
    /// <param name="ExpectsResponse">是否等待响应；广播类请求为 false</param>
    /// <param name="ResponseTimeout">本帧的响应超时；为 null 时使用总线配置的读取超时</param>
    public sealed record Send(ReadOnlyMemory<byte> Request, bool ExpectsResponse = true, TimeSpan? ResponseTimeout = null) : ExchangeStep
    {
        /// <summary>
        /// 发送前通道至少空闲多久；为 null 时不额外等待
        /// </summary>
        public FrameGap? GapBefore { get; init; }

        /// <summary>
        /// 本帧结束（收到响应，或不等待响应时发送完成）后再等待多久才进行下一步，
        /// 如广播后给从站留出处理时间、仪表执行命令后需要的稳定时间
        /// </summary>
        public TimeSpan DelayAfter { get; init; }
    }

    /// <summary>
    /// 交互成功结束。命令交互通常没有点位值，传空列表。
    /// </summary>
    public sealed record Completed(IReadOnlyList<PointSample> Samples) : ExchangeStep;

    /// <summary>
    /// 交互失败（可预期的问题）。原因会显示给操作员并写入日志。
    /// </summary>
    /// <param name="Reason">面向操作员的原因，如"设备返回异常码 02（非法数据地址）"</param>
    /// <param name="Kind">失败分类，决定内核是否重试</param>
    public sealed record Failed(string Reason, FailureKind Kind = FailureKind.Transient) : ExchangeStep;
}

/// <summary>
/// 一个点位的数值
/// </summary>
/// <param name="Key">对应 <see cref="PointInfo.Key"/></param>
/// <param name="Value">已换算到 <see cref="PointInfo.Unit"/> 的工程值</param>
public readonly record struct PointSample(string Key, double Value);
