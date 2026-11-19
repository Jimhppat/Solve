using Avalonia.Interactivity;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 六层控件共用的点选处理
/// </summary>
internal static class TwinNodeInput
{
    /// <summary>
    /// 点选节点：只选中（控制区显示详情），不进入下一级。
    /// 标记已处理，事件不再冒泡到上级容器（否则点产品位时上级也会被选中）。
    /// </summary>
    public static void Select(object? dataContext, RoutedEventArgs e)
    {
        if (dataContext is not TwinNodeViewModel node)
            return;

        node.Select();
        e.Handled = true;
    }
}
