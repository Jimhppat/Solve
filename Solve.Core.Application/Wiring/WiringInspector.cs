using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Wiring;

/// <summary>
/// 一路接线的运行健康度。数值越大越严重，汇总时取最严重的一路。
/// </summary>
public enum WireHealth
{
    /// <summary>
    /// 就绪：模块在线，读数有效
    /// </summary>
    Ready = 1,

    /// <summary>
    /// 无协议：接在通用模块上，只登记接线关系，系统不监视、也不能下发命令（联锁同样拒绝）
    /// </summary>
    NoProtocol = 2,

    /// <summary>
    /// 等待：总线未连接或尚未轮询到
    /// </summary>
    Waiting = 3,

    /// <summary>
    /// 故障：模块通信异常、设备故障、配置错误或驱动未就绪
    /// </summary>
    Fault = 4,

    /// <summary>
    /// 无效：模块已不存在，或这路通道已没有、已停用、功能已不同
    /// </summary>
    Invalid = 5,
}

public static class WireHealths
{
    public static string DisplayName(this WireHealth health) => health switch
    {
        WireHealth.Ready => "就绪",
        WireHealth.NoProtocol => "无协议",
        WireHealth.Waiting => "等待",
        WireHealth.Fault => "故障",
        WireHealth.Invalid => "无效",
        _ => "-",
    };
}

/// <summary>
/// 一路有效接线的运行状态快照（不可变）
/// </summary>
/// <param name="Source">接线实际所在的位置</param>
/// <param name="IsInherited">接线来自查询位置的上级</param>
/// <param name="ModuleText">"总线 · 模块"，模块已不存在时为说明文字</param>
/// <param name="ChannelText">通道名，如"输出 3"</param>
/// <param name="Problem">非就绪时的原因（面向操作员）</param>
/// <param name="Driver">驱动的点位声明（有驱动实现时），用于格式化读数</param>
/// <param name="Module">模块通信状态与读数；未轮询过为 null</param>
public sealed record WireStatus(
    Wire Wire,
    TopologyNode Source,
    bool IsInherited,
    string ModuleText,
    string ChannelText,
    WireHealth Health,
    string? Problem,
    DriverInfo? Driver,
    ModuleStatus? Module);

/// <summary>
/// 接线的运行视图：把接线、模块绑定、通信状态与读数合在一起，按位置回答"这里接了什么、现在能不能用"。
/// 只读，不访问存储与硬件；可在任意线程调用（各数据源都是不可变快照）。
/// </summary>
public sealed class WiringInspector
{
    private readonly IWiringService _wiring;
    private readonly IDeviceBindings _bindings;
    private readonly IDriverCatalog _catalog;
    private readonly IModuleStatusSource _statuses;
    private readonly IBusManagementService _buses;

    public WiringInspector(
        IWiringService wiring,
        IDeviceBindings bindings,
        IDriverCatalog catalog,
        IModuleStatusSource statuses,
        IBusManagementService buses)
    {
        ArgumentNullException.ThrowIfNull(wiring);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(buses);
        _wiring = wiring;
        _bindings = bindings;
        _catalog = catalog;
        _statuses = statuses;
        _buses = buses;
    }

    public IWiringService Wiring => _wiring;

    /// <summary>
    /// 位置的全部有效接线（上级的在前）及其运行状态
    /// </summary>
    public IReadOnlyList<WireStatus> Inspect(TopologyNodeId nodeId)
    {
        var effective = _wiring.Current.GetEffective(nodeId);
        var result = new WireStatus[effective.Count];
        for (var i = 0; i < effective.Count; i++)
            result[i] = Inspect(effective[i], nodeId);
        return result;
    }

    /// <summary>
    /// 位置的硬件健康度：有效接线中最严重的一路；没有接线为 null
    /// </summary>
    public WireHealth? Summarize(TopologyNodeId nodeId)
    {
        WireHealth? worst = null;
        foreach (var effective in _wiring.Current.GetEffective(nodeId))
        {
            var health = Inspect(effective, nodeId).Health;
            if (worst is null || health > worst)
                worst = health;
        }

        return worst;
    }

    /// <summary>
    /// 安全联锁的判据：位置可用（自身与上级都启用）、至少有一路接线、全部有效接线都就绪，才允许向这里发命令。
    /// 将来的命令执行必须先通过本检查；不满足时返回第一个原因，故障安全方向是拒绝。
    /// </summary>
    public Result CheckReady(TopologyNodeId nodeId)
    {
        var map = _wiring.Current;
        if (map.Topology is not { } topology || !topology.TryGetNode(nodeId, out var node))
            return Result.Failure(WiringErrors.NodeNotFound());
        if (!node.IsEffectivelyEnabled)
            return Result.Failure(new Error("Wiring.NodeDisabled", $"{node.Address} 已停用（维护屏蔽），不能发出命令。"));

        var statuses = Inspect(nodeId);
        if (statuses.Count == 0)
            return Result.Failure(new Error("Wiring.NotWired", $"{node.Address} 没有接线。"));

        foreach (var status in statuses)
        {
            if (status.Health != WireHealth.Ready)
            {
                return Result.Failure(new Error("Wiring.NotReady",
                    $"{node.Address} 的「{status.Wire.KindText}」{status.ModuleText} {status.ChannelText} {status.Health.DisplayName()}：{status.Problem}"));
            }
        }

        return Result.Success();
    }

    private WireStatus Inspect(EffectiveWire effective, TopologyNodeId queried)
    {
        var wire = effective.Wire;
        var inherited = effective.Source.Id != queried;

        if (!_bindings.TryGet(wire.Channel.Device, out var binding))
        {
            return new WireStatus(wire, effective.Source, inherited, "模块已不存在", $"{wire.Channel.Group} {wire.Channel.Index}",
                WireHealth.Invalid, "接线指向的模块已被删除，或所在总线未注册。", null, null);
        }

        var device = binding.Device;
        var busName = _buses.GetRuntime(device.BusId) is { IsSuccess: true } runtime ? runtime.Value.Descriptor.Name : "未知总线";
        var moduleText = $"{busName} · {device.Name}";

        // 模块实际可用的通道 = 驱动的通道能力 + 模块的通道配置
        var channels = ModuleChannelLookup.Resolve(_catalog, device);
        var group = channels.IsSuccess ? ModuleChannelLookup.Find(channels.Value, wire.Channel.Group) : null;
        var channelText = group?.ChannelName(wire.Channel.Index) ?? $"{wire.Channel.Group} {wire.Channel.Index}";
        if (channels.IsFailure)
            return Invalid(channels.Error.Message);
        if (group is null || !group.Contains(wire.Channel.Index))
            return Invalid($"{device.Name}已没有这路通道。");
        if (!group.IsEnabled(wire.Channel.Index))
            return Invalid($"这路通道已在模块配置里停用。");
        if (group.Kind != wire.Kind)
            return Invalid($"这路通道现在是「{group.Kind.DisplayName()}」，与接线时的「{wire.Kind.DisplayName()}」不同。");

        var status = _statuses.Get(device.Id);
        var (health, problem) = binding.Status == DriverBindingStatus.NoProtocol
            ? (WireHealth.NoProtocol, binding.Problem)
            : binding.Status != DriverBindingStatus.Ready
            ? (WireHealth.Fault, binding.Problem)
            : status?.State switch
            {
                ModuleCommState.Online => (WireHealth.Ready, null),
                ModuleCommState.CommFault or ModuleCommState.DeviceFault or ModuleCommState.ConfigError => (WireHealth.Fault, status.Problem),
                _ => (WireHealth.Waiting, status?.Problem ?? "尚未轮询到"),
            };

        return new WireStatus(wire, effective.Source, inherited, moduleText, channelText, health, problem, binding.Driver, status);

        WireStatus Invalid(string reason)
            => new(wire, effective.Source, inherited, moduleText, channelText, WireHealth.Invalid, reason, binding.Driver, null);
    }
}
