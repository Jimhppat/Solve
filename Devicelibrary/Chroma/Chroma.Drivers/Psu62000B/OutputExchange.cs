using Chroma.Drivers.Common;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 打开 / 关闭输出：[*IDN?] → CONF:OUTP ON|OFF（设置命令，设备不应答）→ 等待 → CONF:OUTP? 回读确认。
/// </summary>
/// <remarks>
/// 设置命令没有应答，设备是否真的执行了（远程未生效、命令被拒）只能靠回读判断；
/// 回读与命令不符时报设备故障，让操作员看到"命令没有生效"，而不是默认已经成功。
/// </remarks>
internal sealed class OutputExchange(Chroma62000BModel model, bool on, TimeSpan commandDelay, bool verifyIdentity) : IChromaExchange
{
    private enum Stage
    {
        Identify,
        Switch,
        Verify,
        Finished,
    }

    private Stage _stage = verifyIdentity ? Stage.Identify : Stage.Switch;

    public bool Succeeded { get; private set; }

    public ExchangeStep Start() => StepFor(_stage);

    public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength) => ScpiText.MatchLine(received, out frameLength);

    public ExchangeStep OnFrame(ReadOnlySpan<byte> frame)
    {
        switch (_stage)
        {
            case Stage.Identify:
                return Chroma62000BProtocol.CheckIdentity(ScpiText.Decode(frame), model) ?? Advance(Stage.Switch);

            case Stage.Switch:
                return Advance(Stage.Verify);

            case Stage.Verify:
                return Finish(ScpiText.Decode(frame));

            default:
                return new ExchangeStep.Failed("命令已结束，却又收到了响应。", FailureKind.Configuration);
        }
    }

    private ExchangeStep Finish(string text)
    {
        _stage = Stage.Finished;
        if (!Chroma62000BProtocol.TryParseOutput(text, out var actual))
            return new ExchangeStep.Failed($"输出状态回读无法识别：「{text}」。", FailureKind.Transient);

        if (actual != on)
            return new ExchangeStep.Failed($"{(on ? "打开" : "关闭")}输出后回读为 {text}，命令没有生效。", FailureKind.Device);

        Succeeded = true;
        return new ExchangeStep.Completed([]);
    }

    private ExchangeStep Advance(Stage next)
    {
        _stage = next;
        return StepFor(next);
    }

    private ExchangeStep StepFor(Stage stage) => stage switch
    {
        Stage.Identify => ScpiText.Query(Chroma62000BProtocol.Identify),
        Stage.Switch => new ExchangeStep.Send(
            ScpiText.Encode(on ? Chroma62000BProtocol.OutputOn : Chroma62000BProtocol.OutputOff),
            ExpectsResponse: false)
        {
            DelayAfter = commandDelay,
        },
        Stage.Verify => ScpiText.Query(Chroma62000BProtocol.QueryOutput),
        _ => new ExchangeStep.Failed("命令状态无效。", FailureKind.Configuration),
    };
}
