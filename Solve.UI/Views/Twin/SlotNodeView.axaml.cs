using Avalonia.Controls;
using Avalonia.Input;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 六层控件之一。逻辑在视图模型中，代码后置只把点选（选中，不进入下一级）转交视图模型。
/// </summary>
public partial class SlotNodeView : UserControl
{
    public SlotNodeView()
    {
        InitializeComponent();
    }

    private void OnTapped(object? sender, TappedEventArgs e) => TwinNodeInput.Select(DataContext, e);
}
