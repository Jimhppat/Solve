using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Solve.UI.Behaviors;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 运行日志页。代码后置只做纯视图行为：自动滚动（见 <see cref="LogAutoScroll"/>）、复制到剪贴板、打开日志目录、
/// 日期面板的打开与关闭、导出时的保存对话框（写入由视图模型经历史日志完成）。
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

    // 打开日期面板：翻到正在查看的月份，并重新列出有日志的日期（日志目录可能新增了当天文件，也可能清理了过期文件）
    private void OnDateFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is RuntimeLogViewModel vm)
            _ = vm.OpenDatePickerAsync();
    }

    private void OnDatePicked(object? sender, EventArgs e) => DateButton.Flyout?.Hide();

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RuntimeLogViewModel vm || !vm.CanExport)
            return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
                return;

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出日志",
                SuggestedFileName = vm.ExportFileName,
                DefaultExtension = "log",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("日志文件") { Patterns = ["*.log"] },
                    new FilePickerFileType("所有文件") { Patterns = ["*"] },
                ],
            });

            // 用户取消了保存对话框
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await vm.ExportAsync(stream, file.Name);

            // 覆盖一个更大的旧文件时截掉尾部残留，导出的文件只含这一天的日志
            if (stream.CanSeek && stream.CanWrite)
                stream.SetLength(stream.Position);
        }
        catch (Exception ex)
        {
            // 目标位置无写权限、U 盘被拔出等：只提示，不能让异常逃逸到 UI 线程
            vm.ReportExportFailure(ex.Message);
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
