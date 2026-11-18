using System;
using System.Threading.Tasks;
using Solve.Core.Application.Buses;

namespace Solve.UI.ViewModels;

/// <summary>
/// 上位机主窗口宿主视图模型（Shell Host）：负责窗口级安全策略、全局模式指示、全站急停与多页面导航路由
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private Action<string>? _onEmergencyStop;
    private string _modeText = "仿真模式";
    private string _modeBadgeColor = "#F59E0B";
    private bool _isHardwareMode;
    private int _selectedNavIndex = 0;
    private ViewModelBase _currentPage;

    public MainViewModel()
    {
        BusConsole = new BusConsoleViewModel();
        _currentPage = BusConsole;

        // 联动总线控制台的实时统计文本到主窗口底部状态栏
        BusConsole.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusConsole.StatusSummaryText))
                OnPropertyChanged(nameof(StatusSummaryText));
        };

        EmergencyStopCommand = new RelayCommand(EmergencyStop);
    }

    /// <summary>
    /// 总线控制台专属子页面
    /// </summary>
    public BusConsoleViewModel BusConsole { get; }

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
            0 => BusConsole,
            _ => BusConsole // 后续拓扑、诊断、设置页面在此接入对应子 ViewModel
        };
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
        BusConsole.Dispose();
    }
}
