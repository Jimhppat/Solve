using Solve.Device.Sdk;

namespace Chroma.Drivers.Common;

/// <summary>
/// 第一步就失败的交互（如不支持的命令），不会向总线发送任何字节
/// </summary>
internal sealed class FailedExchange(string reason, FailureKind kind) : IExchange
{
    public ExchangeStep Start() => new ExchangeStep.Failed(reason, kind);

    public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
    {
        frameLength = 0;
        return FrameStatus.Invalid;
    }

    public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => new ExchangeStep.Failed(reason, kind);
}
