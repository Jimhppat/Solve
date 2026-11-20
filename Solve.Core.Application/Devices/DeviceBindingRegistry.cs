using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Common;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 每条总线上各模块的当前绑定（模块 ↔ 驱动会话）
/// </summary>
public interface IDeviceBindings
{
    /// <summary>
    /// 一条总线上各模块的绑定，按地址升序；总线不存在或尚未绑定时为空列表。可被任意线程读取。
    /// </summary>
    IReadOnlyList<DriverBinding> Get(BusId busId);

    /// <summary>
    /// 全部总线上使用指定驱动的模块数。可被任意线程读取。
    /// </summary>
    int CountByDriver(DriverId driver);

    /// <summary>
    /// 按存储中的模块与总线当前配置重新绑定一条总线。模块增删改后调用。
    /// </summary>
    Task RefreshAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 一条总线的绑定被替换之后触发（参数为总线），在执行绑定的后台线程上调用。界面处理程序须自行切回 UI 线程。
    /// </summary>
    event EventHandler<BusId>? Changed;
}

/// <summary>
/// 绑定登记表：启动时绑定全部总线；随后跟随总线注册表（新增、重配置、删除）、模块的增删改与驱动目录的变化重新绑定。
/// </summary>
/// <remarks>
/// <para>线程：重新绑定互斥执行（同一时刻只有一次），结果整体替换为不可变快照，读取无锁且不会读到一半的结果。</para>
/// <para>增量：重新绑定时，配置没变、驱动实现也没变的模块原样保留原绑定（会话、协议状态、轮询计数都不重建）；
/// 驱动目录变化时只重新绑定含过期绑定的总线。这样载入新驱动包不会打断其他模块的通信。</para>
/// <para>失败处理：读取模块失败时保留该总线上一次的绑定并记录错误，不清空，避免一次读库失败让设备全部"消失"。</para>
/// <para>只绑定、不通信：拿到的会话交给轮询调度使用。</para>
/// </remarks>
public sealed class DeviceBindingRegistry : IDeviceBindings, IAsyncDisposable
{
    private readonly IDeviceRepository _repository;
    private readonly IBusManagementService _buses;
    private readonly DriverBinder _binder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // 驱动目录变化的信号：容量 1、满则丢弃，连续多次变化合并成一次全量检查
    private readonly Channel<bool> _catalogSignals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private ImmutableDictionary<BusId, IReadOnlyList<DriverBinding>> _bindings = ImmutableDictionary<BusId, IReadOnlyList<DriverBinding>>.Empty;
    private EventSubscription<BusRegistryEvent>? _subscription;
    private Task? _listener;
    private Task? _catalogListener;

    public DeviceBindingRegistry(
        IDeviceRepository repository,
        IBusManagementService buses,
        DriverBinder binder,
        ILogger<DeviceBindingRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(buses);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _buses = buses;
        _binder = binder;
        _logger = logger;
    }

    public event EventHandler<BusId>? Changed;

    public IReadOnlyList<DriverBinding> Get(BusId busId)
        => Volatile.Read(ref _bindings).TryGetValue(busId, out var bindings) ? bindings : [];

    public int CountByDriver(DriverId driver)
        => Volatile.Read(ref _bindings).Values.Sum(list => list.Count(b => b.Device.Driver == driver));

    /// <summary>
    /// 绑定当前已注册的全部总线，并开始跟随总线注册表与驱动目录的变化。只调用一次。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            throw new InvalidOperationException("绑定登记表已经启动。");

        // 先订阅目录变化再做首次绑定：首次绑定期间目录变了，之后的全量检查会补上
        _binder.Catalog.Changed += OnCatalogChanged;

        // 快照与订阅原子完成：快照里的总线不会再收到 BusRegistered，之后新增的一定会收到
        _subscription = _buses.SubscribeRegistry(out var current);
        foreach (var runtime in current)
            await RefreshAsync(runtime.Id, cancellationToken).ConfigureAwait(false);

        _listener = Task.Run(() => ListenAsync(_subscription.Reader, _lifetime.Token), CancellationToken.None);
        _catalogListener = Task.Run(() => ListenCatalogAsync(_lifetime.Token), CancellationToken.None);
    }

    public async Task RefreshAsync(BusId busId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buses.GetRuntime(busId) is not { IsSuccess: true } runtime)
            {
                if (Remove(busId))
                    RaiseChanged(busId);
                return;
            }

            var listed = await _repository.ListByBusAsync(busId, cancellationToken).ConfigureAwait(false);
            if (listed.IsFailure)
            {
                _logger.LogError("读取总线 {BusId} 的模块失败，保留上一次的绑定：{Error}", busId, listed.Error);
                return;
            }

            var link = LinkOf(runtime.Value.Descriptor);
            var previous = Get(busId).ToDictionary(b => b.Device.Id);
            var reused = 0;
            var bindings = new List<DriverBinding>(listed.Value.Count);
            foreach (var device in listed.Value)
            {
                if (previous.TryGetValue(device.Id, out var old) && CanReuse(old, device, link))
                {
                    bindings.Add(old);
                    reused++;
                }
                else
                {
                    bindings.Add(_binder.Bind(device, link));
                }
            }

            Volatile.Write(ref _bindings, _bindings.SetItem(busId, bindings));

            _logger.LogInformation("总线 {BusId} 绑定模块 {Total} 个（沿用 {Reused} 个）：就绪 {Ready}，未就绪 {NotReady}",
                busId, bindings.Count, reused, bindings.Count(b => b.Status == DriverBindingStatus.Ready),
                bindings.Count(b => b.Status is not (DriverBindingStatus.Ready or DriverBindingStatus.NoProtocol)));
            foreach (var binding in bindings.Where(b => b.Status is not (DriverBindingStatus.Ready or DriverBindingStatus.NoProtocol)))
                _logger.LogWarning("{Device} 未就绪：{Status} {Problem}", binding.Device, binding.Status, binding.Problem);

            RaiseChanged(busId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _binder.Catalog.Changed -= OnCatalogChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _catalogSignals.Writer.TryComplete();
        _subscription?.Dispose();
        if (_listener is not null)
            await _listener.ConfigureAwait(false);
        if (_catalogListener is not null)
            await _catalogListener.ConfigureAwait(false);

        _lifetime.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 链路类型只看总线的物理链路：串口总线为 Serial，TCP 客户端总线为 Tcp
    /// </summary>
    internal static LinkKind LinkOf(BusDescriptor descriptor) => descriptor switch
    {
        SerialBusDescriptor => LinkKind.Serial,
        TcpBusDescriptor => LinkKind.Tcp,
        _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.GetType().Name, "未知的总线类型。"),
    };

    /// <summary>
    /// 原绑定可以沿用：模块配置完全相同（含参数）、链路类型没变、驱动实现没变
    /// </summary>
    private bool CanReuse(DriverBinding old, DeviceDescriptor device, LinkKind link)
        => old.Device == device
           && (old.Context is null || old.Context.Link == link)
           && _binder.IsCurrent(old);

    private bool Remove(BusId busId)
    {
        if (!Volatile.Read(ref _bindings).ContainsKey(busId))
            return false;

        Volatile.Write(ref _bindings, _bindings.Remove(busId));
        _logger.LogInformation("总线 {BusId} 已不存在，移除其模块绑定", busId);
        return true;
    }

    private void RaiseChanged(BusId busId)
    {
        try
        {
            Changed?.Invoke(this, busId);
        }
        catch (Exception ex)
        {
            // 订阅者（界面）出错不能让绑定流程中断：绑定结果已写入，只记录
            _logger.LogError(ex, "处理总线 {BusId} 绑定变化通知时出错", busId);
        }
    }

    // 在替换目录的线程上调用：只投递信号，实际的重新绑定在后台监听任务中串行完成
    private void OnCatalogChanged(object? sender, EventArgs e) => _catalogSignals.Writer.TryWrite(true);

    private async Task ListenCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _catalogSignals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // 合并：一次全量检查覆盖之前积压的全部信号
                while (_catalogSignals.Reader.TryRead(out _))
                {
                    // 只是取空队列
                }

                // 只重新绑定含过期绑定的总线；其余总线上的会话与轮询完全不受影响
                var stale = Volatile.Read(ref _bindings)
                    .Where(pair => pair.Value.Any(b => !_binder.IsCurrent(b)))
                    .Select(pair => pair.Key)
                    .ToList();

                _logger.LogInformation("驱动目录已变化，重新绑定受影响的总线 {Count} 条", stale.Count);
                foreach (var busId in stale)
                    await RefreshAsync(busId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 程序退出：正常结束监听
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "驱动目录变化监听异常终止，之后载入或停用驱动不会再触发重新绑定");
        }
    }

    // 退订或登记表释放后队列完成 / 被取消，循环自然退出
    private async Task ListenAsync(ChannelReader<BusRegistryEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    // 新增与重配置（链路类型可能变了）都按最新配置重新绑定；删除时 RefreshAsync 发现总线已不存在即移除
                    await RefreshAsync(evt.BusId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 程序退出：正常结束监听
        }
        catch (Exception ex)
        {
            // 监听循环不能悄悄消失：绑定会停在过期的状态上，必须留下记录
            _logger.LogCritical(ex, "模块绑定监听异常终止，之后的总线变化不会再触发重新绑定");
        }
    }
}
