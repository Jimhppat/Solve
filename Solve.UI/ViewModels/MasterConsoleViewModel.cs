using System;

namespace Solve.UI.ViewModels;

/// <summary>
/// 主控台视图模型（Master Console / Dashboard ViewModel）：
/// 负责主控台全局监视、系统状态汇总与核心控制指令调度。
/// </summary>
public sealed class MasterConsoleViewModel : ViewModelBase, IDisposable
{
    private string _pageTitle = "主控台";
    private string _systemStatusText = "系统待命";
    private bool _isDisposed;

    public MasterConsoleViewModel()
    {
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
    /// 资源确定性释放，防止长时间运行下的内存与事件泄漏
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
    }
}
