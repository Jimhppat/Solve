using Avalonia.Controls;
using Avalonia.Input;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 产品位：显示区中唯一可选中的节点。代码后置只把点选转交视图模型。
/// </summary>
public partial class ProductNodeView : UserControl
{
    public ProductNodeView()
    {
        InitializeComponent();
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProductNodeViewModel product)
            return;

        product.Select();
        e.Handled = true;
    }
}
