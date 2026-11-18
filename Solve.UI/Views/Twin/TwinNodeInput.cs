using Avalonia.Interactivity;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 六层控件共用的点选处理
/// </summary>
internal static class TwinNodeInput
{
    /// <summary>
    /// 点选节点：选中；容器同时进入。标记已处理，事件不再冒泡到上级容器（否则上级也会被选中）。
    /// </summary>
    public static void Activate(object? dataContext, RoutedEventArgs e)
    {
        if (dataContext is not TwinNodeViewModel node)
            return;

        node.Activate();
        e.Handled = true;
    }
}
