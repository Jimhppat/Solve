using GJ.Drivers.Common;
using Solve.Device.Sdk;

namespace GJ.Drivers.Da750;

/// <summary>
/// 一个 DA750 模块的会话。内核保证同一会话的调用串行进行，内部不加锁。
/// </summary>
/// <remarks>
/// <para>设定镜像：协议只有"一次写全部通道"的命令（CID 41），改一路就必须同时给出其他各路的设定。
/// 会话保存设备已确认（应答 F0）的各路设定，命令只替换目标通道，其余各路按镜像原样重发。</para>
/// <para>故障安全：会话创建时（启动、模块或总线配置变化后）镜像为"全部卸载"，所以此后第一条命令会把其他通道一并卸载——
/// 宁可多卸载，也不凭猜测给其他通道拉载。写入失败（超时、设备报错）不更新镜像：设备是否已执行未知，
/// 下一条命令会按镜像重新下发全部通道，使设备回到已知状态。</para>
/// </remarks>
internal sealed class Da750Session : IDeviceSession
{
    private readonly Da750Model _model;
    private readonly DriverInfo _info;
    private readonly int _address;
    private readonly ushort _vonRaw;
    private readonly bool _strict;
    private readonly bool _undervoltageIsFault;
    private Da750Setting[] _applied;

    public Da750Session(Da750Model model, DriverInfo info, int address, double von, bool strict, bool undervoltageIsFault)
    {
        _model = model;
        _info = info;
        _address = address;
        _vonRaw = Da750Protocol.ToRaw(von, Da750Protocol.VoltageScale);
        _strict = strict;
        _undervoltageIsFault = undervoltageIsFault;
        _applied = Enumerable.Repeat(Da750Setting.Unloaded, model.Channels).ToArray();
    }

    /// <summary>
    /// 设备已确认的各路设定（通道 1 起），测试用
    /// </summary>
    internal IReadOnlyList<Da750Setting> Applied => _applied;

    public IExchange BeginPoll()
        => new Ers272Exchange(
            Da750Protocol.ReadValuesRequest(_address),
            _address,
            Da750Protocol.ValuesInfoLength,
            _strict,
            "读取测量值",
            info => Da750Protocol.DecodeValues(info, _model, _undervoltageIsFault));

    public IExchange BeginCommand(DeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var declared = _info.Commands.FirstOrDefault(c => string.Equals(c.Key, command.Key, StringComparison.Ordinal));

        // 不支持的命令、参数无效重试也没用：标为配置错误，且不发送任何字节
        if (declared is null)
            return Reject($"GJ {_model.Model} 不支持命令 {command.Key}。");
        if (command.Channel < 1 || command.Channel > _model.Channels)
            return Reject($"「{declared.Name}」的通道号 {command.Channel} 无效，应为 1–{_model.Channels}。");
        if (command.Arguments is null)
            return Reject($"「{declared.Name}」的参数表为 null。");
        if (command.Arguments.Keys.FirstOrDefault(k => declared.Arguments.All(a => a.Key != k)) is { } unknown)
            return Reject($"「{declared.Name}」没有参数 {unknown}。");

        // 内核已按声明校验并补全默认值；这里按同一份声明再校验一次，旧内核原样转交参数时也不会把越界值写进设备
        Da750Setting setting;
        var error = command.Key switch
        {
            Da750Driver.CcKey => TryCc(declared, command, Da750Mode.CcSlow, out setting),
            Da750Driver.CcFastKey => TryCc(declared, command, Da750Mode.CcFast, out setting),
            Da750Driver.CvKey => TryCv(declared, command, out setting),
            Da750Driver.OffKey => Done(Da750Setting.Unloaded, out setting),
            _ => Fail($"GJ {_model.Model} 不支持命令 {command.Key}。", out setting),
        };
        if (error is not null)
            return Reject(error);

        var desired = _applied.ToArray();
        desired[command.Channel - 1] = setting;
        return new Ers272Exchange(
            Da750Protocol.WriteSettingsRequest(_address, desired, _vonRaw),
            _address,
            infoLength: 0,
            _strict,
            $"通道 {command.Channel}「{declared.Name}」",
            _ =>
            {
                // 只有设备应答 F0 才更新镜像
                _applied = desired;
                return new ExchangeStep.Completed([]);
            });
    }

    private string? TryCc(CommandInfo declared, DeviceCommand command, Da750Mode mode, out Da750Setting setting)
    {
        setting = default;
        if (TryArgument(declared, command, Da750Driver.CurrentArgument, out var current) is { } badCurrent)
            return badCurrent;
        if (TryArgument(declared, command, Da750Driver.StartDelayArgument, out var delay) is { } badDelay)
            return badDelay;

        setting = new Da750Setting(
            mode,
            Da750Protocol.ToRaw(current, _model.CurrentScale),
            checked((byte)Da750Protocol.ToRaw(delay, Da750Protocol.DelayScale)));
        return null;
    }

    private static string? TryCv(CommandInfo declared, DeviceCommand command, out Da750Setting setting)
    {
        setting = default;
        if (TryArgument(declared, command, Da750Driver.VoltageArgument, out var voltage) is { } badVoltage)
            return badVoltage;

        // CV 的模式附加参数按 CDA_750_4 的注释"无实际意义，需赋值为 0"
        setting = new Da750Setting(Da750Mode.Cv, Da750Protocol.ToRaw(voltage, Da750Protocol.VoltageScale), 0);
        return null;
    }

    private static string? TryArgument(CommandInfo declared, DeviceCommand command, string key, out double value)
    {
        var argument = declared.Arguments.First(a => a.Key == key);
        if (!command.Arguments.TryGetValue(key, out value))
        {
            if (argument.DefaultValue is not { } fallback)
                return $"「{declared.Name}」缺少参数「{argument.Name}」。";
            value = fallback;
        }

        return argument.Check(value) is { } error ? $"「{declared.Name}」的{error}" : null;
    }

    private static string? Done(Da750Setting value, out Da750Setting setting)
    {
        setting = value;
        return null;
    }

    private static string Fail(string reason, out Da750Setting setting)
    {
        setting = default;
        return reason;
    }

    private static FailedExchange Reject(string reason) => new(reason, FailureKind.Configuration);
}
