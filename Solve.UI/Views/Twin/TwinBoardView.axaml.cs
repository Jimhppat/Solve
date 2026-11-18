using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 主控台显示区。代码后置只负责刷新节拍：显示区在界面上时按固定周期刷新，离开界面（切到其他页面）即停止，不占 UI 线程。
/// </summary>
public partial class TwinBoardView : UserControl
{
    private readonly DispatcherTimer _timer;

    public TwinBoardView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TwinBoardViewModel.RefreshInterval };
        _timer.Tick += OnTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as TwinBoardViewModel)?.Refresh();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e) => (DataContext as TwinBoardViewModel)?.Refresh();
}
