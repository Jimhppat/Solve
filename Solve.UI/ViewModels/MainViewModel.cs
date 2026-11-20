using System;
using System.Threading.Tasks;
using Solve.Core.Application.Buses;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Logging;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Twin;

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
    public const int RuntimeLogNavIndex = 4;
    public const int SettingsNavIndex = 5;

    private Action<string>? _onEmergencyStop;
    private string _modeText = "仿真模式";
    private string _modeBadgeColor = "#F59E0B";
    private bool _isHardwareMode;
    private int _selectedNavIndex = 0;
    private ViewModelBase _currentPage;

    public MainViewModel()
    {
        MasterConsole = new MasterConsoleViewModel(openRuntimeLog: () => SelectedNavIndex = RuntimeLogNavIndex);
        DeviceLibrary = new DeviceLibraryViewModel();
        BusConsole = new BusConsoleViewModel { DeviceLibrary = DeviceLibrary };
        Topology = new TopologyViewModel();
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
        private set => SetProperty(ref _isHardwareMode, value);
    }

    public string StatusSummaryText => BusConsole.StatusSummaryText;

    public RelayCommand EmergencyStopCommand { get; }

    private void OnNavIndexChanged(int index)
    {
        CurrentPage = index switch
        {
            MasterConsoleNavIndex => MasterConsole,
            BusConsoleNavIndex => BusConsole,
            TopologyNavIndex => Topology,
            RuntimeLogNavIndex => RuntimeLog,
            SettingsNavIndex => Settings,
            _ => MasterConsole // 后续报文诊断（3）页面在此接入对应子 ViewModel
        };
    }

    /// <summary>
    /// 接入设备拓扑服务（拓扑页：建档、扩缩容、节点浏览）。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachTopology(ITopologyService topology) => Topology.Attach(topology);

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

    private void EmergencyStop()
    {
        _onEmergencyStop?.Invoke("操作员手动触发工作台紧急跳闸");
    }

    public void Dispose()
    {
        MasterConsole.Dispose();
        BusConsole.Dispose();
        RuntimeLog.Dispose();
    }
}
