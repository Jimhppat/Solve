using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 驱动库窗口的内容。代码后置只做纯视图行为：选择要导入的文件夹、在资源管理器中打开驱动库目录；导入本身由视图模型完成。
/// </summary>
public partial class DeviceLibraryView : UserControl
{
    public DeviceLibraryView()
    {
        InitializeComponent();
    }

    private DeviceLibraryViewModel? ViewModel => DataContext as DeviceLibraryViewModel;

    // async void 事件处理：异常必须在这里处理。UI 线程未处理异常会触发全站安全跳闸并退出程序，
    // 选择文件夹失败不值得这样处理，改为在页面上提示原因
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanImport: true } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择驱动包目录（含 package.json 与驱动程序集，如驱动项目的编译输出目录）",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
                return;

            if (folders[0].TryGetLocalPath() is not { } path)
            {
                vm.ReportError("所选文件夹不是本机磁盘上的目录，不能导入");
                return;
            }

            await vm.ImportAsync(path);
        }
        catch (Exception ex)
        {
            vm.ReportError($"导入失败：{ex.Message}");
        }
    }

    private async void OnOpenDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsAvailable: true } vm)
            return;

        try
        {
            // 设备库目录在启动时已创建；被删掉时提示，不在这里重建
            if (!Directory.Exists(vm.RootDirectory))
            {
                vm.ReportError($"目录不存在：{vm.RootDirectory}（点击「扫描」会重新创建）");
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
                && !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(vm.RootDirectory)))
            {
                vm.ReportError($"系统未能打开目录：{vm.RootDirectory}");
            }
        }
        catch (Exception ex)
        {
            vm.ReportError($"无法打开目录 {vm.RootDirectory}：{ex.Message}");
        }
    }
}
