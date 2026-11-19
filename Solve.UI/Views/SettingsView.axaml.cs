using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 系统设置页。代码后置只做纯视图行为：选择文件夹、在资源管理器中打开目录；保存由视图模型完成。
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
            // 从输入框中的目录开始选；它还不存在时从当前写入的目录开始
            var start = await FindExistingDirectoryAsync(vm.LogDirectoryInput) ?? vm.CurrentLogDirectory;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择日志存储路径",
                AllowMultiple = false,
                SuggestedStartLocation = start is null ? null : await storage.TryGetFolderFromPathAsync(start),
            });

            if (folders.Count == 0)
                return;

            if (folders[0].TryGetLocalPath() is { } path)
                vm.LogDirectoryInput = path;
            else
                vm.ReportError("所选文件夹不是本机磁盘上的目录，不能作为日志存储路径");
        }
        catch (Exception ex)
        {
            vm.ReportError($"无法打开文件夹选择框：{ex.Message}");
        }
    }

    private async void OnOpenDeviceLibrary(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.DeviceLibrary is not { IsAvailable: true } library)
            return;

        try
        {
            // 设备库目录在启动时已创建；被删掉时提示，不在这里重建
            if (!Directory.Exists(library.RootDirectory))
            {
                library.ReportError($"目录不存在：{library.RootDirectory}（重启程序后会重新创建）");
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
                && !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(library.RootDirectory)))
            {
                library.ReportError($"系统未能打开目录：{library.RootDirectory}");
            }
        }
        catch (Exception ex)
        {
            library.ReportError($"无法打开目录 {library.RootDirectory}：{ex.Message}");
        }
    }

    private async void OnOpenDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var input = vm.LogDirectoryInput.Trim();
        try
        {
            if (await FindExistingDirectoryAsync(input) is not { } directory)
            {
                vm.ReportError(input.Length == 0 ? "存储路径为空" : $"目录不存在：{input}（保存时会自动创建）");
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
                && !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory)))
            {
                vm.ReportError($"系统未能打开目录：{directory}");
            }
        }
        catch (Exception ex)
        {
            vm.ReportError($"无法打开目录 {input}：{ex.Message}");
        }
    }

    /// <summary>
    /// 输入是已存在目录的完整路径时返回它，否则返回 null。
    /// 存在性检查放到线程池：手填的网络路径不可达时可能卡住数秒，不能占用 UI 线程。
    /// </summary>
    private static async Task<string?> FindExistingDirectoryAsync(string input)
    {
        var path = input.Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return null;

        return await Task.Run(() => Directory.Exists(path)) ? path : null;
    }
}
