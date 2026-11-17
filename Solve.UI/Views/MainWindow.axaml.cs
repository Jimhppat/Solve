using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Solve.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
}
