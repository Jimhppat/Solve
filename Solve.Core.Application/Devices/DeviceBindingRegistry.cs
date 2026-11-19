using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Common;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
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
    /// 按存储中的模块与总线当前配置重新绑定一条总线。模块增删改后调用。
    /// </summary>
    Task RefreshAsync(BusId busId, CancellationToken cancellationToken);
}

/// <summary>
/// 绑定登记表：启动时绑定全部总线；随后跟随总线注册表（新增、重配置、删除）与模块的增删改重新绑定。
/// </summary>
/// <remarks>
/// <para>线程：重新绑定互斥执行（同一时刻只有一次），结果整体替换为不可变快照，读取无锁且不会读到一半的结果。</para>
/// <para>失败处理：读取模块失败时保留该总线上一次的绑定并记录错误，不清空，避免一次读库失败让设备全部"消失"。</para>
/// <para>只绑定、不通信：拿到的会话交给后续的轮询调度使用；重新绑定会重建会话，会话内的协议状态从头开始。</para>
/// </remarks>
public sealed class DeviceBindingRegistry : IDeviceBindings, IAsyncDisposable
{
    private readonly IDeviceRepository _repository;
    private readonly IBusManagementService _buses;
    private readonly DriverBinder _binder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private ImmutableDictionary<BusId, IReadOnlyList<DriverBinding>> _bindings = ImmutableDictionary<BusId, IReadOnlyList<DriverBinding>>.Empty;
    private EventSubscription<BusRegistryEvent>? _subscription;
    private Task? _listener;

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

    public IReadOnlyList<DriverBinding> Get(BusId busId)
        => Volatile.Read(ref _bindings).TryGetValue(busId, out var bindings) ? bindings : [];

    /// <summary>
    /// 绑定当前已注册的全部总线，并开始跟随总线注册表的变化。只调用一次。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            throw new InvalidOperationException("绑定登记表已经启动。");

        // 快照与订阅原子完成：快照里的总线不会再收到 BusRegistered，之后新增的一定会收到
        _subscription = _buses.SubscribeRegistry(out var current);
        foreach (var runtime in current)
            await RefreshAsync(runtime.Id, cancellationToken).ConfigureAwait(false);

        _listener = Task.Run(() => ListenAsync(_subscription.Reader, _lifetime.Token), CancellationToken.None);
    }

    public async Task RefreshAsync(BusId busId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buses.GetRuntime(busId) is not { IsSuccess: true } runtime)
            {
                Remove(busId);
                return;
            }

            var listed = await _repository.ListByBusAsync(busId, cancellationToken).ConfigureAwait(false);
            if (listed.IsFailure)
            {
                _logger.LogError("读取总线 {BusId} 的模块失败，保留上一次的绑定：{Error}", busId, listed.Error);
                return;
            }

            var link = LinkOf(runtime.Value.Descriptor);
            var bindings = listed.Value.Select(device => _binder.Bind(device, link)).ToList();
            Volatile.Write(ref _bindings, _bindings.SetItem(busId, bindings));

            _logger.LogInformation("总线 {BusId} 绑定模块 {Total} 个：就绪 {Ready}，未就绪 {NotReady}",
                busId, bindings.Count, bindings.Count(b => b.Status == DriverBindingStatus.Ready),
                bindings.Count(b => b.Status is not (DriverBindingStatus.Ready or DriverBindingStatus.NoProtocol)));
            foreach (var binding in bindings.Where(b => b.Status is not (DriverBindingStatus.Ready or DriverBindingStatus.NoProtocol)))
                _logger.LogWarning("{Device} 未就绪：{Status} {Problem}", binding.Device, binding.Status, binding.Problem);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _subscription?.Dispose();
        if (_listener is not null)
            await _listener.ConfigureAwait(false);

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

    private void Remove(BusId busId)
    {
        if (Volatile.Read(ref _bindings).ContainsKey(busId))
        {
            Volatile.Write(ref _bindings, _bindings.Remove(busId));
            _logger.LogInformation("总线 {BusId} 已不存在，移除其模块绑定", busId);
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
