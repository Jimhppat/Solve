using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Solve.Core.Application.Buses;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Logging;
using Solve.Core.Application.Settings;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Twin;
using Solve.Core.Application.Wiring;

namespace Solve.UI.ViewModels;

/// <summary>
/// 上位机主窗口宿主视图模型（Shell Host）：负责窗口级安全策略、全局模式指示、全站急停与多页面导航路由
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    // 侧栏导航索引，与 MainWindow.axaml 中 ListBoxItem 的顺序一一对应（含暂未启用的项）
    public const int MasterConsoleNavIndex = 0;
    public const int BusConsoleNavIndex = 1;
    public const int TopologyNavIndex = 2;
    public const int HardwareMappingNavIndex = 3;
    public const int RuntimeLogNavIndex = 5;
    public const int SettingsNavIndex = 6;

    private Action<string>? _onEmergencyStop;
    private string _modeText = "仿真模式";
    private string _modeBadgeColor = "#F59E0B";
    private bool _isHardwareMode;
    private int _selectedNavIndex = 0;
    private ViewModelBase _currentPage;
    private IOperatingModeSwitch? _modeSwitch;
    private Func<bool>? _requestRestart;
    private OperatingMode? _pendingModeTarget;
    private string? _modeDialogError;
    private bool _isModeSaving;

    public MainViewModel()
    {
        MasterConsole = new MasterConsoleViewModel(openRuntimeLog: () => SelectedNavIndex = RuntimeLogNavIndex);
        DeviceLibrary = new DeviceLibraryViewModel();
        BusConsole = new BusConsoleViewModel { DeviceLibrary = DeviceLibrary };
        Topology = new TopologyViewModel();
        HardwareMapping = new HardwareMappingViewModel();
        RuntimeLog = new RuntimeLogViewModel();
        Settings = new SettingsViewModel(onLogDirectoryChanged: OnLogDirectoryChanged);
        _currentPage = MasterConsole;

        // 联动总线控制台的实时统计文本到主窗口底部状态栏
        BusConsole.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusConsole.StatusSummaryText))
                OnPropertyChanged(nameof(StatusSummaryText));
        };

        EmergencyStopCommand = new RelayCommand(EmergencyStop);
        CancelModeSwitchCommand = new RelayCommand(CancelModeSwitch);
        ConfirmModeSwitchCommand = new AsyncRelayCommand(ConfirmModeSwitchAsync, () => CanConfirmModeSwitch && !_isModeSaving);
    }

    /// <summary>
    /// 主控台专属子页面
    /// </summary>
    public MasterConsoleViewModel MasterConsole { get; }

    /// <summary>
    /// 总线控制台专属子页面
    /// </summary>
    public BusConsoleViewModel BusConsole { get; }

    /// <summary>
    /// 设备拓扑页。由组合根在配置库就绪后接入拓扑服务，设计器中不可编辑
    /// </summary>
    public TopologyViewModel Topology { get; }

    /// <summary>
    /// 硬件映射页：把总线上模块的通道关联到位置（拓扑节点）
    /// </summary>
    public HardwareMappingViewModel HardwareMapping { get; }

    /// <summary>
    /// 驱动库（扫描、导入、信任驱动包，启停驱动）。不在侧栏导航中，从模块配置窗口的「驱动库」按钮打开独立窗口
    /// </summary>
    public DeviceLibraryViewModel DeviceLibrary { get; }

    /// <summary>
    /// 运行日志页。由组合根接入日志源，设计器中为空列表
    /// </summary>
    public RuntimeLogViewModel RuntimeLog { get; }

    /// <summary>
    /// 系统设置页
    /// </summary>
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// 当前活动页面（驱动主窗体 ContentControl 呈现）
    /// </summary>
    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    /// <summary>
    /// 侧边栏导航选中索引
    /// </summary>
    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            if (SetProperty(ref _selectedNavIndex, value))
            {
                OnNavIndexChanged(value);
            }
        }
    }

    public string ModeText
    {
        get => _modeText;
        private set => SetProperty(ref _modeText, value);
    }

    public string ModeBadgeColor
    {
        get => _modeBadgeColor;
        private set => SetProperty(ref _modeBadgeColor, value);
    }

    public bool IsHardwareMode
    {
        get => _isHardwareMode;
        private set
        {
            if (SetProperty(ref _isHardwareMode, value))
                OnPropertyChanged(nameof(IsProductionSelected));
        }
    }

    public string StatusSummaryText => BusConsole.StatusSummaryText;

    public RelayCommand EmergencyStopCommand { get; }

    // ==================== 生产 / 仿真切换 ====================

    /// <summary>
    /// 状态栏滑块的位置，始终反映本次运行实际生效的模式。
    /// 操作员拨动时只打开确认弹窗，滑块随即弹回实际模式；确认保存后程序重启，新进程按新模式启动。
    /// </summary>
    public bool IsProductionSelected
    {
        get => _modeSwitch?.CurrentMode == OperatingMode.Production || _modeSwitch is null && IsHardwareMode;
        set
        {
            var target = value ? OperatingMode.Production : OperatingMode.Simulation;
            if (_modeSwitch is not null && target != _modeSwitch.CurrentMode && CanSwitchMode)
                OpenModeDialog(target);

            // 不在 setter 里同步回弹：控件正处于自身的属性变更中，投递到下一轮再通知，滑块才会可靠地回到实际位置
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsProductionSelected)));
        }
    }

    /// <summary>
    /// 滑块可用：模式切换已接入，且没有正在处理或等待重启的切换
    /// </summary>
    public bool CanSwitchMode => _modeSwitch is { IsRestartPending: false } && !_isModeSaving;

    /// <summary>
    /// 等待确认的目标模式；非空时显示切换弹窗
    /// </summary>
    public OperatingMode? PendingModeTarget
    {
        get => _pendingModeTarget;
        private set
        {
            if (SetProperty(ref _pendingModeTarget, value))
            {
                OnPropertyChanged(nameof(IsModeDialogOpen));
                OnPropertyChanged(nameof(IsModeDialogProduction));
                OnPropertyChanged(nameof(IsModeDialogBlocked));
                OnPropertyChanged(nameof(CanConfirmModeSwitch));
                OnPropertyChanged(nameof(ModeDialogTitle));
                OnPropertyChanged(nameof(ModeDialogMessage));
                OnPropertyChanged(nameof(ModeDialogCancelText));
                ConfirmModeSwitchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsModeDialogOpen => _pendingModeTarget is not null;

    /// <summary>
    /// 目标是生产（危险样式）
    /// </summary>
    public bool IsModeDialogProduction => _pendingModeTarget == OperatingMode.Production;

    /// <summary>
    /// 本机不允许切到目标模式：弹窗只说明原因，不提供确认按钮
    /// </summary>
    public bool IsModeDialogBlocked => _pendingModeTarget == OperatingMode.Production && _modeSwitch?.ProductionBlockedReason is not null;

    public bool CanConfirmModeSwitch => IsModeDialogOpen && !IsModeDialogBlocked;

    public string ModeDialogTitle => _pendingModeTarget switch
    {
        OperatingMode.Production when IsModeDialogBlocked => "不能切换到生产模式",
        OperatingMode.Production => "切换到生产模式？",
        OperatingMode.Simulation => "切换到仿真模式？",
        _ => string.Empty,
    };

    public string ModeDialogMessage => _pendingModeTarget switch
    {
        OperatingMode.Production when _modeSwitch?.ProductionBlockedReason is { } reason => reason,
        OperatingMode.Production =>
            "程序将释放全部通道后自动重启。重启后连接总线会访问真实串口 / TCP 设备，下发的命令直接作用到实机。请确认现场设备与人员处于安全状态。",
        OperatingMode.Simulation =>
            "程序将释放全部通道后自动重启。重启后总线只连接仿真设备，不会访问任何实机。",
        _ => string.Empty,
    };

    public string ModeDialogCancelText => IsModeDialogBlocked ? "知道了" : "取消";

    /// <summary>
    /// 保存失败的原因（如仍有总线在连接）；弹窗保持打开，操作员处理后可再次确认
    /// </summary>
    public string? ModeDialogError
    {
        get => _modeDialogError;
        private set => SetProperty(ref _modeDialogError, value);
    }

    public RelayCommand CancelModeSwitchCommand { get; }

    public AsyncRelayCommand ConfirmModeSwitchCommand { get; }

    private void OnNavIndexChanged(int index)
    {
        CurrentPage = index switch
        {
            MasterConsoleNavIndex => MasterConsole,
            BusConsoleNavIndex => BusConsole,
            TopologyNavIndex => Topology,
            HardwareMappingNavIndex => HardwareMapping,
            RuntimeLogNavIndex => RuntimeLog,
            SettingsNavIndex => Settings,
            _ => MasterConsole // 后续报文诊断（4）页面在此接入对应子 ViewModel
        };

        // 位置结构在拓扑页维护：每次进入映射页按当前拓扑与接线刷新
        if (index == HardwareMappingNavIndex)
            HardwareMapping.Refresh();
    }

    /// <summary>
    /// 接入设备拓扑服务（拓扑页：建档、扩缩容、节点浏览）。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachTopology(ITopologyService topology)
    {
        Topology.Attach(topology);
        HardwareMapping.AttachTopology(topology);
    }

    /// <summary>
    /// 接入总线挂载设备服务（总线页的设备页）。须在 UI 线程上调用。
    /// </summary>
    public void AttachDevices(IDeviceService devices)
    {
        BusConsole.AttachDevices(devices);
        DeviceLibrary.AttachDevices(devices);
    }

    /// <summary>
    /// 接入驱动库。须在 UI 线程上调用。
    /// </summary>
    public void AttachDeviceLibrary(IDeviceLibrary library) => DeviceLibrary.Attach(library);

    /// <summary>
    /// 接入驱动库目录设置（设置页的「驱动库」卡片）。须在 UI 线程上调用。
    /// </summary>
    public void AttachLibraryLocation(IDeviceLibraryLocation location) => Settings.AttachLibraryLocation(location);

    /// <summary>
    /// 接入孪生状态库（主控台显示区）。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachTwin(TwinStateStore twin) => MasterConsole.Twin.Attach(twin);

    /// <summary>
    /// 接入接线运行视图：硬件映射页、拓扑页删除位置前的接线数提示、主控台产品信息卡片的硬件状态。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    /// <param name="commands">按位置下发命令；为 null 时接线面板不显示命令按钮</param>
    public void AttachWiring(WiringInspector wiring, DeviceCommandService? commands = null)
    {
        Topology.AttachWiring(wiring);
        HardwareMapping.AttachWiring(wiring, commands);
        MasterConsole.Twin.AttachWiring(wiring);
    }

    /// <summary>
    /// 接入日志源：运行日志页与主控台的最近日志各自订阅。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    /// <param name="archive">历史日志；只接给运行日志页，主控台的最近日志只看实时</param>
    public void AttachLogFeed(ILogFeed feed, string logDirectory, ILogArchive? archive = null)
    {
        RuntimeLog.Attach(feed, logDirectory, archive);
        MasterConsole.RecentLog.Attach(feed, logDirectory);
    }

    /// <summary>
    /// 接入日志设置（设置页：存储路径、保存天数、单个文件上限）。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachLogSettings(ILogSettings settings) => Settings.AttachLogSettings(settings);

    // 设置页切换目录后，日志页与主控台的「打开日志目录」随之指向新目录；日志页的按日期查看改读新目录
    private void OnLogDirectoryChanged(string logDirectory)
    {
        RuntimeLog.UpdateLogDirectory(logDirectory);
        MasterConsole.RecentLog.UpdateLogDirectory(logDirectory);
    }

    /// <summary>
    /// 后端组合根启动完成后初始化接入真实操作台
    /// </summary>
    public async Task InitializeAsync(
        IBusManagementService busService,
        string modeText,
        bool isHardware,
        Action<string> onEmergencyStop)
    {
        _onEmergencyStop = onEmergencyStop;

        ModeText = modeText;
        IsHardwareMode = isHardware;
        ModeBadgeColor = isHardware ? "#EF4444" : "#F59E0B";

        await BusConsole.InitializeAsync(busService);
    }

    /// <summary>
    /// 显示启动阶段的问题。启动失败时总线服务可能为空、<see cref="InitializeAsync"/> 不会被调用，仍需单独展示。
    /// </summary>
    public void ShowStartupNotice(StartupNotice notice) => BusConsole.ShowStartupNotice(notice);

    /// <summary>
    /// 接入生产 / 仿真切换。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    /// <param name="requestRestart">模式保存成功后发起受控重启；返回 false 表示重启请求被拒绝</param>
    public void AttachModeSwitch(IOperatingModeSwitch modeSwitch, Func<bool> requestRestart)
    {
        ArgumentNullException.ThrowIfNull(modeSwitch);
        ArgumentNullException.ThrowIfNull(requestRestart);
        if (_modeSwitch is not null)
            throw new InvalidOperationException("模式切换已接入。");

        _modeSwitch = modeSwitch;
        _requestRestart = requestRestart;
        OnPropertyChanged(nameof(IsProductionSelected));
        OnPropertyChanged(nameof(CanSwitchMode));
    }

    private void OpenModeDialog(OperatingMode target)
    {
        ModeDialogError = null;
        PendingModeTarget = target;
    }

    private void CancelModeSwitch()
    {
        if (_isModeSaving)
            return;

        ModeDialogError = null;
        PendingModeTarget = null;
    }

    private async Task ConfirmModeSwitchAsync()
    {
        if (_modeSwitch is not { } modeSwitch || PendingModeTarget is not { } target || _isModeSaving)
            return;

        SetModeSaving(true);
        ModeDialogError = null;
        try
        {
            // 保存要读写配置库（同步磁盘 I/O），放到线程池；限时，避免弹窗无限等待
            using var timeout = new CancellationTokenSource(ModeSaveTimeout);
            var saved = await Task.Run(() => modeSwitch.SaveAsync(target, timeout.Token));
            if (saved.IsFailure)
            {
                ModeDialogError = saved.Error.Message;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            ModeDialogError = $"保存运行模式超时（{ModeSaveTimeout.TotalSeconds:0} 秒），模式未改变。";
            return;
        }
        finally
        {
            SetModeSaving(false);
        }

        PendingModeTarget = null;
        ModeText = $"正在切换到{OperatingModeErrors.Describe(target)}模式，程序即将重启…";
        OnPropertyChanged(nameof(CanSwitchMode));

        if (_requestRestart?.Invoke() != true)
            ModeText = $"已保存为{OperatingModeErrors.Describe(target)}模式，但未能自动重启，请手动重启程序";
    }

    private void SetModeSaving(bool saving)
    {
        _isModeSaving = saving;
        OnPropertyChanged(nameof(CanSwitchMode));
        ConfirmModeSwitchCommand.RaiseCanExecuteChanged();
    }

    private static readonly TimeSpan ModeSaveTimeout = TimeSpan.FromSeconds(15);

    private void EmergencyStop()
    {
        _onEmergencyStop?.Invoke("操作员手动触发工作台紧急跳闸");
    }

    public void Dispose()
    {
        MasterConsole.Dispose();
        BusConsole.Dispose();
        HardwareMapping.Dispose();
        RuntimeLog.Dispose();
    }
}
