using System.Buffers.Binary;
using System.Globalization;
using GJ.Drivers.Common;
using Solve.Device.Sdk;

namespace GJ.Drivers.Da750;

/// <summary>
/// 负载模式（设定数据每通道第 1 字节）。协议另有 CP = 2、CR = 3、LED = 5、MTK_PE = 6，本驱动不提供：
/// 旧代码中 DA750-4-60A 的 CP / CR 倍率与同系列其他型号互相矛盾（CP ×100 时 750 W 会超出 16 位），没有手册依据。
/// </summary>
internal enum Da750Mode : byte
{
    CcSlow = 0,
    Cv = 1,
    CcFast = 4,
}

/// <summary>
/// 一路通道的设定（协议原始值）
/// </summary>
/// <param name="Mode">负载模式</param>
/// <param name="Value">负载值原始值：CC 为电流 × <see cref="Da750Model.CurrentScale"/>；CV 为电压 × 20</param>
/// <param name="Extra">模式附加参数：CC 为启动延迟 × 10（s）；CV 固定为 0</param>
internal readonly record struct Da750Setting(Da750Mode Mode, ushort Value, byte Extra)
{
    /// <summary>
    /// 卸载：CC 慢速、0 A。旧上位机的设定模板即以此作为"不带载"
    /// </summary>
    public static Da750Setting Unloaded { get; } = new(Da750Mode.CcSlow, 0, 0);
}

/// <summary>
/// DA750 的报文内容（纯计算，不做 I/O）。帧格式见 <see cref="Ers272Frame"/>。
/// </summary>
/// <remarks>
/// <para>依据：旧上位机公共库 GJ.DEV/Load/DA750_4_60A.cs（DA750-4-60A 实际使用的 CID 与倍率）
/// 与 GJ.DEV/LED/CDA_750_4.cs（同系列固件的字段注释）。没有厂家手册。</para>
/// <para>固件按 8 个通道槽组织数据：读测量值返回 8 槽 × 8 字节 + 1 字节逆变器状态，写设定要 8 槽 × 6 字节。
/// DA750-4 只有 4 路，旧驱动读取时只解析前 4 槽，写入时把 4 路数据重复一次填满 8 槽，本驱动照此执行。</para>
/// <para>所有多字节字段均为大端（高字节在前）；读数按有符号 16 位解析（与旧驱动 Convert.ToInt16 一致），设定值为无符号 16 位。</para>
/// </remarks>
internal static class Da750Protocol
{
    /// <summary>
    /// 读测量值。DA750-4-60A 的旧驱动用 0F；同系列 LED 机型用 07，型号不同 CID 也不同
    /// </summary>
    public const byte ReadValues = 0x0F;

    /// <summary>
    /// 写全部通道设定且不保存到 EEPROM（40 为写入并保存）。设定会频繁改变，不写 EEPROM，避免擦写寿命耗尽；
    /// 代价是设备掉电重启后回到 EEPROM 中保存的设定，见对接文档的实机待确认项
    /// </summary>
    public const byte WriteSettings = 0x41;

    public const int SlotCount = 8;
    public const int ValueSlotLength = 8;
    public const int SettingSlotLength = 6;

    /// <summary>
    /// 读测量值的数据区：8 槽 × 8 字节 + 逆变器状态 1 字节
    /// </summary>
    public const int ValuesInfoLength = SlotCount * ValueSlotLength + 1;

    /// <summary>
    /// 写设定的数据区：8 槽 × 6 字节
    /// </summary>
    public const int SettingsInfoLength = SlotCount * SettingSlotLength;

    /// <summary>
    /// 电压倍率：读数、Von、CV 设定值的原始值 = 电压（V）× 20（0.05 V / LSB）
    /// </summary>
    public const double VoltageScale = 20;

    /// <summary>
    /// CC 启动延迟倍率：原始值 = 延迟（s）× 10，取值 0–20 s（CDA_750_4 注释）
    /// </summary>
    public const double DelayScale = 10;

    /// <summary>
    /// 读数的合理范围：额定值的 -5% 到 +20%。下限容纳零点偏移，上限容纳过冲；超出说明数据已损坏，不能上报
    /// </summary>
    private const double PlausibleLow = -0.05;
    private const double PlausibleHigh = 1.20;

    // ---------- 通道状态字（每槽第 7、8 字节）----------

    /// <summary>
    /// Bit 0：DC-DC 开启（通道正在带载）
    /// </summary>
    private const int InputOnBit = 0;

    private static readonly (int Bit, string Name)[] ChannelFaultBits =
    [
        (1, "LLC 过压"),
        (4, "超功率保护"),
        (5, "过温保护"),
        (8, "过流保护（OCP1）"),
        (9, "过流保护（OCP2）"),
    ];

    /// <summary>
    /// 两位的电压状态字段：0 欠压、1 正常、2 过压（3 未定义）
    /// </summary>
    private static readonly (int Shift, string Name)[] ChannelVoltageFields =
    [
        (2, "输入电压"),
        (6, "直流母线"),
    ];

    // ---------- 逆变器状态（数据区最后 1 字节）----------

    private static readonly (int Bit, string Name)[] InverterFaultBits =
    [
        (0, "过温"),
        (1, "AD 采样故障"),
        (4, "风扇故障"),
        (5, "超时故障"),
    ];

    private static readonly (int Shift, string Name)[] InverterVoltageFields =
    [
        (2, "电网电压"),
        (6, "直流母线"),
    ];

    public static byte[] ReadValuesRequest(int address) => Ers272Frame.EncodeRequest(address, ReadValues, []);

    /// <summary>
    /// 写全部通道设定。<paramref name="channels"/> 为各路设定（通道 1 起），按旧驱动的做法重复填满 8 槽
    /// </summary>
    /// <param name="vonRaw">Von 原始值（电压 × 20），各通道相同</param>
    public static byte[] WriteSettingsRequest(int address, IReadOnlyList<Da750Setting> channels, ushort vonRaw)
    {
        var info = new byte[SettingsInfoLength];
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var setting = channels[slot % channels.Count];
            var field = info.AsSpan(slot * SettingSlotLength, SettingSlotLength);
            field[0] = (byte)setting.Mode;
            BinaryPrimitives.WriteUInt16BigEndian(field[1..3], vonRaw);
            BinaryPrimitives.WriteUInt16BigEndian(field[3..5], setting.Value);
            field[5] = setting.Extra;
        }

        return Ers272Frame.EncodeRequest(address, WriteSettings, info);
    }

    /// <summary>
    /// 工程值转原始值（四舍五入）。调用方已按声明范围校验，结果不会超出 16 位
    /// </summary>
    public static ushort ToRaw(double value, double scale) => checked((ushort)Math.Round(value * scale, MidpointRounding.AwayFromZero));

    /// <summary>
    /// 解析读测量值的数据区：合理性检查 → 保护与报警 → 点位样本
    /// </summary>
    /// <param name="undervoltageIsFault">两位电压状态字段为"欠压"时是否报警（模块参数，见驱动说明）</param>
    public static ExchangeStep DecodeValues(ReadOnlySpan<byte> info, Da750Model model, bool undervoltageIsFault)
    {
        var samples = new List<PointSample>(model.Channels * 5 + 1);
        var faults = new List<string>();
        for (var channel = 1; channel <= model.Channels; channel++)
        {
            var slot = info.Slice((channel - 1) * ValueSlotLength, ValueSlotLength);

            // 每槽：电压(2) 电流(2) LLC 输入电压(2) 状态字(2)，大端；LLC 电压没有可靠的合理范围依据，暂不上报
            var voltage = BinaryPrimitives.ReadInt16BigEndian(slot[..2]) / VoltageScale;
            var current = BinaryPrimitives.ReadInt16BigEndian(slot[2..4]) / model.CurrentScale;
            var status = BinaryPrimitives.ReadUInt16BigEndian(slot[6..8]);

            if (CheckPlausible($"通道 {channel} 电压", "V", voltage, model.RatedVoltage) is { } badVoltage)
                return badVoltage;
            if (CheckPlausible($"通道 {channel} 电流", "A", current, model.RatedCurrent) is { } badCurrent)
                return badCurrent;

            var channelFaults = DescribeFaults(status, ChannelFaultBits, ChannelVoltageFields, undervoltageIsFault);
            if (channelFaults.Count > 0)
                faults.Add($"通道 {channel}：{string.Join("、", channelFaults)}（状态字 0x{status:X4}）");

            samples.Add(new PointSample(Da750Driver.VoltageKey, voltage) { Channel = channel });
            samples.Add(new PointSample(Da750Driver.CurrentKey, current) { Channel = channel });

            // 设备没有功率读数，由同一帧的电压 × 电流算出
            samples.Add(new PointSample(Da750Driver.PowerKey, voltage * current) { Channel = channel });
            samples.Add(new PointSample(Da750Driver.InputKey, (status & (1 << InputOnBit)) != 0 ? 1 : 0) { Channel = channel });
            samples.Add(new PointSample(Da750Driver.StatusWordKey, status) { Channel = channel });
        }

        var inverter = info[SlotCount * ValueSlotLength];
        var inverterFaults = DescribeFaults(inverter, InverterFaultBits, InverterVoltageFields, undervoltageIsFault);
        if (inverterFaults.Count > 0)
            faults.Add($"逆变器：{string.Join("、", inverterFaults)}（状态 0x{inverter:X2}）");

        // 设备自己报告的保护或故障：转为设备故障，由内核报警并按较长间隔继续观察恢复
        if (faults.Count > 0)
            return new ExchangeStep.Failed($"电子负载报警：{string.Join("；", faults)}。", FailureKind.Device);

        samples.Add(new PointSample(Da750Driver.InverterStatusKey, inverter));
        return new ExchangeStep.Completed(samples);
    }

    private static ExchangeStep.Failed? CheckPlausible(string name, string unit, double value, double rated)
    {
        var low = rated * PlausibleLow;
        var high = rated * PlausibleHigh;
        if (value >= low && value <= high)
            return null;

        return new ExchangeStep.Failed(
            $"{name}读数 {Format(value)} {unit} 超出合理范围 [{Format(low)}, {Format(high)}] {unit}，数据可能已损坏。",
            FailureKind.Transient);
    }

    private static List<string> DescribeFaults(int status, (int Bit, string Name)[] bits, (int Shift, string Name)[] voltageFields, bool undervoltageIsFault)
    {
        var names = bits.Where(b => (status & (1 << b.Bit)) != 0).Select(b => b.Name).ToList();
        foreach (var (shift, name) in voltageFields)
        {
            // 旧代码把"过压"写成与 0x10 比较（永远不成立），这里按两位字段的值 2 判断
            switch ((status >> shift) & 0b11)
            {
                case 0 when undervoltageIsFault:
                    names.Add($"{name}欠压");
                    break;
                case 2:
                    names.Add($"{name}过压");
                    break;
                case 3:
                    names.Add($"{name}状态无效");
                    break;
            }
        }

        return names;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
