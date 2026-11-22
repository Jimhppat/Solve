using System.Buffers.Binary;
using GJ.Drivers.Common;
using Solve.Device.Sdk;

namespace GJ.Drivers.Da750;

/// <summary>
/// DA750 的仿真设备：按 ERS272 协议应答读测量值（0F）与写设定（41），只在 Solve 的仿真模式下使用。
/// </summary>
/// <remarks>
/// <para>被测产品按一台 <see cref="SourceVoltage"/> 的恒压源仿真：CC 带载时电流等于设定值；CV 设定低于源电压时
/// 电压等于设定值、电流为 <see cref="CvCurrent"/>，否则不带载；源电压低于 Von 时不带载。上电默认全部卸载。</para>
/// <para>状态字只置"输入电压正常、直流母线正常"与带载位；逆变器状态为"电网正常、母线正常"。没有任何保护位。</para>
/// <para>与实机一样：地址不是自己的不应答；校验和错应答 F1、长度错应答 F2、不认识的 CID 应答 F3、模式无效应答 F4。
/// 本驱动不使用身份查询，报文里没有可以标出"仿真"的字段；仿真与实机由内核在命令日志的"模式"字段区分。</para>
/// </remarks>
internal sealed class Da750Simulator(Da750Model model, int address) : ISimulatedDevice
{
    /// <summary>
    /// 仿真被测产品的输出电压
    /// </summary>
    public const double SourceVoltage = 12;

    /// <summary>
    /// CV 带载时仿真被测产品的输出电流
    /// </summary>
    public const double CvCurrent = 5;

    /// <summary>
    /// 状态字：输入电压正常（Bit 2–3 = 01）、直流母线正常（Bit 6–7 = 01）
    /// </summary>
    private const ushort NormalChannelStatus = 0x0044;

    /// <summary>
    /// 逆变器：电网电压正常（Bit 2–3 = 01）、直流母线正常（Bit 6–7 = 01）
    /// </summary>
    private const byte NormalInverterStatus = 0x44;

    private readonly Da750Setting[] _settings = Enumerable.Repeat(Da750Setting.Unloaded, Da750Protocol.SlotCount).ToArray();
    private readonly ushort[] _vonRaw = new ushort[Da750Protocol.SlotCount];

    public byte[]? Respond(ReadOnlySpan<byte> request)
    {
        if (!Ers272Frame.TryParseRequest(request, out var target, out var command, out var info, out var checksumOk) || target != address)
            return null;
        if (!checksumOk)
            return Reply(Ers272Code.ChecksumError);

        return command switch
        {
            Da750Protocol.ReadValues => info.Length == 0 ? Reply(Ers272Code.Ok, Values()) : Reply(Ers272Code.LengthError),
            Da750Protocol.WriteSettings => Write(info),
            _ => Reply(Ers272Code.InvalidCommand),
        };
    }

    private byte[] Write(byte[] info)
    {
        if (info.Length != Da750Protocol.SettingsInfoLength)
            return Reply(Ers272Code.LengthError);

        var settings = new Da750Setting[Da750Protocol.SlotCount];
        var von = new ushort[Da750Protocol.SlotCount];
        for (var slot = 0; slot < Da750Protocol.SlotCount; slot++)
        {
            var field = info.AsSpan(slot * Da750Protocol.SettingSlotLength, Da750Protocol.SettingSlotLength);
            if (!Enum.IsDefined((Da750Mode)field[0]))
                return Reply(Ers272Code.InvalidData);

            von[slot] = BinaryPrimitives.ReadUInt16BigEndian(field[1..3]);
            settings[slot] = new Da750Setting((Da750Mode)field[0], BinaryPrimitives.ReadUInt16BigEndian(field[3..5]), field[5]);
        }

        // 全部校验通过才生效，与设备"整帧接受或整帧拒绝"一致
        settings.CopyTo(_settings, 0);
        von.CopyTo(_vonRaw, 0);
        return Reply(Ers272Code.Ok);
    }

    private byte[] Values()
    {
        var info = new byte[Da750Protocol.ValuesInfoLength];
        for (var slot = 0; slot < model.Channels; slot++)
        {
            var (voltage, current) = Measure(_settings[slot], _vonRaw[slot] / Da750Protocol.VoltageScale);
            var on = current > 0;
            var field = info.AsSpan(slot * Da750Protocol.ValueSlotLength, Da750Protocol.ValueSlotLength);
            BinaryPrimitives.WriteInt16BigEndian(field[..2], (short)Math.Round(voltage * Da750Protocol.VoltageScale));
            BinaryPrimitives.WriteInt16BigEndian(field[2..4], (short)Math.Round(current * model.CurrentScale));
            BinaryPrimitives.WriteInt16BigEndian(field[4..6], 0);
            BinaryPrimitives.WriteUInt16BigEndian(field[6..8], (ushort)(NormalChannelStatus | (on ? 1 : 0)));
        }

        // 4 路机型的第 5–8 槽驱动不解析；实机空槽的内容未知，这里只给"正常"状态字
        for (var slot = model.Channels; slot < Da750Protocol.SlotCount; slot++)
            BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(slot * Da750Protocol.ValueSlotLength + 6, 2), NormalChannelStatus);

        info[^1] = NormalInverterStatus;
        return info;
    }

    private (double Voltage, double Current) Measure(Da750Setting setting, double von)
    {
        if (SourceVoltage < von)
            return (SourceVoltage, 0);

        return setting.Mode switch
        {
            Da750Mode.CcSlow or Da750Mode.CcFast => (SourceVoltage, Math.Min(setting.Value / model.CurrentScale, model.RatedPower / SourceVoltage)),
            Da750Mode.Cv when setting.Value / Da750Protocol.VoltageScale < SourceVoltage => (setting.Value / Da750Protocol.VoltageScale, CvCurrent),
            _ => (SourceVoltage, 0),
        };
    }

    private byte[] Reply(Ers272Code code, byte[]? info = null) => Ers272Frame.EncodeResponse(address, code, info ?? []);
}
