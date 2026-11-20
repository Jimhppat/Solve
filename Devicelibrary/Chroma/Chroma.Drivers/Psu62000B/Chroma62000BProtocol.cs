using System.Globalization;
using Chroma.Drivers.Common;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 62000B 状态查询（FETC:STAT?）的结果
/// </summary>
/// <param name="Flags">状态字</param>
/// <param name="Alarms">报警字，0 表示无报警</param>
internal readonly record struct Chroma62000BStatus(int Flags, int Alarms)
{
    /// <summary>
    /// 状态字 Bit 12：电压输出正常
    /// </summary>
    public bool VoltageOk => (Flags & (1 << 12)) != 0;

    /// <summary>
    /// 状态字 Bit 13：已发送 CONF:OUTP ON 或面板开关处于 ON
    /// </summary>
    public bool OutputOn => (Flags & (1 << 13)) != 0;
}

/// <summary>
/// 62000B 的 SCPI 命令与响应解析（纯计算，不做 I/O）。
/// 依据：Chroma 62000B Series Operation Manual v1.5（2013-07）第 4 章。
/// </summary>
internal static class Chroma62000BProtocol
{
    public const string Identify = "*IDN?";
    public const string FetchVoltage = "FETC:VOLT?";
    public const string FetchCurrent = "FETC:CURR?";
    public const string FetchStatus = "FETC:STAT?";
    public const string OutputOn = "CONF:OUTP ON";
    public const string OutputOff = "CONF:OUTP OFF";
    public const string QueryOutput = "CONF:OUTP?";

    private const string Manufacturer = "CHROMA";

    /// <summary>
    /// 读数的合理范围：额定值的 -5% 到 +20%。
    /// 下限留一点负值容纳零点偏移；上限覆盖 OVP 可调上限（15 V 机型为 16 V）与过冲。超出说明数据已损坏或设备异常，不能上报。
    /// </summary>
    private const double PlausibleLow = -0.05;
    private const double PlausibleHigh = 1.20;

    /// <summary>
    /// 报警字各位的含义（手册 4.3 FETCh:STATus?）。
    /// 手册中该图排版错位，位号与名称的对应需在实机上核对。
    /// </summary>
    private static readonly (int Bit, string Name)[] AlarmBits =
    [
        (0, "模块风扇故障"),
        (1, "机框交流输入故障"),
        (2, "模块过温保护（硬件）"),
        (3, "模块过温保护（软件）"),
        (4, "过流保护（CC 模式）"),
        (5, "模块过压保护（硬件）"),
        (6, "模块过压保护（软件）"),
        (7, "过流关断（CV 模式）"),
        (15, "蜂鸣报警"),
    ];

    /// <summary>
    /// 检查 *IDN? 的响应。手册示例为 "CHROMA 62015B-15-90,01.00,2005/07/14"（厂家与型号以空格分隔），
    /// 也兼容标准 SCPI 的 "CHROMA,62015B-15-90,..." 写法。
    /// </summary>
    /// <returns>匹配时为 null；否则为失败原因与分类</returns>
    public static ExchangeStep.Failed? CheckIdentity(string response, Chroma62000BModel expected)
    {
        var fields = response.Split(',', StringSplitOptions.TrimEntries);
        var head = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string manufacturer, model;
        if (head.Length >= 2)
        {
            // 手册写法："CHROMA 62015B-15-90,..."
            (manufacturer, model) = (head[0], head[1]);
        }
        else
        {
            // 标准 SCPI 写法："CHROMA,62015B-15-90,..."
            manufacturer = head.Length == 1 ? head[0] : string.Empty;
            model = fields.Length >= 2 ? fields[1] : string.Empty;
        }

        if (string.Equals(manufacturer, Manufacturer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model, expected.Model, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 至少有"厂家 型号,版本"这样的结构才算设备确实回答了另一个型号：这是配置问题，重试没用；
        // 结构都不对时更可能是干扰，按暂时性错误重试
        var wellFormed = fields.Length >= 2 && manufacturer.Length > 0 && model.Length > 0;
        return wellFormed
            ? new ExchangeStep.Failed($"设备型号不符：期望 CHROMA {expected.Model}，实际为「{response}」。", FailureKind.Configuration)
            : new ExchangeStep.Failed($"型号查询的响应无法识别：「{response}」。", FailureKind.Transient);
    }

    /// <summary>
    /// 解析一个测量值并检查是否在合理范围内
    /// </summary>
    /// <param name="name">点位显示名，用于失败原因</param>
    /// <param name="unit">单位</param>
    /// <param name="rated">该量的额定值</param>
    public static ExchangeStep.Failed? TryReading(string response, string name, string unit, double rated, out double value)
    {
        if (!ScpiText.TryParseNumber(response, out value))
            return new ExchangeStep.Failed($"{name}的响应不是数值：「{response}」。", FailureKind.Transient);

        var low = rated * PlausibleLow;
        var high = rated * PlausibleHigh;
        if (value < low || value > high)
        {
            return new ExchangeStep.Failed(
                $"{name}读数 {value.ToString(CultureInfo.InvariantCulture)} {unit} 超出合理范围 [{low.ToString(CultureInfo.InvariantCulture)}, {high.ToString(CultureInfo.InvariantCulture)}] {unit}。",
                FailureKind.Transient);
        }

        return null;
    }

    /// <summary>
    /// 解析 FETC:STAT? 的响应 "状态字, 报警字"，如 "4096, 0"
    /// </summary>
    public static bool TryParseStatus(string response, out Chroma62000BStatus status)
    {
        status = default;
        var parts = response.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var alarms)
            || flags > ushort.MaxValue
            || alarms > ushort.MaxValue)
        {
            return false;
        }

        status = new Chroma62000BStatus(flags, alarms);
        return true;
    }

    /// <summary>
    /// 报警字转成面向操作员的文字，如"模块过压保护（硬件）、蜂鸣报警（报警字 0x8020）"
    /// </summary>
    public static string DescribeAlarms(int alarms)
    {
        var names = AlarmBits.Where(b => (alarms & (1 << b.Bit)) != 0).Select(b => b.Name).ToList();
        var known = AlarmBits.Aggregate(0, (mask, b) => mask | (1 << b.Bit));
        if ((alarms & ~known) != 0)
            names.Add("未知报警位");

        return $"{string.Join("、", names)}（报警字 0x{alarms:X4}）";
    }

    /// <summary>
    /// 解析 CONF:OUTP? 的响应（手册：ON 或 OFF）
    /// </summary>
    public static bool TryParseOutput(string response, out bool on)
    {
        on = string.Equals(response, "ON", StringComparison.OrdinalIgnoreCase);
        return on || string.Equals(response, "OFF", StringComparison.OrdinalIgnoreCase);
    }
}
