using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Solve.Adapters.Hardware.Buses;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Adapters.Hardware.Drivers;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Settings;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Adapters.Persistence.Topology;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Logging;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Desktop.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Solve.Desktop.Composition;

/// <summary>
/// 组合根：唯一决定"用哪个实现"的地方，并负责启动顺序与退出时的资源释放。
/// </summary>
/// <remarks>
/// <para>启动（<see cref="Start"/>，线程池执行，不阻塞 UI）：
/// 打开配置库 → 读取设备拓扑并创建孪生状态库 → 读取 bus.hardware_enabled → 双重确认决定仿真/实机
/// → 创建 BusManager → 加载并注册总线。
/// 不自动连接任何总线；拓扑读取失败只让拓扑功能不可用，不阻止总线启动。</para>
/// <para>故障安全：配置库打不开时不创建任何总线；产品状态暂未接入数据源（仿真与实机模式都不提供假数据），
/// 产品位保持默认的空闲；进程级未处理异常先对全部总线安全跳闸再刷写日志。</para>
/// <para>退出（<see cref="DisposeAsync"/>）：取消启动 → 释放 BusManager（关闭全部物理通道）→ 刷写并关闭日志。</para>
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>
    /// 退出时等待仍在进行的启动流程的上限（配置库命令超时为 5 s）
    /// </summary>
    private static readonly TimeSpan StartupStopTimeout = TimeSpan.FromSeconds(6);

    private readonly LoggingPipeline _logging;
    private readonly SerilogLoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();

    private Task<StartupReport>? _startup;
    private BusManager? _buses;
    private BusManagementService? _busService;
    private DeviceService? _devices;
    private TopologyService? _topology;
    private TwinStateStore? _twin;
    private bool _disposed;

    private AppHost(DesktopOptions options)
    {
        Options = options;

        // 日志目录与保留设置要在创建日志管线之前确定，所以设置单独存文件、不放配置库（配置库在启动流程中才打开）
        var settingsStore = options.LogLocationPath is null ? null : new LogSettingsStore(options.LogLocationPath);
        var logSettings = LogSettingsService.ResolveStartup(options.LogDirectory, settingsStore);

        _logging = LoggingPipeline.Create(logSettings.Directory, options.LoggingSettingsPath, logSettings.Retention);

        // 历史日志跟随管线当前正在写的目录：设置页切换目录后，按日期查看与导出读的是新目录
        LogArchive = new FileLogArchive(() => _logging.LogDirectory);

        // 日志器的生命周期归管线所有：工厂释放时不连带释放，由 _logging 统一刷写
        _loggerFactory = new SerilogLoggerFactory(_logging.Logger, dispose: false);
        _logger = _loggerFactory.CreateLogger<AppHost>();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _logger.LogInformation("程序启动 进程={ProcessId} 实机参数={HardwareRequested} 配置库={Database} 日志={Logs}（{Retention}）",
            Environment.ProcessId, options.HardwareRequested, options.DatabasePath, logSettings.Directory, logSettings.Retention);

        if (logSettings.FallbackReason is { } fallbackReason)
        {
            _logger.LogWarning("本次运行日志设置部分改用默认值，写入 {Directory}（{Retention}）。{Reason}",
                logSettings.Directory, logSettings.Retention, fallbackReason);
        }

        if (_logging.SettingsError is { } settingsError)
        {
            _logger.LogWarning("日志级别配置 {Path} 无效，本次运行使用默认级别：{Error}",
                options.LoggingSettingsPath, settingsError);
        }

        LogSettings = new LogSettingsService(
            _logging, settingsStore, options.LogDirectory, logSettings, _loggerFactory.CreateLogger<LogSettingsService>());
    }

    /// <summary>
    /// 创建组合根并初始化日志。不做任何设备或数据库 I/O。
    /// </summary>
    public static AppHost Create(DesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AppHost(options);
    }

    public DesktopOptions Options { get; }

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public ILogger Logger => _logger;

    /// <summary>
    /// 黑匣子日志管线；供接入框架日志（如 Avalonia）等需要 Serilog 根日志器的场合
    /// </summary>
    public LoggingPipeline Logging => _logging;

    /// <summary>
    /// 按日期保存的历史日志，供运行日志页查看与导出；始终读取当前日志目录
    /// </summary>
    public ILogArchive LogArchive { get; }

    /// <summary>
    /// 日志目录、保存天数与单个文件上限的查看与保存，供设置页使用
    /// </summary>
    public ILogSettings LogSettings { get; }

    /// <summary>
    /// 总线注册表；启动完成前或配置库不可用时为 null
    /// </summary>
    public BusManager? Buses
    {
        get
        {
            lock (_gate)
            {
                return _buses;
            }
        }
    }

    /// <summary>
    /// 总线管理用例服务；启动完成前或配置库不可用时为 null
    /// </summary>
    public IBusManagementService? BusService
    {
        get
        {
            lock (_gate)
            {
                return _busService;
            }
        }
    }

    /// <summary>
    /// 总线挂载设备用例服务；启动完成前或配置库不可用时为 null
    /// </summary>
    public IDeviceService? Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    /// <summary>
    /// 设备拓扑用例服务；启动读取完成前或配置库不可用时为 null。读取失败时不为 null，失败原因见其 LoadError
    /// </summary>
    public ITopologyService? Topology
    {
        get
        {
            lock (_gate)
            {
                return _topology;
            }
        }
    }

    /// <summary>
    /// 数字孪生状态库（主控台显示产品状态）；配置库不可用时为 null。跟随拓扑服务的当前拓扑
    /// </summary>
    public TwinStateStore? Twin
    {
        get
        {
            lock (_gate)
            {
                return _twin;
            }
        }
    }

    /// <summary>
    /// 在线程池上启动总线系统，幂等。返回的任务不会以异常结束，结果见 <see cref="StartupReport"/>。
    /// </summary>
    public Task<StartupReport> Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startup ??= Task.Run(() => StartCoreAsync(_lifetime.Token));
        }
    }

    /// <summary>
    /// 系统级急停：对全部已注册总线安全跳闸。任何线程可调用，返回跳闸的总线数。
    /// </summary>
    public int EmergencyStop(string reason) => Buses?.SafetyTripAll(reason) ?? 0;

    /// <summary>
    /// 供 Main 在 UI 消息循环结束后调用的同步关停入口，最长等待 <paramref name="timeout"/>。
    /// </summary>
    /// <remarks>
    /// 放到线程池执行再等待：直接在 UI 线程上同步等待异步释放，续体回投 UI 同步上下文会造成死锁。
    /// </remarks>
    public bool Shutdown(TimeSpan timeout) => Task.Run(() => DisposeAsync().AsTask()).Wait(timeout);

    public async ValueTask DisposeAsync()
    {
        Task<StartupReport>? startup;
        lock (_gate)
        {
            if (_disposed)
                return;

            // 置位后启动流程不会再发布新的 BusManager（见 StartCoreAsync），之后取出的就是最终实例
            _disposed = true;
            startup = _startup;
        }

        _lifetime.Cancel();

        if (startup is not null)
        {
            try
            {
                await startup.WaitAsync(StartupStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("启动流程未在 {Timeout} 内结束，继续关停", StartupStopTimeout);
            }
        }

        BusManager? buses;
        lock (_gate)
        {
            buses = _buses;
            _buses = null;
            _busService = null;
            _topology = null;
            _twin = null;
        }

        if (buses is not null)
        {
            try
            {
                await buses.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 关停路径上不能再抛：后面还要刷写日志。记录后继续，进程退出时操作系统会回收句柄
                _logger.LogCritical(ex, "释放总线时发生异常");
            }
        }

        _logger.LogInformation("程序退出，组合根已释放");

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        // 刷写异步队列中尚未落盘的日志并关闭文件
        _loggerFactory.Dispose();
        _logging.Dispose();
        _lifetime.Dispose();
    }

    // ==================== 启动流程 ====================

    private async Task<StartupReport> StartCoreAsync(CancellationToken cancellationToken)
    {
        TransportModeDecision? mode = null;
        try
        {
            var database = await SqliteDatabase.OpenAsync(
                new SqliteStorageOptions(Options.DatabasePath),
                _loggerFactory.CreateLogger<SqliteDatabase>(),
                cancellationToken).ConfigureAwait(false);

            if (database.IsFailure)
            {
                _logger.LogCritical("配置库不可用，本次运行不创建任何总线：{Error}", database.Error);
                return StartupReport.Failed(null, database.Error);
            }

            // 设备拓扑先于总线读取：将来总线通道要映射到拓扑节点。读取失败已由服务记录并禁止保存，这里继续启动总线
            var topology = new TopologyService(
                new SqliteTopologyRepository(database.Value, _loggerFactory.CreateLogger<SqliteTopologyRepository>()),
                _loggerFactory.CreateLogger<TopologyService>());
            await topology.LoadAsync(cancellationToken).ConfigureAwait(false);
            // 孪生状态库跟随拓扑服务：之后的扩缩容、改名、停用都自动生效，产品位状态按 Id 保留
            var twin = new TwinStateStore(topology, _loggerFactory.CreateLogger<TwinStateStore>());
            lock (_gate)
            {
                if (!_disposed)
                {
                    _topology = topology;
                    _twin = twin;
                }
            }

            var settings = new SqliteAppSettingsStore(database.Value, _loggerFactory.CreateLogger<SqliteAppSettingsStore>());
            var stored = await settings.GetAsync(TransportModePolicy.HardwareEnabledKey, cancellationToken).ConfigureAwait(false);
            mode = TransportModePolicy.Decide(Options.HardwareRequested, stored);
            LogMode(mode);

            // 产品状态数据源尚未接入：不运行任何假数据，产品位保持默认的空闲
            _logger.LogInformation("产品状态暂未接入数据源，主控台的产品位保持默认的空闲");

            var manager = new BusManager(
                CreateTransportFactory(mode.Mode),
                new NoSignalSafetyInterlock(_loggerFactory.CreateLogger<NoSignalSafetyInterlock>()),
                _loggerFactory);

            if (!TryPublish(manager))
            {
                await manager.DisposeAsync().ConfigureAwait(false);
                return StartupReport.Failed(mode, Cancelled());
            }

            var repository = new SqliteBusConfigurationRepository(
                database.Value, _loggerFactory.CreateLogger<SqliteBusConfigurationRepository>());

            var service = new BusManagementService(
                manager, repository, _loggerFactory.CreateLogger<BusManagementService>());

            // 挂载模块只是配置，不访问硬件；与总线共用配置库，删除总线时其下模块由库级联删除。
            // 驱动目录是代码常量，构造时校验，登记错误在启动时就暴露
            var devices = new DeviceService(
                new SqliteDeviceRepository(database.Value, _loggerFactory.CreateLogger<SqliteDeviceRepository>()),
                new BuiltInDriverCatalog(),
                _loggerFactory.CreateLogger<DeviceService>());
            lock (_gate)
            {
                _busService = service;
                _devices = devices;
            }

            var snapshot = await repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.IsFailure)
            {
                _logger.LogCritical("读取总线配置失败，本次运行没有任何总线：{Error}", snapshot.Error);
                return StartupReport.Failed(mode, snapshot.Error);
            }

            var failures = new List<BusRegistrationFailure>();
            foreach (var descriptor in snapshot.Value.Descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 只注册，不连接：连接由操作员手动发起
                var registered = manager.Register(descriptor);
                if (registered.IsFailure)
                {
                    failures.Add(new BusRegistrationFailure(descriptor.Id, descriptor.Name, registered.Error));
                    _logger.LogError("总线 {BusId} {Name} 注册失败：{Error}", descriptor.Id, descriptor.Name, registered.Error);
                }
            }

            var report = new StartupReport(
                mode,
                snapshot.Value.Descriptors.Count - failures.Count,
                snapshot.Value.Issues,
                failures,
                Fatal: null);

            _logger.LogInformation("总线系统启动完成 模式={Mode} 已注册={Registered} 配置无效={Issues} 注册失败={Failures}",
                mode.Mode, report.RegisteredCount, report.ConfigurationIssues.Count, report.RegistrationFailures.Count);
            return report;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && ex is OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogWarning("程序退出，总线系统启动被中止");
            return StartupReport.Failed(mode, Cancelled());
        }
        catch (Exception ex)
        {
            // 启动任务的边界：记录并以报告返回，不让异常变成未观察的任务异常
            _logger.LogCritical(ex, "总线系统启动发生未处理异常");
            return StartupReport.Failed(mode, BusErrors.UnhandledException(ex));
        }
    }

    private IBusTransportFactory CreateTransportFactory(TransportMode mode) => mode switch
    {
        TransportMode.Hardware => new HardwareTransportFactory(_loggerFactory),
        _ => new SimulatedTransportFactory(),
    };

    private bool TryPublish(BusManager manager)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            _buses = manager;
            return true;
        }
    }

    private void LogMode(TransportModeDecision mode)
    {
        if (mode.Mode == TransportMode.Hardware)
            _logger.LogWarning("传输模式：实机（{Reason}）。连接操作将访问真实串口 / TCP 设备", mode.Reason);
        else if (Options.HardwareRequested)
            _logger.LogWarning("传输模式：仿真。已请求实机但未获授权：{Reason}", mode.Reason);
        else
            _logger.LogInformation("传输模式：仿真（{Reason}）", mode.Reason);
    }

    private static Error Cancelled() => new("App.StartupCancelled", "程序正在退出，启动被中止。");

    // ==================== 进程级异常 ====================

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        _logger.LogCritical(e.ExceptionObject as Exception,
            "进程级未处理异常（即将终止={Terminating}），对全部总线执行安全跳闸", e.IsTerminating);

        var tripped = EmergencyStop("进程级未处理异常");
        _logger.LogCritical("已安全跳闸 {Count} 条总线", tripped);

        if (e.IsTerminating)
        {
            // 进程即将结束，来不及走正常关停：立即刷写异步日志队列，保住事故现场
            _logging.Dispose();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "未观察的任务异常");
    }
}
