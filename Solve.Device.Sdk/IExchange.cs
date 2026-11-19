namespace Solve.Device.Sdk;

/// <summary>
/// 一次报文交互（一次轮询或一条命令），由内核逐步推进的小状态机：
/// <code>
/// step = exchange.Start();
/// while (step is Send send)
/// {
///     内核：清空接收缓冲 → 发送 send.Request → 若不等待响应，step = exchange.OnFrame(空)；否则在超时内接收，
///           每收到一块字节就调用 Match，直到 Complete（取出完整一帧）或 Invalid / 超时（交互失败）；
///     step = exchange.OnFrame(帧);
/// }
/// // step 为 Completed 或 Failed，交互结束
/// </code>
/// </summary>
/// <remarks>
/// 实现要求：所有方法都是纯计算，必须很快返回（毫秒级以内），不得阻塞、等待或访问外部资源。
/// 可预期的问题（校验错误、设备返回异常码、数值越界）用 <see cref="ExchangeStep.Failed"/> 表达，不要抛异常；
/// 抛出的异常会被视为通信状态未知，内核会对整条总线执行安全停止。
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
    /// 收到的数据不可能构成有效响应（如 CRC 错误、地址不符），本次交互失败
    /// </summary>
    Invalid = 3,
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
    public sealed record Send(ReadOnlyMemory<byte> Request, bool ExpectsResponse = true, TimeSpan? ResponseTimeout = null) : ExchangeStep;

    /// <summary>
    /// 交互成功结束。命令交互通常没有点位值，传空列表。
    /// </summary>
    public sealed record Completed(IReadOnlyList<PointSample> Samples) : ExchangeStep;

    /// <summary>
    /// 交互失败（可预期的问题）。原因会显示给操作员并写入日志。
    /// </summary>
    public sealed record Failed(string Reason) : ExchangeStep;
}

/// <summary>
/// 一个点位的数值
/// </summary>
/// <param name="Key">对应 <see cref="PointInfo.Key"/></param>
/// <param name="Value">已换算到 <see cref="PointInfo.Unit"/> 的工程值</param>
public readonly record struct PointSample(string Key, double Value);
