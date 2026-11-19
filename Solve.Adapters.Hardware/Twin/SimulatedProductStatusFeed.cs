using Microsoft.Extensions.Logging;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.Adapters.Hardware.Twin;

/// <summary>
/// 仿真产品状态源：为主控台提供会变化的假产品状态，用于压测显示区性能、演示界面。不访问任何设备。
/// </summary>
/// <remarks>
/// <para>目前没有接入启动流程：正式运行时不显示任何假数据，产品位保持默认的空闲，直到接入真实数据源。
/// 需要压测时在组合根里创建并 <see cref="Start"/>。</para>
/// <para>每个周期做两件事：按轮转对一部分产品位重发当前状态（心跳，保证每个产品位在
/// <see cref="HeartbeatRounds"/> 个周期内至少上报一次，更新时间不会停住，将来加超时判断时可直接压测）；再随机推进少量产品位的状态。</para>
/// <para>状态推进只是演示用的近似流程（空闲 → 待机 → 良品 / 不良品 → 空闲），不代表真实测试规则。</para>
/// <para>在线程池上运行，由 <see cref="PeriodicTimer"/> 驱动；单个周期出错只记录、不终止循环。</para>
/// </remarks>
public sealed class SimulatedProductStatusFeed : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 心跳轮转的周期数：250 ms × 16 = 4 s 内每个产品位至少上报一次
    /// </summary>
    public const int HeartbeatRounds = 16;

    /// <summary>
    /// 每个周期推进状态的产品位比例
    /// </summary>
    private const double TransitionRatio = 0.02;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly TwinStateStore _store;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _loop;
    private int _heartbeatCursor;
    private long _serialSequence;

    public SimulatedProductStatusFeed(
        TwinStateStore store,
        ILogger<SimulatedProductStatusFeed> logger,
        TimeSpan? interval = null,
        int? seed = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_interval, TimeSpan.Zero, nameof(interval));

        _store = store;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _random = seed is { } value ? new Random(value) : new Random();
    }

    /// <summary>
    /// 启动后台循环，幂等
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            if (_loop is not null)
                return;

            _loop = Task.Run(() => RunAsync(_stop.Token));
        }

        _logger.LogInformation("仿真产品状态源已启动，周期 {Interval}", _interval);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
                return;

            _stop.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("仿真产品状态源未在 {Timeout} 内停止", StopTimeout);
            }
        }

        _stop.Dispose();
        _logger.LogInformation("仿真产品状态源已停止");
    }

    /// <summary>
    /// 执行一个周期。后台循环按周期调用；测试可直接调用，不经过计时器。不可与后台循环并发调用。
    /// </summary>
    public void Tick()
    {
        var products = _store.ProductIds;
        if (products.Count == 0)
            return;

        // 心跳：轮转重发当前状态
        var slice = (products.Count + HeartbeatRounds - 1) / HeartbeatRounds;
        for (var i = 0; i < slice; i++)
        {
            var id = products[_heartbeatCursor++ % products.Count];
            if (_store.TryGetProduct(id, out var current))
                _store.Report(id, current.State, current.SerialNumber);
        }

        _heartbeatCursor %= products.Count;

        // 推进：随机挑一小部分产品位走到下一状态
        var transitions = Math.Max(1, (int)(products.Count * TransitionRatio));
        for (var i = 0; i < transitions; i++)
        {
            var id = products[_random.Next(products.Count)];
            if (!_store.TryGetProduct(id, out var current))
                continue;

            var next = Next(current.State);
            var serial = next switch
            {
                ProductState.Idle => null,
                ProductState.Standby => $"SIM{Interlocked.Increment(ref _serialSequence):D8}",
                _ => current.SerialNumber,
            };
            _store.Report(id, next, serial);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 仿真源出错不能拖垮程序，但必须留痕；下个周期继续
                    _logger.LogError(ex, "仿真产品状态源本周期出错");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停止
        }
    }

    private ProductState Next(ProductState state)
    {
        var roll = _random.NextDouble();
        return state switch
        {
            ProductState.Idle => ProductState.Standby,
            // 待机的产品约一半在下一次被挑中时出结果，约 1/10 为不良品
            ProductState.Standby => roll switch
            {
                < 0.05 => ProductState.Defective,
                < 0.50 => ProductState.Good,
                _ => ProductState.Standby,
            },
            _ => ProductState.Idle,
        };
    }
}
