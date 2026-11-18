using System;

namespace Solve.UI.ViewModels;

/// <summary>
/// 主控台视图模型（Master Console / Dashboard ViewModel）：
/// 负责主控台全局监视、系统状态汇总与核心控制指令调度。
/// </summary>
public sealed class MasterConsoleViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 右栏最近日志保留的条数：卡片一次只显示几条，留一些余量供向上翻看，完整记录在运行日志页
    /// </summary>
    public const int RecentLogCapacity = 100;

    private string _pageTitle = "主控台";
    private string _systemStatusText = "系统待命";
    private bool _isDisposed;

    /// <summary>
    /// XAML 设计器使用：没有导航宿主，「全部」按钮不可用
    /// </summary>
    public MasterConsoleViewModel() : this(null)
    {
    }

    /// <param name="openRuntimeLog">切换到运行日志页；由主窗口宿主提供</param>
    public MasterConsoleViewModel(Action? openRuntimeLog)
    {
        // 默认不显示调试：现场把某个来源调到 Debug 排查问题时，调试日志不会刷满主控台；需要时可在卡片上勾选
        RecentLog = new RuntimeLogViewModel(RecentLogCapacity, LogCategories.Operational);
        OpenRuntimeLogCommand = new RelayCommand(() => openRuntimeLog?.Invoke(), () => openRuntimeLog is not null);
    }

    /// <summary>
    /// 页面标题
    /// </summary>
    public string PageTitle
    {
        get => _pageTitle;
        set => SetProperty(ref _pageTitle, value);
    }

    /// <summary>
    /// 系统总体运行状态摘要
    /// </summary>
    public string SystemStatusText
    {
        get => _systemStatusText;
        set => SetProperty(ref _systemStatusText, value);
    }

    /// <summary>
    /// 右栏的最近日志。与运行日志页各自订阅，筛选与清空互不影响
    /// </summary>
    public RuntimeLogViewModel RecentLog { get; }

    public RelayCommand OpenRuntimeLogCommand { get; }

    /// <summary>
    /// 资源确定性释放，防止长时间运行下的内存与事件泄漏
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        RecentLog.Dispose();
    }
}
