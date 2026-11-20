using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// Chroma 62000B 系列直流电源模块驱动（SCPI 文本命令）。
/// </summary>
/// <remarks>
/// <para>链路：62000B 模块原生只有 CAN 总线（SCPI 文本装在 CAN 帧里，模块地址由拨码开关设定）。
/// Solve 内核目前只有串口 / TCP 字节流，本驱动先按"SCPI 文本行直接在字节流上收发"实现，
/// 适用于把 SCPI 文本透传到串口或 TCP 的接口（如厂家控制盒、CAN 透传网关），具体接口需在实机上确认。</para>
/// <para>地址：字节流封装里没有模块地址，一条总线上的所有请求都发给同一台设备。
/// 为防止把两个模块的命令发到同一台电源上，地址范围限定为 1，即每条总线只能挂一台。内核支持 CAN 后再放开到 1–254。</para>
/// </remarks>
internal sealed class Chroma62000BDriver : IDeviceDriver
{
    public const string VoltageKey = "voltage";
    public const string CurrentKey = "current";
    public const string PowerKey = "power";
    public const string OutputKey = "output";
    public const string OutputOnKey = "output.on";
    public const string OutputOffKey = "output.off";
    public const string CommandDelayKey = "command-delay-ms";

    private readonly Chroma62000BModel _model;

    public Chroma62000BDriver(Chroma62000BModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        Info = new DriverInfo(
            model.DriverId,
            $"Chroma {model.Model}",
            DeviceCategory.PowerAndLoad,
            FormattableString.Invariant($"Chroma 62000B 系列直流电源模块，{model.RatedVoltage} V / {model.RatedCurrent} A / {model.RatedPower} W，SCPI 文本命令"),
            MinAddress: 1,
            MaxAddress: 1)
        {
            Points =
            [
                new PointInfo(VoltageKey, "输出电压", "V", PointAccess.Read),
                new PointInfo(CurrentKey, "输出电流", "A", PointAccess.Read),

                // 设备没有功率查询命令（手册未列出），由同一次轮询的电压 × 电流算出
                new PointInfo(PowerKey, "输出功率", "W", PointAccess.Read),

                // 1 = 输出已打开（状态字 Bit 13），0 = 关闭
                new PointInfo(OutputKey, "输出状态", "", PointAccess.Read),
            ],
            Commands =
            [
                new CommandInfo(OutputOnKey, "打开输出", CommandSafety.Output),
                new CommandInfo(OutputOffKey, "关闭输出", CommandSafety.Output),
            ],
            Parameters =
            [
                // 手册示例程序在命令与读取之间有一段延时，但没有给出数值；不同固件、接口可能不同，所以做成模块参数
                new ParameterInfo(CommandDelayKey, "命令执行等待", ParameterKind.Integer, DefaultValue: "100")
                {
                    Unit = "ms",
                    Minimum = 0,
                    Maximum = 2000,
                },
            ],
        };
    }

    public DriverInfo Info { get; }

    public IDeviceSession CreateSession(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var delay = TimeSpan.FromMilliseconds(context.Parameters.GetInteger(CommandDelayKey));
        return new Chroma62000BSession(_model, delay);
    }
}
