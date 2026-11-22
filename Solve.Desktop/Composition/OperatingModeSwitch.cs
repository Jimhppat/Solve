using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Settings;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Composition;

/// <summary>
/// 生产 / 仿真切换：只改配置库里的 bus.hardware_enabled，下次启动由 <see cref="TransportModePolicy"/> 照常双重确认。
/// </summary>
/// <remarks>
/// <para>--hardware 是本机的实机许可：没带它启动时不允许保存"生产"，否则重启后策略仍会落到仿真，界面与实际不符。</para>
/// <para>只在全部总线断开（含故障、安全停止这类通道已释放的锁存态）时允许切换：
/// 不替操作员隐式断开或卸载设备，现场状态由操作员自己先收拾好。</para>
/// <para>保存后回读校验，值不一致不重启，避免重启后落到意料之外的模式。</para>
/// </remarks>
public sealed class OperatingModeSwitch : IOperatingModeSwitch
{
    private readonly bool _hardwareRequested;
    private readonly IAppSettingsStore _settings;
    private readonly BusManager _buses;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _restartPending;

    public OperatingModeSwitch(
        TransportMode currentMode,
        bool hardwareRequested,
        IAppSettingsStore settings,
        BusManager buses,
        ILogger<OperatingModeSwitch> logger)
    {
        _hardwareRequested = hardwareRequested;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _buses = buses ?? throw new ArgumentNullException(nameof(buses));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CurrentMode = currentMode == TransportMode.Hardware ? OperatingMode.Production : OperatingMode.Simulation;
    }

    public OperatingMode CurrentMode { get; }

    public string? ProductionBlockedReason => _hardwareRequested
        ? null
        : $"本次启动未带 {DesktopOptions.HardwareArgument} 参数，本机不允许切换到生产模式。需要连接实机时，请用带 {DesktopOptions.HardwareArgument} 的方式启动程序。";

    public bool IsRestartPending => _restartPending;

    public async Task<Result> SaveAsync(OperatingMode target, CancellationToken cancellationToken)
    {
        if (_restartPending)
            return Result.Failure(OperatingModeErrors.RestartPending());

        if (target == CurrentMode)
            return Result.Failure(OperatingModeErrors.Unchanged(target));

        if (target == OperatingMode.Production && ProductionBlockedReason is { } blocked)
            return Result.Failure(OperatingModeErrors.ProductionNotAllowed(blocked));

        // 不排队：重复点击直接拒绝，而不是在后面再保存一次
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result.Failure(OperatingModeErrors.InProgress());

        try
        {
            if (_restartPending)
                return Result.Failure(OperatingModeErrors.RestartPending());

            var active = _buses.Buses
                .Where(b => IsChannelInUse(b.State))
                .Select(b => b.Descriptor.Name)
                .ToList();
            if (active.Count > 0)
            {
                _logger.LogWarning("拒绝切换到{Target}模式：{Count} 条总线仍在使用通道（{Buses}）",
                    OperatingModeErrors.Describe(target), active.Count, string.Join("、", active));
                return Result.Failure(OperatingModeErrors.BusesActive(active));
            }

            var value = target == OperatingMode.Production ? TransportModePolicy.EnabledValue : TransportModePolicy.DisabledValue;
            var saved = await _settings.SetAsync(TransportModePolicy.HardwareEnabledKey, value, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                _logger.LogError("保存运行模式失败，保持{Current}模式：{Error}", OperatingModeErrors.Describe(CurrentMode), saved.Error);
                return saved;
            }

            var readBack = await _settings.GetAsync(TransportModePolicy.HardwareEnabledKey, cancellationToken).ConfigureAwait(false);
            if (readBack.IsFailure || !string.Equals(readBack.Value, value, StringComparison.Ordinal))
            {
                var detail = readBack.IsFailure ? readBack.Error.Message : $"期望 '{value}'，读到 '{readBack.Value ?? "（未设置）"}'";
                _logger.LogError("运行模式保存后回读不一致，不重启：{Detail}", detail);
                return Result.Failure(OperatingModeErrors.VerifyFailed(detail));
            }

            _restartPending = true;
            _logger.LogWarning("操作员将运行模式由{Current}切换为{Target}（{Key}={Value}），程序将释放全部通道后重启生效",
                OperatingModeErrors.Describe(CurrentMode), OperatingModeErrors.Describe(target),
                TransportModePolicy.HardwareEnabledKey, value);
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 物理通道正在打开、使用或关闭的状态。Faulted / SafeStop 已释放通道，属于可切换的锁存态
    /// </summary>
    private static bool IsChannelInUse(BusState state)
        => state is BusState.Connecting or BusState.Connected or BusState.Reconnecting or BusState.Disconnecting;
}
