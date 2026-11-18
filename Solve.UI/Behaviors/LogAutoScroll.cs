using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Solve.UI.ViewModels;

namespace Solve.UI.Behaviors;

/// <summary>
/// 日志列表的自动滚动：视图模型追加或重新筛选日志后，若开启了自动滚动，就把列表滚到最新一条。
/// 运行日志页与主控台的最近日志控件共用。
/// </summary>
/// <remarks>
/// 放在 Behaviors 而不是 ViewModels：它直接操作 Avalonia 控件与 Dispatcher，属于视图层；
/// 视图模型保持不依赖具体控件，才能在没有 UI 线程的单元测试里运行。
/// </remarks>
/// <remarks>
/// 切换页面时视图会被重新创建，旧视图从可视树移除。对视图模型事件的订阅只在宿主挂在可视树上期间有效，
/// 否则离开过的旧视图会一直被视图模型引用，无法回收。
/// </remarks>
internal sealed class LogAutoScroll
{
    private readonly Control _host;
    private readonly ListBox _list;
    private RuntimeLogViewModel? _viewModel;
    private bool _attached;

    public LogAutoScroll(Control host, ListBox list)
    {
        _host = host;
        _list = list;

        host.AttachedToVisualTree += OnAttached;
        host.DetachedFromVisualTree += OnDetached;
        host.DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 当前跟踪的视图模型；宿主不在可视树上时为 null
    /// </summary>
    public RuntimeLogViewModel? ViewModel => _viewModel;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _attached = true;
        Track(_host.DataContext as RuntimeLogViewModel);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Track(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attached)
            Track(_host.DataContext as RuntimeLogViewModel);
    }

    private void Track(RuntimeLogViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
        {
            _viewModel.EntriesChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.EntriesChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_viewModel.AutoScroll)
                ScrollToEnd();
        }
    }

    private void OnEntriesChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { AutoScroll: true })
            ScrollToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 重新勾选自动滚动时立即跳到最新一条
        if (e.PropertyName == nameof(RuntimeLogViewModel.AutoScroll) && _viewModel is { AutoScroll: true })
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        // 以较低优先级执行：等新增的行完成布局后再滚动，否则会停在倒数第几条
        Dispatcher.UIThread.Post(() =>
        {
            var count = _list.ItemCount;
            if (count > 0)
                _list.ScrollIntoView(count - 1);
        }, DispatcherPriority.Background);
    }
}
