using Chroma.Drivers.Common;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 一次轮询：[*IDN?] → FETC:VOLT? → FETC:CURR? → FETC:STAT?。
/// 每条查询单独一问一答：手册要求读完上一条响应才能发下一条，否则剩余响应丢失并产生 query-interrupted 错误；
/// 手册也没有说明用分号合并多条查询时的响应格式，所以不合并。
/// </summary>
internal sealed class PollExchange(Chroma62000BModel model, bool verifyIdentity) : IChromaExchange
{
    private enum Stage
    {
        Identify,
        Voltage,
        Current,
        Status,
        Finished,
    }

    private Stage _stage = verifyIdentity ? Stage.Identify : Stage.Voltage;
    private double _voltage;
    private double _current;

    public bool Succeeded { get; private set; }

    public ExchangeStep Start() => QueryFor(_stage);

    public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength) => ScpiText.MatchLine(received, out frameLength);

    public ExchangeStep OnFrame(ReadOnlySpan<byte> frame)
    {
        var text = ScpiText.Decode(frame);
        switch (_stage)
        {
            case Stage.Identify:
                return Chroma62000BProtocol.CheckIdentity(text, model) ?? Advance(Stage.Voltage);

            case Stage.Voltage:
                return Chroma62000BProtocol.TryReading(text, "输出电压", "V", model.RatedVoltage, out _voltage)
                       ?? Advance(Stage.Current);

            case Stage.Current:
                return Chroma62000BProtocol.TryReading(text, "输出电流", "A", model.RatedCurrent, out _current)
                       ?? Advance(Stage.Status);

            case Stage.Status:
                return Finish(text);

            default:
                return new ExchangeStep.Failed("轮询已结束，却又收到了响应。", FailureKind.Configuration);
        }
    }

    private ExchangeStep Finish(string text)
    {
        _stage = Stage.Finished;
        if (!Chroma62000BProtocol.TryParseStatus(text, out var status))
            return new ExchangeStep.Failed($"状态查询的响应无法识别：「{text}」。", FailureKind.Transient);

        // 设备自己报告的保护或故障：转为设备故障，由内核报警并按较长间隔继续观察恢复
        if (status.Alarms != 0)
            return new ExchangeStep.Failed($"电源报警：{Chroma62000BProtocol.DescribeAlarms(status.Alarms)}。", FailureKind.Device);

        Succeeded = true;
        return new ExchangeStep.Completed(
        [
            new PointSample(Chroma62000BDriver.VoltageKey, _voltage),
            new PointSample(Chroma62000BDriver.CurrentKey, _current),
            new PointSample(Chroma62000BDriver.PowerKey, _voltage * _current),
            new PointSample(Chroma62000BDriver.OutputKey, status.OutputOn ? 1 : 0),
        ]);
    }

    private ExchangeStep Advance(Stage next)
    {
        _stage = next;
        return QueryFor(next);
    }

    private static ExchangeStep QueryFor(Stage stage) => stage switch
    {
        Stage.Identify => ScpiText.Query(Chroma62000BProtocol.Identify),
        Stage.Voltage => ScpiText.Query(Chroma62000BProtocol.FetchVoltage),
        Stage.Current => ScpiText.Query(Chroma62000BProtocol.FetchCurrent),
        Stage.Status => ScpiText.Query(Chroma62000BProtocol.FetchStatus),
        _ => new ExchangeStep.Failed("轮询状态无效。", FailureKind.Configuration),
    };
}
