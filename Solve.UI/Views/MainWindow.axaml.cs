using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

public partial class MainWindow : Window
{
    // 模块配置窗口由主窗口统一持有：总线页视图在切换页面时会被重建，放在那里会丢失已打开窗口的登记
    private readonly Dictionary<BusDevicesViewModel, BusModulesWindow> _moduleWindows = [];
    private BusConsoleViewModel? _busConsole;

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

        if (_busConsole is not null)
            _busConsole.ModuleWindowRequested -= OnModuleWindowRequested;

        _busConsole = (DataContext as MainViewModel)?.BusConsole;

        if (_busConsole is not null)
            _busConsole.ModuleWindowRequested += OnModuleWindowRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_busConsole is not null)
        {
            _busConsole.ModuleWindowRequested -= OnModuleWindowRequested;
            _busConsole = null;
        }

        base.OnClosed(e);
    }

    // 模块配置用非模态的从属窗口：模态会锁住主窗口，标题栏的「紧急停止」就点不到了。
    // 从属窗口始终在主窗口之上，随主窗口最小化、关闭；同一条总线已打开时只激活，不重复创建。
    private void OnModuleWindowRequested(object? sender, BusDevicesViewModel page)
    {
        if (_moduleWindows.TryGetValue(page, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new BusModulesWindow { DataContext = page };
        FitToScreen(window);
        _moduleWindows.Add(page, window);
        window.Closed += (_, _) =>
        {
            _moduleWindows.Remove(page);
            page.Dispose();
            _busConsole?.ReleaseModulePage(page);
        };
        window.Show(this);
    }

    /// <summary>
    /// 模块配置窗口取主窗口所在屏幕工作区（不含任务栏）的 90%：各种分辨率下都完整可见，仍可手动最大化
    /// </summary>
    private void FitToScreen(Window window)
    {
        const double ScreenFraction = 0.9;

        if (Screens.ScreenFromWindow(this) is not { } screen)
            return;

        // WorkingArea 是物理像素，窗口尺寸是与 DPI 无关的逻辑像素，按屏幕缩放换算
        var area = screen.WorkingArea;
        window.Width = Math.Max(window.MinWidth, area.Width / screen.Scaling * ScreenFraction);
        window.Height = Math.Max(window.MinHeight, area.Height / screen.Scaling * ScreenFraction);
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

    // 侧栏是 CompactOverlay 浮层，展开时盖住内容区：点选导航项后立即收起，把工作台让出来。
    // 用 Tapped 而不是 SelectionChanged：再次点选当前页也要收起（选中项不变就不会有 SelectionChanged），
    // 而键盘方向键逐项浏览时不收起，免得焦点跟着浮层一起消失。
    // 点在禁用项（暂未启用的页面）或列表空白处不收起，那一下并没有切换到任何页面。
    private void OnNavItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;

        var item = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item is not { IsEffectivelyEnabled: true })
            return;

        Shell.IsPaneOpen = false;
    }
}
