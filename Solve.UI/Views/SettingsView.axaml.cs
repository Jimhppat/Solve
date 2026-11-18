using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 系统设置页。代码后置只做纯视图行为：选择文件夹、在资源管理器中打开目录；切换目录由视图模型完成。
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    // async void 事件处理：异常必须在这里处理。UI 线程未处理异常会触发全站安全跳闸并退出程序，
    // 选择、打开文件夹失败不值得这样处理，改为在页面上提示原因
    private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        try
        {
            var start = vm.CurrentLogDirectory is { } current ? await storage.TryGetFolderFromPathAsync(current) : null;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择日志目录",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            if (folders.Count == 0)
                return;

            if (folders[0].TryGetLocalPath() is { } path)
                vm.LogDirectoryInput = path;
            else
                vm.ReportLogDirectoryError("所选文件夹不是本机磁盘上的目录，不能作为日志目录");
        }
        catch (Exception ex)
        {
            vm.ReportLogDirectoryError($"无法打开文件夹选择框：{ex.Message}");
        }
    }

    private async void OnOpenCurrentDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CurrentLogDirectory: { } directory } vm)
            return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
                && !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory)))
            {
                vm.ReportLogDirectoryError($"系统未能打开目录：{directory}");
            }
        }
        catch (Exception ex)
        {
            vm.ReportLogDirectoryError($"无法打开目录 {directory}：{ex.Message}");
        }
    }
}
