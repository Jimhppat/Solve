using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.SettingsWindowRequested -= OnSettingsWindowRequested;

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
            _viewModel.SettingsWindowRequested += OnSettingsWindowRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SettingsWindowRequested -= OnSettingsWindowRequested;
            _viewModel = null;
        }

        base.OnClosed(e);
    }

    // 自绘系统按钮：ElementRole 只负责 Win32 命中测试（贴靠布局、悬停语义），
    // 点击本身仍由客户区的 Button 接收，所以窗口状态切换要在这里显式完成。
    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => Close();

    // 系统设置用非模态的从属窗口：模态会锁住主窗口，标题栏的「紧急停止」就点不到了。
    // 从属窗口始终在主窗口之上，随主窗口最小化、关闭；已打开时只激活，不重复创建。
    private void OnSettingsWindowRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show(this);
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        // 视图模型没有接受这次选中，侧栏高亮仍停在「系统设置」上，需要退回当前页。
        // 此刻 ListBox 正在把选中项写回视图模型，同步改回会重入它的选择变更流程，所以投递到下一轮再改。
        Dispatcher.UIThread.Post(SyncNavSelection);
    }

    private void SyncNavSelection()
    {
        if (_viewModel is not null)
            NavList.SelectedIndex = _viewModel.SelectedNavIndex;
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow is not null)
            _settingsWindow.Closed -= OnSettingsWindowClosed;

        _settingsWindow = null;
    }
}
