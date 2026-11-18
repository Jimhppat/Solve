using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 运行日志页。代码后置只做纯视图行为：自动滚动、复制到剪贴板、打开日志目录。
/// </summary>
/// <remarks>
/// 切换页面时视图会被重新创建，旧视图从可视树移除。对视图模型事件的订阅只在挂到可视树期间有效，
/// 否则离开过的旧视图会一直被视图模型引用，无法回收。
/// </remarks>
public partial class RuntimeLogView : UserControl
{
    private RuntimeLogViewModel? _viewModel;
    private bool _attached;

    public RuntimeLogView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Track(DataContext as RuntimeLogViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Track(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached)
            Track(DataContext as RuntimeLogViewModel);
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
            var count = LogList.ItemCount;
            if (count > 0)
                LogList.ScrollIntoView(count - 1);
        }, DispatcherPriority.Background);
    }

    // async void 事件处理：任何异常都必须在这里吃掉。UI 线程未处理异常会触发全站安全跳闸并退出程序，
    // 复制、打开文件夹失败不值得这样处理
    private async void OnCopyEntry(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LogEntryItem item)
            return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(item.DetailText);
        }
        catch (Exception)
        {
            // 剪贴板被其他程序占用等，忽略
        }
    }

    private async void OnOpenLogDirectory(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.LogDirectory is not { } directory)
            return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
        }
        catch (Exception)
        {
            // 系统无法打开文件夹（权限、目录被删除等），忽略
        }
    }
}
