using Solve.Device.Sdk;

namespace GJ.Drivers.Common;

/// <summary>
/// 处理一帧正常应答（应答码 F0、数据区长度已核对）的数据区，给出交互结果
/// </summary>
internal delegate ExchangeStep Ers272InfoHandler(ReadOnlySpan<byte> info);

/// <summary>
/// 一问一答的 ERS272 交互：发送一帧请求 → 成帧 → 核对应答码与数据区长度 → 交给 <see cref="Ers272InfoHandler"/>。
/// </summary>
/// <param name="request">完整请求帧</param>
/// <param name="address">设备地址，应答的 ADR 必须相同</param>
/// <param name="infoLength">正常应答的数据区长度</param>
/// <param name="strict">核对 LEN 与校验和，见 <see cref="Ers272Frame.Match"/></param>
/// <param name="action">面向操作员的动作名，用于失败原因，如"读取测量值"</param>
internal sealed class Ers272Exchange(
    byte[] request,
    int address,
    int infoLength,
    bool strict,
    string action,
    Ers272InfoHandler onInfo) : IExchange
{
    private enum Stage
    {
        Request,
        Finished,
    }

    private Stage _stage = Stage.Request;

    public ExchangeStep Start() => _stage == Stage.Request
        ? new ExchangeStep.Send(request)
        : new ExchangeStep.Failed($"{action}已结束，不能重新开始。", FailureKind.Configuration);

    public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
        => Ers272Frame.Match(received, address, infoLength, strict, out frameLength);

    public ExchangeStep OnFrame(ReadOnlySpan<byte> frame)
    {
        if (_stage != Stage.Request)
            return new ExchangeStep.Failed($"{action}已结束，却又收到了响应。", FailureKind.Configuration);

        _stage = Stage.Finished;
        var code = Ers272Frame.CodeOf(frame);
        if (code != Ers272Code.Ok)
            return Ers272Frame.DescribeError(code, action);

        var info = Ers272Frame.InfoOf(frame);
        if (info.Length != infoLength)
            return new ExchangeStep.Failed($"{action}：应答数据区为 {info.Length} 字节，应为 {infoLength} 字节。", FailureKind.Transient);

        return onInfo(info);
    }
}
