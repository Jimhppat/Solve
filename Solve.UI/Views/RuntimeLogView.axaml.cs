using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 运行日志页。代码后置只做纯视图行为：自动滚动（见 <see cref="LogAutoScroll"/>）、复制到剪贴板、打开日志目录。
/// </summary>
public partial class RuntimeLogView : UserControl
{
    private readonly LogAutoScroll _autoScroll;

    public RuntimeLogView()
    {
        InitializeComponent();
        _autoScroll = new LogAutoScroll(this, LogList);
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
        if (_autoScroll.ViewModel?.LogDirectory is not { } directory)
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
