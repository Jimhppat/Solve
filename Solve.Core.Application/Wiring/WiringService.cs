using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Drivers;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.Core.Application.Wiring;

/// <inheritdoc cref="IWiringService"/>
/// <remarks>
/// <para>跟随拓扑：每次读取 <see cref="Current"/> 都核对拓扑服务的当前拓扑，拓扑被替换时按新拓扑重新定位
/// （位置 Id 不变，接线随之保留；被删除的位置上的接线已由存储在同一事务内删除）。</para>
/// <para>跟随模块：模块被删除时其接线由存储级联删除，绑定登记表发出变化后本服务重新读取，合并连续的多次变化。</para>
/// </remarks>
public sealed class WiringService : IWiringService, IDeviceWiring, IAsyncDisposable
{
    private readonly IWiringRepository _repository;
    private readonly ITopologyService _topology;
    private readonly IDeviceBindings _bindings;
    private readonly IDriverCatalog _catalog;
    private readonly IBusManagementService _buses;
    private readonly ILogger _logger;

    // 写入与读取串行：保存时"预览基准仍是当前接线"的判断与写库、替换必须是一个整体
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 接线快照的缓存（按拓扑与接线列表的引用判断是否过期），读取时持锁时间很短，不做 I/O
    private readonly Lock _sync = new();

    // 模块变化的信号：容量 1、满则丢弃，连续多次变化合并成一次重新读取
    private readonly Channel<bool> _reloadSignals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<Wire> _wires = [];
    private IReadOnlyList<Wire>? _mapWires;
    private WiringMap _map = WiringMap.Empty;
    private volatile Error? _loadError;
    private Task? _listener;

    public WiringService(
        IWiringRepository repository,
        ITopologyService topology,
        IDeviceBindings bindings,
        IDriverCatalog catalog,
        IBusManagementService buses,
        ILogger<WiringService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(buses);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _topology = topology;
        _bindings = bindings;
        _catalog = catalog;
        _buses = buses;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public WiringMap Current
    {
        get
        {
            lock (_sync)
            {
                var topology = _topology.Current;
                var wires = Volatile.Read(ref _wires);
                if (!ReferenceEquals(_map.Topology, topology) || !ReferenceEquals(_mapWires, wires))
                {
                    _map = WiringMap.Create(topology, wires);
                    _mapWires = wires;
                }

                return _map;
            }
        }
    }

    public Error? LoadError => _loadError;

    /// <summary>
    /// 开始跟随模块的变化（删除模块后重新读取）。只调用一次。
    /// </summary>
    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("接线服务已经启动。");

        _bindings.Changed += OnBindingsChanged;
        _listener = Task.Run(() => ListenAsync(_lifetime.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _bindings.Changed -= OnBindingsChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _reloadSignals.Writer.TryComplete();
        if (_listener is not null)
            await _listener.ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }

    public async Task<Result> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 仓储的 Async 方法内部是同步磁盘 I/O，必须离开调用方（可能是 UI）线程
            var loaded = await Task.Run(() => _repository.LoadAllAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                // 保留上一次读到的接线供显示，但禁止修改：冲突判断不能建立在不完整的数据上
                _loadError = loaded.Error;
                _logger.LogError("接线读取失败，禁止修改接线：{Error}", loaded.Error);
                return Result.Failure(loaded.Error);
            }

            _loadError = null;
            Volatile.Write(ref _wires, loaded.Value);
            var map = Current;
            if (map.Topology is not null && map.Detached.Count > 0)
            {
                // 外键保证落点存在，这里出现说明库被手工改过或同一路通道重复：不参与运行，留痕供人工修复
                _logger.LogError("有 {Count} 条接线无法落位（位置不存在、落在系统根或通道重复），已忽略：{Wires}",
                    map.Detached.Count, string.Join("，", map.Detached.Take(WiringErrors.MaxListed)));
            }

            _logger.LogInformation("读取接线 {Count} 条", map.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return Result.Success();
    }

    // ==================== 查询 ====================

    public IReadOnlyList<WiringModule> ListModules()
    {
        var modules = new List<WiringModule>();
        foreach (var runtime in _buses.GetAllRuntimes().OrderBy(r => r.Descriptor.Name, StringComparer.CurrentCulture))
        {
            foreach (var binding in _bindings.Get(runtime.Id))
            {
                if (_catalog.TryGet(binding.Device.Driver, out var driver) && driver.Channels.Count > 0)
                    modules.Add(new WiringModule(runtime.Id, runtime.Descriptor.Name, binding.Device, driver));
            }
        }

        return modules;
    }

    public string DescribeChannel(ChannelRef channel)
    {
        if (_bindings.TryGet(channel.Device, out var binding))
        {
            var group = _catalog.TryGet(binding.Device.Driver, out var driver) ? driver.FindChannelGroup(channel.Group) : null;
            return group is not null
                ? $"{binding.Device.Name} {group.ChannelName(channel.Index)}"
                : $"{binding.Device.Name} {channel.Group} {channel.Index}";
        }

        return $"已删除的模块 {channel.Group} {channel.Index}";
    }

    // ==================== 预览 ====================

    public Result<WiringPlan> PlanAdd(TopologyNodeId node, ChannelRef channel, string? purpose, string? label)
    {
        if (CheckWritable() is { } blocked)
            return Result<WiringPlan>.Failure(blocked);

        var map = Current;
        if (map.Topology is not { } topology)
            return Result<WiringPlan>.Failure(WiringErrors.NoTopology());
        if (!topology.TryGetNode(node, out var target))
            return Result<WiringPlan>.Failure(WiringErrors.NodeNotFound());

        var resolved = ResolveGroup(channel.Device, channel.Group);
        if (resolved.IsFailure)
            return Result<WiringPlan>.Failure(resolved.Error);

        var (device, group) = resolved.Value;
        if (!group.Contains(channel.Index))
        {
            return Result<WiringPlan>.Failure(WiringErrors.ChannelNotDeclared(
                $"{device.Name}的「{group.Name}」只有 {group.Count} 路，没有第 {channel.Index} 路。"));
        }

        var wire = new Wire(WireId.New(), node, channel, group.Kind)
        {
            Purpose = Wire.NormalizeText(purpose),
            Label = Wire.NormalizeText(label),
        };

        var candidate = map.WithAdded([wire], DescribeChannel);
        return candidate.IsSuccess
            ? Result<WiringPlan>.Success(new WiringPlan(map, candidate.Value, [wire],
                $"在 {target} 接线「{wire.KindText}」：{DescribeChannel(channel)}"))
            : Result<WiringPlan>.Failure(candidate.Error);
    }

    public Result<WiringPlan> PlanBatch(WiringBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWritable() is { } blocked)
            return Result<WiringPlan>.Failure(blocked);

        var map = Current;
        if (map.Topology is not { } topology)
            return Result<WiringPlan>.Failure(WiringErrors.NoTopology());
        if (!topology.TryGetNode(request.Root, out var root))
            return Result<WiringPlan>.Failure(WiringErrors.NodeNotFound());

        // 通道组按驱动目录重新取：界面传来的声明可能已过期（驱动包被停用或升级）
        var sources = new List<ChannelSource>(request.Sources.Count);
        foreach (var source in request.Sources)
        {
            var resolved = ResolveGroup(source.Device, source.Group.Key);
            if (resolved.IsFailure)
                return Result<WiringPlan>.Failure(resolved.Error);
            sources.Add(source with { Group = resolved.Value.Group });
        }

        var expanded = WiringBatch.Expand(topology, request with { Sources = sources });
        if (expanded.IsFailure)
            return Result<WiringPlan>.Failure(expanded.Error);

        var candidate = map.WithAdded(expanded.Value, DescribeChannel);
        if (candidate.IsFailure)
            return Result<WiringPlan>.Failure(candidate.Error);

        var wires = expanded.Value;
        var operation = $"批量接线：{root} 下的 {wires.Count} 个{request.TargetLevel.DisplayName()}，" +
                        $"「{wires[0].KindText}」{DescribeChannel(wires[0].Channel)} 起至 {DescribeChannel(wires[^1].Channel)}";
        return Result<WiringPlan>.Success(new WiringPlan(map, candidate.Value, wires, operation));
    }

    // ==================== 写入 ====================

    public async Task<Result> ApplyAsync(WiringPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckWritable() is { } blocked)
                return Result.Failure(blocked);
            if (!ReferenceEquals(plan.Base, Current))
                return Result.Failure(WiringErrors.StalePlan());

            var saved = await Task.Run(() => _repository.AddRangeAsync(plan.Added, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                _logger.LogWarning("接线保存失败，接线保持不变。操作：{Operation}；原因：{Error}", plan.Operation, saved.Error);
                return saved;
            }

            Replace([.. Volatile.Read(ref _wires), .. plan.Added], plan.Candidate);
            _logger.LogInformation("接线已生效：{Operation}（共 {Count} 路）", plan.Operation, plan.Added.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return Result.Success();
    }

    public async Task<Result> UpdateAsync(WireId id, string? purpose, string? label, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckWritable() is { } blocked)
                return Result.Failure(blocked);

            var map = Current;
            var candidate = map.WithUpdated(id, purpose, label);
            if (candidate.IsFailure)
                return Result.Failure(candidate.Error);
            if (!candidate.Value.TryGet(id, out var updated))
                return Result.Failure(WiringErrors.WireNotFound());

            var saved = await Task.Run(() => _repository.UpdateAsync(updated, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return saved;

            Replace(Volatile.Read(ref _wires).Select(w => w.Id == id ? updated : w).ToList(), candidate.Value);
            _logger.LogInformation("修改接线：{Wire}，用途「{Purpose}」，线号「{Label}」", Describe(map, updated), updated.Purpose, updated.Label);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return Result.Success();
    }

    public async Task<Result<int>> RemoveAsync(IReadOnlyList<WireId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return Result<int>.Success(0);

        Result<int> deleted;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckWritable() is { } blocked)
                return Result<int>.Failure(blocked);

            var map = Current;
            deleted = await Task.Run(() => _repository.DeleteRangeAsync(ids, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (deleted.IsFailure)
                return deleted;

            var removed = ids.ToHashSet();
            foreach (var id in removed)
            {
                if (map.TryGet(id, out var wire))
                    _logger.LogInformation("拆除接线：{Wire}", Describe(map, wire));
            }

            Replace(Volatile.Read(ref _wires).Where(w => !removed.Contains(w.Id)).ToList(), map.WithRemoved(removed));
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return deleted;
    }

    // ==================== 模块配置对接线的依赖 ====================

    public int CountWires(IReadOnlyCollection<DeviceId> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var set = devices.ToHashSet();
        return Volatile.Read(ref _wires).Count(w => set.Contains(w.Channel.Device));
    }

    public Result CheckDriverChange(DeviceId device, DriverDescriptor driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var broken = new List<string>();
        foreach (var wire in Volatile.Read(ref _wires))
        {
            if (wire.Channel.Device != device)
                continue;

            var group = driver.FindChannelGroup(wire.Channel.Group);
            if (group is null || !group.Contains(wire.Channel.Index) || group.Kind != wire.Kind)
                broken.Add(DescribeChannel(wire.Channel));
        }

        return broken.Count == 0
            ? Result.Success()
            : Result.Failure(WiringErrors.ChannelsInUseByWires(
                $"「{driver.Name}」没有这些已接线的通道，或功能不同：{string.Join("、", broken.Take(WiringErrors.MaxListed))}" +
                $"{(broken.Count > WiringErrors.MaxListed ? $" 等 {broken.Count} 路" : string.Empty)}。请先在设备拓扑页拆除这些接线再更换驱动。"));
    }

    // ==================== 内部 ====================

    private Error? CheckWritable() => _loadError is { } error ? WiringErrors.LoadFailed(error) : null;

    /// <summary>
    /// 模块 → 驱动 → 通道组。驱动未加载或未声明该组时无法接线
    /// </summary>
    private Result<(DeviceDescriptor Device, ChannelGroup Group)> ResolveGroup(DeviceId deviceId, string? groupKey)
    {
        if (!_bindings.TryGet(deviceId, out var binding))
            return Result<(DeviceDescriptor, ChannelGroup)>.Failure(WiringErrors.DeviceNotFound());

        var device = binding.Device;
        if (!_catalog.TryGet(device.Driver, out var driver))
        {
            return Result<(DeviceDescriptor, ChannelGroup)>.Failure(WiringErrors.ChannelNotDeclared(
                $"{device.Name}的驱动「{device.Driver}」未加载，无法确认它有哪些通道。"));
        }

        var group = driver.FindChannelGroup(groupKey);
        return group is null
            ? Result<(DeviceDescriptor, ChannelGroup)>.Failure(WiringErrors.ChannelNotDeclared(
                $"{device.Name}（{driver.Name}）没有声明这组通道，不能接线。"))
            : Result<(DeviceDescriptor, ChannelGroup)>.Success((device, group));
    }

    private void Replace(IReadOnlyList<Wire> wires, WiringMap map)
    {
        lock (_sync)
        {
            Volatile.Write(ref _wires, wires);

            // 快照只在仍对应当前拓扑时直接采用；拓扑恰好在此期间被替换，下次读取会按新拓扑重建
            if (ReferenceEquals(map.Topology, _topology.Current))
            {
                _map = map;
                _mapWires = wires;
            }
        }
    }

    private string Describe(WiringMap map, Wire wire)
    {
        var place = map.Topology is { } topology && topology.TryGetNode(wire.Node, out var node) ? node.ToString() : $"节点 [{wire.Node}]";
        return $"{place}「{wire.KindText}」← {DescribeChannel(wire.Channel)} [{wire.Id}]";
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 订阅者（界面）出错不能让接线流程中断：变更已写入，只记录
            _logger.LogError(ex, "处理接线变化通知时出错");
        }
    }

    // 在绑定线程上调用：只投递信号，重新读取在后台监听任务中完成
    private void OnBindingsChanged(object? sender, Domain.Buses.BusId e) => _reloadSignals.Writer.TryWrite(true);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _reloadSignals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_reloadSignals.Reader.TryRead(out _))
                {
                    // 合并：一次重新读取覆盖之前积压的全部信号
                }

                // 只有接线引用了已不存在的模块时才需要重新读取（模块被删除，存储已级联删除其接线）
                if (Volatile.Read(ref _wires).Any(w => !_bindings.TryGet(w.Channel.Device, out _)))
                    await LoadAsync(cancellationToken).ConfigureAwait(false);
                else
                    RaiseChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 程序退出：正常结束监听
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "接线跟随模块变化的监听异常终止，之后删除模块不会再刷新接线");
        }
    }
}
