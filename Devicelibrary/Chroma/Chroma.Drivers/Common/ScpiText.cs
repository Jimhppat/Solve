using System.Globalization;
using System.Text;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Common;

/// <summary>
/// SCPI 文本行的编解码：ASCII，命令与响应都以 LF（0x0A）结尾。
/// </summary>
/// <remarks>
/// 依据 Chroma 62000B 操作手册 4.1–4.2：命令与响应均为 ASCII，新命令行为单字节 0Ah，响应以 0Ah 结束，
/// 数值响应为 NR2（如 "8.12"）。手册没有提到 CR，这里容忍行尾多出的 CR，不主动发送。
/// </remarks>
internal static class ScpiText
{
    public const byte LineFeed = 0x0A;
    private const byte CarriageReturn = 0x0D;

    /// <summary>
    /// 一行响应的最大字节数（含 LF）。手册没有给上限；本驱动最长的响应是 *IDN?（约 40 字节），
    /// 留足余量后超过仍无 LF 即判无效，免得把线路噪声一直当作"还没收完"。
    /// </summary>
    public const int MaxLineLength = 128;

    /// <summary>
    /// 编码一条命令（自动补 LF）。命令文本由驱动代码写定，含非可打印字符属于编程错误，直接抛出。
    /// </summary>
    public static byte[] Encode(string command)
    {
        var bytes = new byte[command.Length + 1];
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is < ' ' or > '~')
                throw new ArgumentException($"SCPI 命令只能含可打印 ASCII 字符：{command}", nameof(command));
            bytes[i] = (byte)c;
        }

        bytes[^1] = LineFeed;
        return bytes;
    }

    /// <summary>
    /// 按 LF 拆出一行。LF 之后已收到的字节由内核丢弃并记入追溯日志。
    /// </summary>
    public static FrameStatus MatchLine(ReadOnlySpan<byte> received, out int frameLength)
    {
        frameLength = 0;
        var end = received.IndexOf(LineFeed);
        var line = end < 0 ? received : received[..end];

        // 62000B 的响应只含可打印 ASCII；出现其他字节说明是干扰或串入了别的数据，等下去也不会成为有效响应
        foreach (var b in line)
        {
            if (b is not (>= 0x20 and <= 0x7E or CarriageReturn))
                return FrameStatus.Invalid;
        }

        if (end < 0)
            return received.Length >= MaxLineLength ? FrameStatus.Invalid : FrameStatus.NeedMoreData;

        if (end + 1 > MaxLineLength)
            return FrameStatus.Invalid;

        frameLength = end + 1;
        return FrameStatus.Complete;
    }

    /// <summary>
    /// 一帧（已通过 <see cref="MatchLine"/>，只含可打印 ASCII）转成去掉首尾空白与 CR/LF 的文本
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> frame) => Encoding.ASCII.GetString(frame).Trim();

    /// <summary>
    /// 解析 NR1 / NR2 / NR3 数值（不变区域性，小数点为 '.'），拒绝 NaN 与无穷
    /// </summary>
    public static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static ExchangeStep.Send Query(string command) => new(Encode(command));
}
