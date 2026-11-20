using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Runtime;

/// <summary>
/// 轮询调度参数。默认值为现场常用量级，可按需调整。
/// </summary>
public sealed record DevicePollingOptions
{
    public static DevicePollingOptions Default { get; } = new();

    /// <summary>
    /// 一轮轮询的目标周期：一轮内依次轮询总线上的每个就绪模块，耗时不足周期时补足等待（默认 1 s）
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 连续暂时性失败达到该次数后，模块转为通信异常并开始退避（默认 3）
    /// </summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>
    /// 通信异常时的最长重试间隔（默认 30 s）；从一个轮询周期起按 2 倍递增
    /// </summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 设备报告故障后的重试间隔（默认 10 s）
    /// </summary>
    public TimeSpan DeviceFaultRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 总线未连接时检查其状态的间隔（默认 250 ms）
    /// </summary>
    public TimeSpan IdleRecheck { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// 轮询调度：每条已注册的总线一个轮询循环。总线已连接时，按地址顺序轮询其上驱动就绪的模块，
/// 更新模块通信状态与点位实时值；总线未连接时只等待。
/// </summary>
/// <remarks>
/// <para>线程：每条总线一个后台循环，彼此独立；同一条总线上的交互经总线运行时的通道锁串行执行。</para>
/// <para>重试：按 SDK 的失败分类处理——暂时性失败连续达到阈值后转通信异常并指数退避；配置错误不再重试，
/// 直到模块被重新绑定（配置修改后）；设备故障按较长间隔重试。总线本身不可用时不计入模块失败。</para>
/// <para>驱动异常：按 SDK 约定，驱动抛出异常即视为通信状态未知，总线被安全跳闸，循环记录后继续等待总线复位。</para>
/// </remarks>
public sealed class DevicePollingService : IAsyncDisposable
{
    private readonly IBusManagementService _buses;
    private readonly IDeviceBindings _bindings;
    private readonly ExchangeExecutor _executor;
    private readonly ModuleStatusStore _store;
    private readonly DevicePollingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<BusId, (CancellationTokenSource Stop, Task Loop)> _loops = [];

    private EventSubscription<BusRegistryEvent>? _subscription;
    private Task? _listener;
    private bool _disposed;

    public DevicePollingService(
        IBusManagementService buses,
        IDeviceBindings bindings,
        ExchangeExecutor executor,
        ModuleStatusStore store,
        DevicePollingOptions options,
        TimeProvider time,
        ILogger<DevicePollingService> logger)
    {
        ArgumentNullException.ThrowIfNull(buses);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _buses = buses;
        _bindings = bindings;
        _executor = executor;
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// 为当前已注册的总线启动轮询循环，并跟随总线的新增与删除。只调用一次。
    /// </summary>
    public void Start()
    {
        if (_subscription is not null)
            throw new InvalidOperationException("轮询调度已经启动。");

        _subscription = _buses.SubscribeRegistry(out var current);
        foreach (var runtime in current)
            StartLoop(runtime.Id);

        _listener = Task.Run(() => ListenAsync(_subscription.Reader, _lifetime.Token), CancellationToken.None);
        _logger.LogInformation("轮询调度已启动：总线 {Count} 条，周期 {Interval} ms", current.Count, _options.PollInterval.TotalMilliseconds);
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> loops;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            loops = _loops.Values.Select(l => l.Loop).ToList();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _subscription?.Dispose();

        var all = loops.Concat(_listener is null ? [] : [_listener]);
        await Task.WhenAll(all).ConfigureAwait(false);

        foreach (var (stop, _) in _loops.Values)
            stop.Dispose();
        _lifetime.Dispose();
    }

    // ==================== 循环的增减 ====================

    private void StartLoop(BusId busId)
    {
        lock (_gate)
        {
            if (_disposed || _loops.ContainsKey(busId))
                return;

            var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _loops[busId] = (stop, Task.Run(() => RunBusAsync(busId, stop.Token), CancellationToken.None));
        }
    }

    private async Task StopLoopAsync(BusId busId)
    {
        (CancellationTokenSource Stop, Task Loop) loop;
        lock (_gate)
        {
            if (!_loops.Remove(busId, out loop))
                return;
        }

        await loop.Stop.CancelAsync().ConfigureAwait(false);
        await loop.Loop.ConfigureAwait(false);
        loop.Stop.Dispose();
        _store.RemoveExcept(busId, new HashSet<DeviceId>());
    }

    private async Task ListenAsync(ChannelReader<BusRegistryEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    switch (evt)
                    {
                        case BusRegistered registered:
                            StartLoop(registered.BusId);
                            break;
                        case BusUnregistered unregistered:
                            await StopLoopAsync(unregistered.BusId).ConfigureAwait(false);
                            break;

                        // 重配置不用处理：循环每轮都取最新的运行时与绑定
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 程序退出：正常结束监听
        }
        catch (Exception ex)
        {
            // 监听循环不能悄悄消失：之后新增的总线不会被轮询，必须留下记录
            _logger.LogCritical(ex, "轮询调度的总线监听异常终止，之后新增的总线不会被轮询");
        }
    }

    // ==================== 一条总线的轮询循环 ====================

    /// <summary>
    /// 一个模块的轮询进度。绑定对象变化（模块或总线被修改后重新绑定）时整体重置。
    /// </summary>
    private sealed class Tracker(DriverBinding binding, DateTimeOffset now)
    {
        public DriverBinding Binding { get; } = binding;

        public int Failures { get; set; }

        public DateTimeOffset NextDue { get; set; } = now;

        public ModuleStatus? Status { get; set; }
    }

    private async Task RunBusAsync(BusId busId, CancellationToken cancellationToken)
    {
        var trackers = new Dictionary<DeviceId, Tracker>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_buses.GetRuntime(busId) is not { IsSuccess: true } found)
                    return;

                var runtime = found.Value;
                var bindings = _bindings.Get(busId);
                Sync(busId, trackers, bindings);

                if (runtime.State != BusState.Connected)
                {
                    MarkWaiting(trackers, "总线未连接。");
                    await Task.Delay(_options.IdleRecheck, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var started = _time.GetUtcNow();
                foreach (var binding in bindings)
                {
                    if (binding.Status != DriverBindingStatus.Ready || !trackers.TryGetValue(binding.Device.Id, out var tracker))
                        continue;
                    if (_time.GetUtcNow() < tracker.NextDue)
                        continue;

                    var outcome = await PollAsync(runtime, binding, cancellationToken).ConfigureAwait(false);

                    // 总线本身出了问题（断线、跳闸、被占用）：结束本轮，按总线状态重新判断
                    if (outcome is ExchangeOutcome.BusUnavailable unavailable)
                    {
                        _logger.LogDebug("总线 {BusId} 轮询中断：{Error}", busId, unavailable.Error);
                        break;
                    }

                    Apply(tracker, outcome);
                }

                var elapsed = _time.GetUtcNow() - started;
                var wait = _options.PollInterval - elapsed;
                await Task.Delay(wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 总线被删除或程序退出：正常结束
        }
        catch (Exception ex)
        {
            // 循环自身的缺陷（不是驱动异常，驱动异常在 PollAsync 内处理）：这条总线停止轮询，必须让人看见
            _logger.LogCritical(ex, "总线 {BusId} 的轮询循环异常终止，该总线不再轮询", busId);
        }
    }

    /// <summary>
    /// 按最新绑定同步各模块的轮询进度：移除已不存在的模块；绑定对象变化的模块重置；未就绪的模块标为不轮询
    /// </summary>
    private void Sync(BusId busId, Dictionary<DeviceId, Tracker> trackers, IReadOnlyList<DriverBinding> bindings)
    {
        var current = bindings.Select(b => b.Device.Id).ToHashSet();
        foreach (var gone in trackers.Keys.Where(id => !current.Contains(id)).ToList())
            trackers.Remove(gone);
        _store.RemoveExcept(busId, current);

        foreach (var binding in bindings)
        {
            if (trackers.TryGetValue(binding.Device.Id, out var tracker) && ReferenceEquals(tracker.Binding, binding))
                continue;

            tracker = new Tracker(binding, _time.GetUtcNow());
            trackers[binding.Device.Id] = tracker;
            Publish(tracker, binding.Status == DriverBindingStatus.Ready
                ? Status(binding, ModuleCommState.Waiting, "等待轮询。", null, 0, [])
                : Status(binding, ModuleCommState.NotPolled, binding.Problem, null, 0, []));
        }
    }

    private void MarkWaiting(Dictionary<DeviceId, Tracker> trackers, string reason)
    {
        foreach (var tracker in trackers.Values)
        {
            if (tracker.Binding.Status != DriverBindingStatus.Ready
                || tracker.Status is { State: ModuleCommState.Waiting } waiting && waiting.Problem == reason)
                continue;

            // 重新连上后从头计数：断线期间的失败不算模块的错
            tracker.Failures = 0;
            tracker.NextDue = _time.GetUtcNow();
            var previous = tracker.Status;
            Publish(tracker, Status(tracker.Binding, ModuleCommState.Waiting, reason, previous?.LastSuccess, 0, previous?.Points ?? []));
        }
    }

    private async Task<ExchangeOutcome> PollAsync(BusRuntime runtime, DriverBinding binding, CancellationToken cancellationToken)
    {
        var label = $"{binding.Device} 轮询";
        IExchange exchange;
        try
        {
            exchange = binding.Session!.BeginPoll();
        }
        catch (Exception ex)
        {
            // SDK 约定：驱动抛异常即通信状态未知。这一步在事务外，由这里执行与事务内相同的安全跳闸
            _logger.LogCritical(ex, "{Label}：驱动 {Driver} 开始轮询时抛出异常，安全跳闸总线", label, binding.Device.Driver);
            runtime.SafetyTrip($"驱动 {binding.Device.Driver} 抛出异常 {ex.GetType().Name}");
            return new ExchangeOutcome.BusUnavailable(BusErrors.SafetyTripped($"驱动 {binding.Device.Driver} 抛出异常"));
        }

        try
        {
            return await _executor.ExecuteAsync(runtime, exchange, label, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 总线在轮询途中被删除
            return new ExchangeOutcome.BusUnavailable(BusErrors.NotRegistered(runtime.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 事务内的驱动异常：总线运行时已安全跳闸后原样抛出，这里只记录
            _logger.LogCritical(ex, "{Label}：驱动 {Driver} 在交互中抛出异常，总线已安全跳闸", label, binding.Device.Driver);
            return new ExchangeOutcome.BusUnavailable(BusErrors.SafetyTripped($"驱动 {binding.Device.Driver} 抛出异常"));
        }
    }

    private void Apply(Tracker tracker, ExchangeOutcome outcome)
    {
        var binding = tracker.Binding;
        var now = _time.GetUtcNow();
        var previous = tracker.Status;

        switch (outcome)
        {
            case ExchangeOutcome.Succeeded succeeded:
                tracker.Failures = 0;
                tracker.NextDue = now;
                Publish(tracker, Status(binding, ModuleCommState.Online, null, now, 0, ToPoints(binding, succeeded.Samples, now)));
                break;

            case ExchangeOutcome.DeviceFailed { Kind: FailureKind.Configuration } failed:
                tracker.NextDue = DateTimeOffset.MaxValue;
                Publish(tracker, Status(binding, ModuleCommState.ConfigError, failed.Reason, previous?.LastSuccess, tracker.Failures, previous?.Points ?? []));
                break;

            case ExchangeOutcome.DeviceFailed { Kind: FailureKind.Device } failed:
                tracker.NextDue = now + _options.DeviceFaultRetry;
                Publish(tracker, Status(binding, ModuleCommState.DeviceFault, failed.Reason, previous?.LastSuccess, tracker.Failures, previous?.Points ?? []));
                break;

            case ExchangeOutcome.DeviceFailed failed:
                tracker.Failures++;
                var faulted = tracker.Failures >= _options.FailureThreshold;
                tracker.NextDue = faulted ? now + Backoff(tracker.Failures - _options.FailureThreshold) : now;

                // 未到阈值前保持原状态（偶发一次超时不让界面闪烁），只更新失败原因与次数
                var state = faulted ? ModuleCommState.CommFault : previous?.State ?? ModuleCommState.Waiting;
                Publish(tracker, Status(binding, state, failed.Reason, previous?.LastSuccess, tracker.Failures, previous?.Points ?? []));
                break;
        }
    }

    /// <summary>
    /// 第 n 次退避（从 0 起）：一个轮询周期 × 2^n，封顶 <see cref="DevicePollingOptions.MaxBackoff"/>
    /// </summary>
    internal TimeSpan Backoff(int n)
    {
        var ticks = _options.PollInterval.Ticks * Math.Pow(2, Math.Min(n, 30));
        return ticks >= _options.MaxBackoff.Ticks ? _options.MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// 只接受驱动声明过的点位、与声明相符的通道号与有限数值；其余丢弃并告警（驱动缺陷，不能当成设备数据，
    /// 通道号错位会把一路通道的读数显示到另一个位置上）
    /// </summary>
    private IReadOnlyList<PointValue> ToPoints(DriverBinding binding, IReadOnlyList<PointSample> samples, DateTimeOffset now)
    {
        var driver = binding.Driver;
        var points = new List<PointValue>(samples.Count);
        foreach (var sample in samples)
        {
            if (driver is not null && IsDeclared(driver, sample) && double.IsFinite(sample.Value))
            {
                points.Add(new PointValue(sample.Key, sample.Value, now, sample.Channel));
            }
            else
            {
                _logger.LogWarning("{Device} 的驱动上报了无效点位 {Key}[{Channel}]={Value}，已丢弃",
                    binding.Device, sample.Key, sample.Channel, sample.Value);
            }
        }

        return points;
    }

    /// <summary>
    /// 点位已声明；模块级点位通道号为 0，通道级点位通道号在所属组的 1..数量 之内
    /// </summary>
    public static bool IsDeclared(DriverInfo driver, PointSample sample)
    {
        if (sample.Key is null)
            return false;

        foreach (var point in driver.Points)
        {
            if (!string.Equals(point.Key, sample.Key, StringComparison.Ordinal))
                continue;

            if (point.Group is null)
                return sample.Channel == 0;

            foreach (var group in driver.Channels)
            {
                if (string.Equals(group.Key, point.Group, StringComparison.Ordinal))
                    return sample.Channel >= 1 && sample.Channel <= group.Count;
            }

            return false;
        }

        return false;
    }

    private void Publish(Tracker tracker, ModuleStatus status)
    {
        var previous = tracker.Status;
        tracker.Status = status;
        _store.Set(status);

        if (previous?.State == status.State)
            return;

        // 状态迁移留痕：在线与恢复记为信息，异常记为警告
        if (status.State is ModuleCommState.CommFault or ModuleCommState.ConfigError or ModuleCommState.DeviceFault)
            _logger.LogWarning("{Device} 通信状态 {From} → {To}：{Problem}", tracker.Binding.Device, previous?.State, status.State, status.Problem);
        else
            _logger.LogInformation("{Device} 通信状态 {From} → {To}", tracker.Binding.Device, previous?.State, status.State);
    }

    private static ModuleStatus Status(
        DriverBinding binding,
        ModuleCommState state,
        string? problem,
        DateTimeOffset? lastSuccess,
        int failures,
        IReadOnlyList<PointValue> points)
        => new(binding.Device.Id, binding.Device.BusId, state, problem, lastSuccess, failures, points);
}
