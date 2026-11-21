using Solve.Device.Sdk;

namespace Sample.DriverPackage;

/// <summary>
/// 驱动包入口：package.json 的 entryType 指向这里。构造与 Drivers 只做声明，不访问任何外部资源。
/// </summary>
public sealed class SampleDriverPackage : IDriverPackage
{
    public IReadOnlyList<IDeviceDriver> Drivers { get; } = [new SamplePowerDriver()];
}

/// <summary>
/// 示例电源驱动，使用一个虚构的简单协议（只为演示 SDK 用法，不对应任何真实设备）：
/// <code>
/// 读取：请求 AA 地址 01 和校验          响应 55 地址 01 电压高 电压低 电流高 电流低 和校验
///      电压 = 原始值 / 100 V，电流 = 原始值 / 1000 A（大端、无符号）
/// 输出：请求 AA 地址 10 状态(0/1) 和校验  响应 55 地址 10 状态 和校验
/// 和校验 = 前面所有字节之和的低 8 位
/// </code>
/// </summary>
public sealed class SamplePowerDriver : IDeviceDriver
{
    public const string VoltageKey = "voltage";
    public const string CurrentKey = "current";
    public const string OutputOnKey = "output.on";
    public const string OutputOffKey = "output.off";
    public const string SettleTimeKey = "settle-ms";
    public const string OutputChannelKey = "output";

    /// <summary>
    /// 帧间隔：示例协议与 Modbus RTU 一样以 3.5 个字符的静默分隔帧
    /// </summary>
    private static readonly FrameGap Gap = new(3.5, TimeSpan.FromMilliseconds(2));

    public DriverInfo Info { get; } = new("sample.power", "示例电源", DeviceCategory.PowerAndLoad,
        "演示用虚构协议，不对应真实设备", MinAddress: 1, MaxAddress: 32)
    {
        Points =
        [
            new PointInfo(VoltageKey, "输出电压", "V", PointAccess.Read),
            new PointInfo(CurrentKey, "输出电流", "A", PointAccess.Read),
        ],
        Commands =
        [
            new CommandInfo(OutputOnKey, "打开输出", CommandSafety.Output),
            new CommandInfo(OutputOffKey, "关闭输出", CommandSafety.Output),
        ],
        Parameters =
        [
            // 输出切换后电源需要一段稳定时间，期间读数无意义；不同负载下所需时间不同，所以做成模块参数
            new ParameterInfo(SettleTimeKey, "输出稳定时间", ParameterKind.Integer, DefaultValue: "200")
            {
                Unit = "ms",
                Minimum = 0,
                Maximum = 2000,
            },
        ],

        // 单路输出：可以接到拓扑上作为供电
        Channels =
        [
            new ChannelGroupInfo(OutputChannelKey, "输出", ChannelFunction.PowerSupply, Count: 1),
        ],
    };

    public IDeviceSession CreateSession(DeviceContext context)
        => new Session((byte)context.Address, TimeSpan.FromMilliseconds(context.Parameters.GetInteger(SettleTimeKey)));

    private delegate IReadOnlyList<PointSample> FrameDecoder(ReadOnlySpan<byte> frame);

    private sealed class Session(byte address, TimeSpan settleTime) : IDeviceSession
    {
        public IExchange BeginPoll()
            => new FixedExchange(address, function: 0x01, payload: [], responseLength: 8, TimeSpan.Zero, DecodePoll);

        public IExchange BeginCommand(DeviceCommand command) => command.Key switch
        {
            OutputOnKey => new FixedExchange(address, function: 0x10, payload: [1], responseLength: 5, settleTime, _ => []),
            OutputOffKey => new FixedExchange(address, function: 0x10, payload: [0], responseLength: 5, settleTime, _ => []),

            // 设备不支持的命令重试也没用：标为配置错误
            _ => new FailedExchange($"示例电源不支持命令 {command.Key}。", FailureKind.Configuration),
        };

        private static IReadOnlyList<PointSample> DecodePoll(ReadOnlySpan<byte> frame)
        {
            var voltage = (frame[3] << 8 | frame[4]) / 100.0;
            var current = (frame[5] << 8 | frame[6]) / 1000.0;
            return [new PointSample(VoltageKey, voltage), new PointSample(CurrentKey, current)];
        }
    }

    /// <summary>
    /// 一问一答、定长响应的交互
    /// </summary>
    private sealed class FixedExchange(
        byte address,
        byte function,
        byte[] payload,
        int responseLength,
        TimeSpan delayAfter,
        FrameDecoder decode) : IExchange
    {
        public ExchangeStep Start()
        {
            var request = new byte[3 + payload.Length + 1];
            request[0] = 0xAA;
            request[1] = address;
            request[2] = function;
            payload.CopyTo(request, 3);
            request[^1] = Sum(request.AsSpan(0, request.Length - 1));
            return new ExchangeStep.Send(request) { GapBefore = Gap, DelayAfter = delayAfter };
        }

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
        {
            frameLength = 0;
            if (received.Length > 0 && received[0] != 0x55)
                return FrameStatus.Invalid;
            if (received.Length < responseLength)
                return FrameStatus.NeedMoreData;

            var frame = received[..responseLength];
            if (frame[1] != address || frame[2] != function || frame[^1] != Sum(frame[..^1]))
                return FrameStatus.Invalid;

            frameLength = responseLength;
            return FrameStatus.Complete;
        }

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => new ExchangeStep.Completed(decode(frame));

        private static byte Sum(ReadOnlySpan<byte> bytes)
        {
            var sum = 0;
            foreach (var b in bytes)
                sum += b;
            return (byte)sum;
        }
    }

    private sealed class FailedExchange(string reason, FailureKind kind) : IExchange
    {
        public ExchangeStep Start() => new ExchangeStep.Failed(reason, kind);

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
        {
            frameLength = 0;
            return FrameStatus.Invalid;
        }

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => new ExchangeStep.Failed(reason, kind);
    }
}
