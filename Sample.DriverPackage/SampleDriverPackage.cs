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
/// 同时演示可选的仿真能力（<see cref="IDeviceSimulatorProvider"/>）：仿真模式下由 <see cref="Simulator"/> 按同一协议应答。
/// </summary>
public sealed class SamplePowerDriver : IDeviceDriver, IDeviceSimulatorProvider
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

    public ISimulatedDevice CreateSimulator(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Simulator((byte)context.Address);
    }

    /// <summary>
    /// 仿真设备：只应答发给自己地址、校验正确的请求。输出打开时读数为固定的
    /// <see cref="SimulatedVoltage"/> / <see cref="SimulatedCurrent"/>，关闭时为 0；输出命令原样确认并记住输出状态。
    /// 上电默认输出关闭，与真实电源一致。
    /// </summary>
    public sealed class Simulator(byte address) : ISimulatedDevice
    {
        /// <summary>
        /// 输出打开时的仿真电压，V（编码为 1200，即 0.01 V 分辨率）
        /// </summary>
        public const double SimulatedVoltage = 12.0;

        /// <summary>
        /// 输出打开时的仿真电流，A（编码为 500，即 0.001 A 分辨率）
        /// </summary>
        public const double SimulatedCurrent = 0.5;

        public bool OutputOn { get; private set; }

        public byte[]? Respond(ReadOnlySpan<byte> request)
        {
            // 不是本协议的帧、校验错误或不是发给本机的：真实设备不会应答
            if (request.Length < 4 || request[0] != 0xAA || request[1] != address || request[^1] != Sum(request[..^1]))
                return null;

            byte[] response;
            switch (request[2])
            {
                case 0x01 when request.Length == 4:
                    var voltage = OutputOn ? (int)Math.Round(SimulatedVoltage * 100) : 0;
                    var current = OutputOn ? (int)Math.Round(SimulatedCurrent * 1000) : 0;
                    response = [0x55, address, 0x01, (byte)(voltage >> 8), (byte)voltage, (byte)(current >> 8), (byte)current, 0];
                    break;

                case 0x10 when request.Length == 5 && request[3] is 0 or 1:
                    OutputOn = request[3] == 1;
                    response = [0x55, address, 0x10, request[3], 0];
                    break;

                // 不认识的功能码：示例设备不应答
                default:
                    return null;
            }

            response[^1] = Sum(response.AsSpan(0, response.Length - 1));
            return response;
        }
    }

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
    }

    private static byte Sum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return (byte)sum;
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
