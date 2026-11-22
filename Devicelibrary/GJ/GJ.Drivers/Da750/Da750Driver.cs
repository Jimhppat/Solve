using System.Globalization;
using Solve.Device.Sdk;

namespace GJ.Drivers.Da750;

/// <summary>
/// GJ DA750 系列回馈式多通道电子负载驱动（ERS272 二进制协议，RS-485）。
/// </summary>
/// <remarks>
/// <para>点位、命令、参数的 Key 遵循 Docs/电源与电子负载模块对接规范.md 中"电子负载"一节的标准 Key：
/// 测试流程只认标准 Key，换另一家的电子负载不需要改流程。</para>
/// <para>地址：1–127（旧驱动 SetAddr 的范围，设备拨码开关设定）。一条 RS-485 总线可以挂多台。</para>
/// </remarks>
internal sealed class Da750Driver : IDeviceDriver, IDeviceSimulatorProvider
{
    /// <summary>
    /// 负载通道组的标识，已有接线按它找回通道，一经发布不得改名
    /// </summary>
    public const string LoadGroupKey = "load";

    // ---------- 点位（通道级） ----------
    public const string VoltageKey = "voltage";
    public const string CurrentKey = "current";
    public const string PowerKey = "power";
    public const string InputKey = "input";
    public const string StatusWordKey = "status-word";

    // ---------- 点位（模块级） ----------
    public const string InverterStatusKey = "inverter-status";

    // ---------- 命令（通道级） ----------
    public const string CcKey = "load.cc";
    public const string CcFastKey = "load.cc-fast";
    public const string CvKey = "load.cv";
    public const string OffKey = "load.off";

    // ---------- 命令参数 ----------
    public const string CurrentArgument = "current";
    public const string VoltageArgument = "voltage";
    public const string StartDelayArgument = "start-delay";

    // ---------- 模块参数 ----------
    public const string VonKey = "von";
    public const string StrictFrameKey = "strict-frame-check";
    public const string UndervoltageFaultKey = "undervoltage-is-fault";

    /// <summary>
    /// CC 启动延迟上限（CDA_750_4 注释：0–20 s）
    /// </summary>
    public const double MaxStartDelay = 20;

    private readonly Da750Model _model;

    public Da750Driver(Da750Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;

        var current = new CommandArgumentInfo(CurrentArgument, "电流", "A", model.MinCurrent, model.RatedCurrent);
        var startDelay = new CommandArgumentInfo(StartDelayArgument, "启动延迟", "s", 0, MaxStartDelay) { DefaultValue = 0 };

        Info = new DriverInfo(
            model.DriverId,
            $"GJ {model.Model}",
            DeviceCategory.PowerAndLoad,
            Invariant($"GJ DA750 系列回馈式电子负载，{model.Channels} 通道，单通道 {model.RatedVoltage} V / {model.RatedCurrent} A / {model.RatedPower} W，ERS272 协议"),
            MinAddress: 1,
            MaxAddress: Common.Ers272Frame.MaxAddress)
        {
            Points =
            [
                new PointInfo(VoltageKey, "电压", "V", PointAccess.Read) { Group = LoadGroupKey },
                new PointInfo(CurrentKey, "电流", "A", PointAccess.Read) { Group = LoadGroupKey },

                // 设备没有功率读数，由同一帧的电压 × 电流算出
                new PointInfo(PowerKey, "功率", "W", PointAccess.Read) { Group = LoadGroupKey },

                // 1 = 正在带载（状态字 Bit 0 DC-DC 开启），0 = 未带载
                new PointInfo(InputKey, "带载状态", "", PointAccess.Read) { Group = LoadGroupKey },

                // 原始状态字：位定义没有手册依据，上报原值便于实机核对
                new PointInfo(StatusWordKey, "通道状态字", "", PointAccess.Read) { Group = LoadGroupKey },
                new PointInfo(InverterStatusKey, "逆变器状态", "", PointAccess.Read),
            ],
            Commands =
            [
                // 负载设定一经写入立即按新值拉载，不是"只改设定值"，所以按输出动作而不是写参数：
                // 实机模式下每次都要操作员确认，并受下游停用位置的联锁约束
                new CommandInfo(CcKey, "恒流带载", CommandSafety.Output) { Group = LoadGroupKey, Arguments = [current, startDelay] },
                new CommandInfo(CcFastKey, "恒流带载（快速）", CommandSafety.Output) { Group = LoadGroupKey, Arguments = [current, startDelay] },
                new CommandInfo(CvKey, "恒压带载", CommandSafety.Output)
                {
                    Group = LoadGroupKey,
                    Arguments = [new CommandArgumentInfo(VoltageArgument, "电压", "V", model.MinCvVoltage, model.RatedVoltage)],
                },
                new CommandInfo(OffKey, "卸载", CommandSafety.Output) { Group = LoadGroupKey },
            ],
            Parameters =
            [
                // 输入电压高于 Von 才开始拉载。与被测产品的输出电压有关，按现场配置；默认值取旧上位机设定模板的 8 V
                new ParameterInfo(VonKey, "拉载电压 Von", ParameterKind.Number, DefaultValue: "8")
                {
                    Unit = "V",
                    Minimum = 0.5,
                    Maximum = model.RatedVoltage,
                },

                // 旧驱动注释掉了长度与校验和检查，原因未知：默认核对（保守），实机若因此一直失败再关闭
                new ParameterInfo(StrictFrameKey, "核对长度与校验和", ParameterKind.Boolean, DefaultValue: "true"),

                // 电压状态字段为"欠压"时是否报警。空闲时是否会报欠压没有依据：默认报警（保守），实机确认空闲即欠压后再关闭
                new ParameterInfo(UndervoltageFaultKey, "欠压状态报警", ParameterKind.Boolean, DefaultValue: "true"),
            ],
            Channels =
            [
                new ChannelGroupInfo(LoadGroupKey, "负载", ChannelFunction.Load, model.Channels),
            ],
        };
    }

    public DriverInfo Info { get; }

    public IDeviceSession CreateSession(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Da750Session(
            _model,
            Info,
            context.Address,
            context.Parameters.GetNumber(VonKey),
            context.Parameters.GetBoolean(StrictFrameKey),
            context.Parameters.GetBoolean(UndervoltageFaultKey));
    }

    /// <summary>
    /// 仿真模式下的设备：按同一协议应答，见 <see cref="Da750Simulator"/>
    /// </summary>
    public ISimulatedDevice CreateSimulator(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Da750Simulator(_model, context.Address);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
