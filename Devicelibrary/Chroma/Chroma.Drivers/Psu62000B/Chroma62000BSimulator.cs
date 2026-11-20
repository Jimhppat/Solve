using System.Globalization;
using Chroma.Drivers.Common;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 62000B 的仿真设备：按本驱动用到的 SCPI 命令应答，只在 Solve 的仿真模式下使用。
/// </summary>
/// <remarks>
/// <para>输出打开时读数为额定值的 <see cref="VoltageRatio"/> / <see cref="CurrentRatio"/>，关闭时为 0；
/// 状态字按输出状态置 Bit 12（电压输出正常）与 Bit 13（输出已打开），报警字恒为 0。上电默认输出关闭。</para>
/// <para>设置命令（CONF:OUTP ON|OFF）按手册不应答；不认识的命令也不应答（真实设备会把它记入错误队列，而不是回一行）。</para>
/// <para>字节流封装里没有模块地址（见 <see cref="Chroma62000BDriver"/>），每条总线只挂一台，所以本仿真设备应答总线上的全部 SCPI 请求。</para>
/// </remarks>
internal sealed class Chroma62000BSimulator(Chroma62000BModel model) : ISimulatedDevice
{
    /// <summary>
    /// 输出打开时的仿真电压占额定电压的比例（62015B-15-90 为 12 V）
    /// </summary>
    public const double VoltageRatio = 0.8;

    /// <summary>
    /// 输出打开时的仿真电流占额定电流的比例（62015B-15-90 为 9 A）
    /// </summary>
    public const double CurrentRatio = 0.1;

    /// <summary>
    /// *IDN? 的版本与日期字段：明确标出是仿真设备，日志里一眼能和实机区分
    /// </summary>
    private const string IdentityTail = "SIM,0000/00/00";

    public bool OutputOn { get; private set; }

    public byte[]? Respond(ReadOnlySpan<byte> request)
    {
        // 只认一整行命令：不是完整的可打印 ASCII 行时不是发给本设备的 SCPI 请求
        if (ScpiText.MatchLine(request, out var length) != FrameStatus.Complete || length != request.Length)
            return null;

        var command = ScpiText.Decode(request);
        if (Is(command, Chroma62000BProtocol.Identify))
            return Reply($"CHROMA {model.Model},{IdentityTail}");

        if (Is(command, Chroma62000BProtocol.FetchVoltage))
            return Reply(Number(OutputOn ? model.RatedVoltage * VoltageRatio : 0));

        if (Is(command, Chroma62000BProtocol.FetchCurrent))
            return Reply(Number(OutputOn ? model.RatedCurrent * CurrentRatio : 0));

        if (Is(command, Chroma62000BProtocol.FetchStatus))
        {
            var flags = OutputOn ? (1 << 12) | (1 << 13) : 0;
            return Reply(FormattableString.Invariant($"{flags}, 0"));
        }

        if (Is(command, Chroma62000BProtocol.QueryOutput))
            return Reply(OutputOn ? "ON" : "OFF");

        if (Is(command, Chroma62000BProtocol.OutputOn))
            OutputOn = true;
        else if (Is(command, Chroma62000BProtocol.OutputOff))
            OutputOn = false;

        return null;
    }

    // SCPI 关键字不区分大小写
    private static bool Is(string command, string expected) => string.Equals(command, expected, StringComparison.OrdinalIgnoreCase);

    // 手册：数值响应为 NR2，如 "8.12"
    private static string Number(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static byte[] Reply(string line) => ScpiText.Encode(line);
}
