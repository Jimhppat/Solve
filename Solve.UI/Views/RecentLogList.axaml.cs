using Avalonia.Controls;

namespace Solve.UI.Views;

/// <summary>
/// 窄区域日志列表，数据上下文为 <see cref="ViewModels.RuntimeLogViewModel"/>。
/// </summary>
public partial class RecentLogList : UserControl
{
    public RecentLogList()
    {
        InitializeComponent();

        // 自动滚动对象挂在本控件的事件上，随控件存活，不需要另存引用
        _ = new LogAutoScroll(this, LogList);
    }
}
