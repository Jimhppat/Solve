using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 总线注册表集合变动事件基类。
/// </summary>
public abstract record BusRegistryEvent(BusId BusId);

public sealed record BusRegistered(BusId BusId, BusDescriptor Descriptor) : BusRegistryEvent(BusId);

public sealed record BusUnregistered(BusId BusId) : BusRegistryEvent(BusId);

public sealed record BusReconfigured(BusId BusId, BusDescriptor Descriptor) : BusRegistryEvent(BusId);

/// <summary>
/// 总线注册表：按描述创建/注销 <see cref="BusRuntime"/>，并防止多条总线争用同一物理资源。
/// </summary>
/// <remarks>
/// 只负责生命周期与资源归属；连接、断开、事务直接在取回的 BusRuntime 上调用。
/// 注册与注销在同一把锁内完成冲突检查与登记，运行时的释放（含 I/O）在锁外执行。
/// </remarks>
public sealed class BusManager : IAsyncDisposable
{
    private readonly IBusTransportFactory _transportFactory;
    private readonly IBusSafetyInterlock _interlock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly BusRuntimeOptions _options;
    private readonly TimeProvider _time;
    private readonly EventBroadcaster<BusRegistryEvent> _registryEvents;
    private readonly SafeStopForwarder _safeStop = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<BusId, BusRuntime> _buses = [];
    private bool _disposed;

    public BusManager(
        IBusTransportFactory transportFactory,
        IBusSafetyInterlock interlock,
        ILoggerFactory loggerFactory,
        BusRuntimeOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(interlock);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _transportFactory = transportFactory;
        _interlock = interlock;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BusManager>();
        _options = options ?? BusRuntimeOptions.Default;
        _time = timeProvider ?? TimeProvider.System;
        _registryEvents = new EventBroadcaster<BusRegistryEvent>(_options.StateEventCapacity);
    }

    /// <summary>
    /// 订阅总线集合变动（新增、删除、重配置），并原子地取得订阅时刻已注册的总线：
    /// <paramref name="current"/> 中的总线不会再收到对应的 <see cref="BusRegistered"/>，之后注册的一定会收到。
    /// 每个订阅者独立排队，用完 Dispose 退订。
    /// </summary>
    public EventSubscription<BusRegistryEvent> SubscribeRegistry(out IReadOnlyList<BusRuntime> current)
    {
        // 与 Register 共用 _gate（登记与发布 BusRegistered 在同一把锁内），快照与订阅之间不会漏掉或重复
        lock (_gate)
        {
            current = [.. _buses.Values];
            return _registryEvents.Subscribe();
        }
    }

    /// <summary>
    /// 当前已注册总线的快照
    /// </summary>
    public IReadOnlyList<BusRuntime> Buses
    {
        get
        {
            lock (_gate)
            {
                return [.. _buses.Values];
            }
        }
    }

    /// <summary>
    /// 注册总线（不连接）。Id 重复返回 Bus.AlreadyRegistered；串口名被占用返回 Bus.ResourceConflict。
    /// </summary>
    public Result<BusRuntime> Register(BusDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 先校验再查冲突：无效配置（如 PortName 为 null）不得进入资源比较
        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result<BusRuntime>.Failure(BusErrors.InvalidDescriptor(errors));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_buses.ContainsKey(descriptor.Id))
                return Result<BusRuntime>.Failure(BusErrors.AlreadyRegistered(descriptor.Id));

            if (FindResourceOwner(descriptor) is { } conflict)
                return Result<BusRuntime>.Failure(conflict);

            // Create 只校验配置、创建未打开的通道并启动空闲的监督任务，不做 I/O，可在锁内执行
            var created = BusRuntime.Create(
                descriptor, _transportFactory, _interlock,
                _loggerFactory.CreateLogger<BusRuntime>(), _options, _time, _safeStop);

            if (created.IsFailure)
                return created;

            _buses.Add(descriptor.Id, created.Value);
            _registryEvents.Publish(new BusRegistered(descriptor.Id, descriptor));
            _logger.LogInformation("注册总线 {BusId} {Name} ({Type})", descriptor.Id, descriptor.Name, descriptor.GetType().Name);
            return created;
        }
    }

    public bool TryGet(BusId id, out BusRuntime runtime)
    {
        lock (_gate)
        {
            return _buses.TryGetValue(id, out runtime!);
        }
    }

    /// <summary>
    /// 注销总线：先摘除登记（新请求立即找不到它），再断开并释放运行时。
    /// </summary>
    public async Task<Result> UnregisterAsync(BusId id, CancellationToken cancellationToken)
    {
        BusRuntime? runtime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_buses.Remove(id, out runtime))
                return Result.Failure(BusErrors.NotRegistered(id));
        }

        var disconnected = await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        if (disconnected.IsFailure)
        {
            // 断开失败（如处于锁存态）不阻止注销：Dispose 无论如何都会关闭物理通道
            _logger.LogWarning("注销总线 {BusId} 时断开未成功：{Error}，继续释放", id, disconnected.Error);
        }

        await runtime.DisposeAsync().ConfigureAwait(false);
        _registryEvents.Publish(new BusUnregistered(id));
        _logger.LogInformation("注销总线 {BusId}", id);
        return Result.Success();
    }

    /// <summary>
    /// 重配置已注册总线：先做排己查重，然后由运行时执行软更新或物理通道重构。
    /// </summary>
    public async Task<Result> ReconfigureAsync(BusDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result.Failure(BusErrors.InvalidDescriptor(errors));

        BusRuntime? runtime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_buses.TryGetValue(descriptor.Id, out runtime))
                return Result.Failure(BusErrors.NotRegistered(descriptor.Id));

            if (FindResourceOwner(descriptor, excludeBusId: descriptor.Id) is { } conflict)
                return Result.Failure(conflict);
        }

        var reconfigured = await runtime.ReconfigureAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (reconfigured.IsFailure)
            return reconfigured;

        _registryEvents.Publish(new BusReconfigured(descriptor.Id, descriptor));
        _logger.LogInformation("总线 {BusId} {Name} 重配置成功", descriptor.Id, descriptor.Name);
        return Result.Success();
    }

    /// <summary>
    /// 预检候选总线描述是否与现有总线存在物理资源冲突（如串口占用），支持排除自身。
    /// </summary>
    public Error? ValidateResourceConflict(BusDescriptor candidate, BusId? excludeBusId = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            return FindResourceOwner(candidate, excludeBusId);
        }
    }

    /// <summary>
    /// 挂接设备层的安全停止处理：此后任何总线从 Connected 安全跳闸时，关闭通道前先由它把设备带到安全状态。
    /// 设备层晚于总线注册表创建，所以在组合根里事后挂接；对挂接前已注册的总线同样生效。只能挂接一次。
    /// </summary>
    public void AttachSafeStopHandler(IBusSafeStopHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Interlocked.CompareExchange(ref _safeStop.Inner, handler, null) is not null)
            throw new InvalidOperationException("安全停止处理已经挂接。");
    }

    /// <summary>
    /// 对所有已注册总线执行安全跳闸（系统级急停入口）。立即返回跳闸的总线数，不等待设备安全状态下发；
    /// 下发由各总线的监督任务并行完成。
    /// </summary>
    public int SafetyTripAll(string reason) => TripAll(reason).Count;

    /// <summary>
    /// 对所有已注册总线执行安全跳闸，并等待各总线的设备安全状态下发结束（各自受 <see cref="BusRuntimeOptions.SafeStopTimeout"/> 约束）。
    /// 用于急停后马上要退出进程的场合：先让设备卸载、关输出，再关闭通道。返回跳闸的总线数。
    /// </summary>
    /// <remarks>下发失败或超时不会让本方法失败（已记严重日志）；只有 <paramref name="cancellationToken"/> 取消时抛出。</remarks>
    public async Task<int> SafetyTripAllAsync(string reason, CancellationToken cancellationToken)
    {
        var buses = TripAll(reason);
        await Task.WhenAll(buses.Select(b => b.SafeStopSettled)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return buses.Count;
    }

    private IReadOnlyList<BusRuntime> TripAll(string reason)
    {
        var buses = Buses;
        foreach (var bus in buses)
            bus.SafetyTrip(reason);

        _logger.LogError("全部总线安全跳闸（{Count} 条）：{Reason}", buses.Count, reason);
        return buses;
    }

    /// <summary>
    /// 释放全部总线（并行，各自受 ShutdownTimeout 约束）。幂等。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        BusRuntime[] buses;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            buses = [.. _buses.Values];
            _buses.Clear();
        }

        await Task.WhenAll(buses.Select(b => b.DisposeAsync().AsTask())).ConfigureAwait(false);
        _registryEvents.Complete();
        _logger.LogInformation("BusManager 已释放 {Count} 条总线", buses.Length);
    }

    /// <summary>
    /// 转发到事后挂接的安全停止处理；尚未挂接时什么也不做（总线上还没有可用的设备会话）
    /// </summary>
    private sealed class SafeStopForwarder : IBusSafeStopHandler
    {
        public IBusSafeStopHandler? Inner;

        public Task RunAsync(BusId busId, BusDescriptor descriptor, IBusChannel channel, CancellationToken cancellationToken)
            => Volatile.Read(ref Inner) is { } inner
                ? inner.RunAsync(busId, descriptor, channel, cancellationToken)
                : Task.CompletedTask;
    }

    // 调用方必须持有 _gate
    private Error? FindResourceOwner(BusDescriptor candidate, BusId? excludeBusId = null)
    {
        // 同一串口只能被一个进程内句柄打开；TCP 允许多连接同一端点（由设备自身限制连接数）
        if (candidate is not SerialBusDescriptor serial)
            return null;

        foreach (var existing in _buses.Values)
        {
            if (excludeBusId.HasValue && existing.Id == excludeBusId.Value)
                continue;

            if (existing.Descriptor is SerialBusDescriptor other
                && string.Equals(other.PortName.Trim(), serial.PortName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return BusErrors.ResourceConflict(serial.PortName, existing.Id);
            }
        }

        return null;
    }
}
