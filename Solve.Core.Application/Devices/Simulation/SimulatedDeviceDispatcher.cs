using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Simulation;

/// <summary>
/// 仿真模式下总线上的"设备"：把总线上发出的请求交给该总线各模块的驱动仿真设备（<see cref="ISimulatedDevice"/>），收集应答。
/// </summary>
/// <remarks>
/// <para>只在仿真模式下由组合根创建并接到仿真通道上；实机模式下不存在本对象，驱动的仿真代码不会被调用。</para>
/// <para>内核不解析报文：请求原样交给总线上每台仿真设备，由驱动按自己的协议认领。
/// 恰好一台应答时返回该应答；无人应答时返回 null（总线上没有响应，由执行器按超时处理）；
/// 多台同时应答视为总线冲突，返回 null 并告警，让地址或驱动配置问题暴露出来，而不是随机挑一台。</para>
/// <para>执行第三方代码的边界：创建仿真设备或应答时抛出的异常、超长应答，只让这一次请求没有应答并记日志，
/// 本方法本身不抛异常，不会让总线 I/O 失败。同一仿真设备连续出错只在第一次记录详细异常，恢复时再记录一次。</para>
/// <para>线程：不同总线可在各自线程上并发调用；同一仿真设备的调用由设备锁串行化（总线 I/O 本已串行，锁不会争用）。</para>
/// <para>仿真设备按模块绑定缓存：同一绑定沿用同一台仿真设备（保持输出开关等状态），
/// 绑定被替换（模块或总线配置变化、驱动重新载入）后重新创建；模块删除后在该总线下一次绑定变化时清理。</para>
/// </remarks>
public sealed class SimulatedDeviceDispatcher : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<DeviceId, Entry> _entries = new();

    // 正处于冲突状态的总线：冲突持续期间只告警一次，消除后再记录一次
    private readonly ConcurrentDictionary<BusId, bool> _collisions = new();

    private IDeviceBindings? _bindings;

    public SimulatedDeviceDispatcher(ILogger<SimulatedDeviceDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// 接上模块绑定。仿真通道先于绑定登记表创建（绑定依赖总线注册表），所以绑定在之后接上；接上之前的请求一律不应答。只调用一次。
    /// </summary>
    public void Attach(IDeviceBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (Interlocked.CompareExchange(ref _bindings, bindings, null) is not null)
            throw new InvalidOperationException("仿真设备分派已经接上模块绑定。");

        bindings.Changed += OnBindingsChanged;
    }

    /// <summary>
    /// 一条总线上发出了一帧请求，返回仿真设备的应答；没有（唯一的）应答时返回 null。不抛异常。
    /// </summary>
    public byte[]? Respond(BusId busId, ReadOnlySpan<byte> request)
    {
        try
        {
            return RespondCore(busId, request);
        }
        catch (Exception ex)
        {
            // 分派自身的缺陷同样不能让总线 I/O 失败：本次不应答，由执行器按超时处理
            _logger.LogError(ex, "总线 {BusId} 的仿真设备分派出错，本次请求不应答", busId);
            return null;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _bindings) is { } bindings)
            bindings.Changed -= OnBindingsChanged;

        _entries.Clear();
    }

    private byte[]? RespondCore(BusId busId, ReadOnlySpan<byte> request)
    {
        if (Volatile.Read(ref _bindings) is not { } bindings)
            return null;

        // 每台仿真设备都要收到请求（与真实总线一样，设备可能据此更新自己的状态），所以不在第一台应答后停下
        byte[]? reply = null;
        DriverBinding? first = null;
        DriverBinding? second = null;
        foreach (var binding in bindings.Get(busId))
        {
            if (Simulate(binding) is not { } entry)
                continue;

            if (entry.Respond(request, _logger) is not { } response)
                continue;

            if (first is null)
            {
                first = binding;
                reply = response;
            }
            else
            {
                second ??= binding;
            }
        }

        if (first is null)
            return null;

        if (second is not null)
        {
            if (_collisions.TryAdd(busId, true))
            {
                _logger.LogWarning("总线 {BusId} 仿真冲突：{First} 与 {Second} 同时应答同一请求，本次不应答。请检查模块地址与驱动配置",
                    busId, first.Device, second.Device);
            }

            return null;
        }

        if (_collisions.TryRemove(busId, out _))
            _logger.LogInformation("总线 {BusId} 仿真冲突已消除，由 {Device} 应答", busId, first.Device);

        return reply;
    }

    /// <summary>
    /// 取得绑定对应的仿真设备；绑定未就绪、驱动不提供仿真或创建失败时为 null
    /// </summary>
    private Entry? Simulate(DriverBinding binding)
    {
        if (binding.Status != DriverBindingStatus.Ready
            || binding.Context is not { } context
            || binding.Implementation is not IDeviceSimulatorProvider provider)
        {
            return null;
        }

        if (_entries.TryGetValue(binding.Device.Id, out var cached) && ReferenceEquals(cached.Binding, binding))
            return cached.Device is null ? null : cached;

        var entry = Create(binding, provider, context);
        _entries[binding.Device.Id] = entry;
        return entry.Device is null ? null : entry;
    }

    private Entry Create(DriverBinding binding, IDeviceSimulatorProvider provider, DeviceContext context)
    {
        try
        {
            var device = provider.CreateSimulator(context);
            if (device is null)
            {
                _logger.LogError("驱动 {Driver} 没有为{Device}返回仿真设备，该模块在仿真模式下不应答", binding.Device.Driver, binding.Device);
                return new Entry(binding, null);
            }

            _logger.LogInformation("仿真模式：{Device}由驱动 {Driver} 的仿真设备应答", binding.Device, binding.Device.Driver);
            return new Entry(binding, device);
        }
        catch (Exception ex)
        {
            // 缓存失败结果：同一绑定不反复创建、不刷屏；模块或驱动变化后会换新绑定再试
            _logger.LogError(ex, "驱动 {Driver} 为{Device}创建仿真设备失败，该模块在仿真模式下不应答", binding.Device.Driver, binding.Device);
            return new Entry(binding, null);
        }
    }

    // 在执行绑定的后台线程上调用：清理这条总线上已被替换或删除的模块的仿真设备
    private void OnBindingsChanged(object? sender, BusId busId)
    {
        if (Volatile.Read(ref _bindings) is not { } bindings)
            return;

        var current = bindings.Get(busId).ToDictionary(b => b.Device.Id);
        foreach (var pair in _entries)
        {
            if (pair.Value.Binding.Device.BusId != busId)
                continue;

            if (!current.TryGetValue(pair.Key, out var binding) || !ReferenceEquals(binding, pair.Value.Binding))
                _entries.TryRemove(pair);
        }

        _collisions.TryRemove(busId, out _);
    }

    private sealed class Entry(DriverBinding binding, ISimulatedDevice? device)
    {
        private readonly Lock _gate = new();
        private int _consecutiveFailures;

        public DriverBinding Binding { get; } = binding;

        public ISimulatedDevice? Device { get; } = device;

        public byte[]? Respond(ReadOnlySpan<byte> request, ILogger logger)
        {
            lock (_gate)
            {
                byte[]? response;
                try
                {
                    response = Device!.Respond(request);
                }
                catch (Exception ex)
                {
                    Fail(logger, ex, "应答时抛出异常");
                    return null;
                }

                if (response is { Length: > ISimulatedDevice.MaxResponseLength })
                {
                    Fail(logger, null, $"应答长度 {response.Length} 字节超过上限 {ISimulatedDevice.MaxResponseLength} 字节");
                    return null;
                }

                if (_consecutiveFailures > 0)
                {
                    logger.LogInformation("{Device}的仿真设备已恢复应答（此前连续出错 {Count} 次）", Binding.Device, _consecutiveFailures);
                    _consecutiveFailures = 0;
                }

                // 拷贝一份交给总线：驱动之后复用自己的缓冲区不会改写已送出的应答
                return response is { Length: > 0 } ? response.ToArray() : null;
            }
        }

        // 调用方持有 _gate
        private void Fail(ILogger logger, Exception? ex, string what)
        {
            if (_consecutiveFailures++ == 0)
                logger.LogError(ex, "{Device}的仿真设备{What}，本次请求不应答；连续出错期间不再重复记录", Binding.Device, what);
        }
    }
}
