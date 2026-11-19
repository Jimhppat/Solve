using Avalonia.Controls;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 六层控件之一（只展示，不可选中）。逻辑在视图模型中，代码后置不做任何事。
/// </summary>
public partial class SlotNodeView : UserControl
{
    public SlotNodeView()
    {
        InitializeComponent();
    }
}
