using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
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
