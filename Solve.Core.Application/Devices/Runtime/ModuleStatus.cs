using System.Collections.Concurrent;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Application.Devices.Runtime;

/// <summary>
/// 模块的通信状态
/// </summary>
public enum ModuleCommState
{
    /// <summary>
    /// 不轮询：驱动未就绪（无协议、驱动未加载、地址越界、参数无效）
    /// </summary>
    NotPolled = 1,

    /// <summary>
    /// 等待：总线未连接，或刚绑定尚未轮询到
    /// </summary>
    Waiting = 2,

    /// <summary>
    /// 在线：最近一次轮询成功
    /// </summary>
    Online = 3,

    /// <summary>
    /// 通信异常：连续暂时性失败达到阈值，按退避间隔继续重试
    /// </summary>
    CommFault = 4,

    /// <summary>
    /// 配置错误：驱动判定地址或参数与设备不符，不再重试，直到模块配置被修改
    /// </summary>
    ConfigError = 5,

    /// <summary>
    /// 设备故障：设备报告自身故障，按较长间隔继续轮询以观察恢复
    /// </summary>
    DeviceFault = 6,
}

/// <summary>
/// 一个点位的最新值
/// </summary>
/// <param name="Value">已换算到点位单位的工程值</param>
/// <param name="Timestamp">读到该值的时间（UTC）</param>
public readonly record struct PointValue(string Key, double Value, DateTimeOffset Timestamp);

/// <summary>
/// 一个模块的运行状态快照（不可变）
/// </summary>
/// <param name="Problem">非在线时的原因（面向操作员）</param>
/// <param name="LastSuccess">最近一次轮询成功的时间；从未成功为 null</param>
/// <param name="Points">最近一次成功读到的点位值；通信异常时保留旧值，由 <see cref="LastSuccess"/> 判断新旧</param>
public sealed record ModuleStatus(
    DeviceId DeviceId,
    BusId BusId,
    ModuleCommState State,
    string? Problem,
    DateTimeOffset? LastSuccess,
    int ConsecutiveFailures,
    IReadOnlyList<PointValue> Points);

/// <summary>
/// 模块运行状态的读取端口：界面、报警、孪生按需读取最新快照
/// </summary>
public interface IModuleStatusSource
{
    ModuleStatus? Get(DeviceId deviceId);

    IReadOnlyList<ModuleStatus> GetBus(BusId busId);
}

/// <summary>
/// 模块运行状态库：轮询线程写，界面等任意线程读。每个模块只保留最新快照，上报再频繁内存也不增长。
/// </summary>
public sealed class ModuleStatusStore : IModuleStatusSource
{
    private readonly ConcurrentDictionary<DeviceId, ModuleStatus> _statuses = new();

    public ModuleStatus? Get(DeviceId deviceId) => _statuses.GetValueOrDefault(deviceId);

    public IReadOnlyList<ModuleStatus> GetBus(BusId busId)
        => _statuses.Values.Where(s => s.BusId == busId).ToList();

    public void Set(ModuleStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _statuses[status.DeviceId] = status;
    }

    /// <summary>
    /// 移除一条总线上已不存在的模块（被删除或总线已注销）
    /// </summary>
    public void RemoveExcept(BusId busId, IReadOnlySet<DeviceId> keep)
    {
        foreach (var status in _statuses.Values)
        {
            if (status.BusId == busId && !keep.Contains(status.DeviceId))
                _statuses.TryRemove(status.DeviceId, out _);
        }
    }
}
