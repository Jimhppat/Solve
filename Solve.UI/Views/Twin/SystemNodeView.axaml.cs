using Avalonia.Controls;
using Avalonia.Input;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 六层控件之一。逻辑在视图模型中，代码后置只把点选转交视图模型。
/// </summary>
public partial class SystemNodeView : UserControl
{
    public SystemNodeView()
    {
        InitializeComponent();
    }

    private void OnTapped(object? sender, TappedEventArgs e) => TwinNodeInput.Activate(DataContext, e);
}
