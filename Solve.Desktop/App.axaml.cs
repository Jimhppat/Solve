using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Solve.Desktop.Composition;
using Solve.UI.ViewModels;
using Solve.UI.Views;

namespace Solve.Desktop;

public partial class App : Application
{
    /// <summary>
    /// UI 线程未处理异常导致退出时的进程退出码
    /// </summary>
    private const int UiFaultExitCode = 1;

    private readonly AppHost? _host;
    private int _uiFaulted;

    /// <summary>
    /// XAML 设计器 / 预览器使用：没有组合根，不启动总线系统
    /// </summary>
    public App() : this(null)
    {
    }

    public App(AppHost? host)
    {
        _host = host;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow(vm);

            // 窗口先展示就绪，总线系统在线程池启动并异步注入 ViewModel
            if (_host is not null && !Design.IsDesignMode)
            {
                var host = _host;
                Dispatcher.UIThread.UnhandledException += (_, e) => OnUiThreadException(desktop, host, e);

                // 日志管线在 UI 之前就已创建：先接入日志页与主控台最近日志，启动过程中的日志也能看到
                // 目录取管线当前的实际目录（启动时设置的目录不可用会退回默认目录）；历史日志只接给运行日志页
                vm.AttachLogFeed(host.Logging.Feed, host.Logging.LogDirectory, host.LogArchive);
                vm.AttachLogSettings(host.LogSettings);

                _ = Task.Run(async () =>
                {
                    var report = await _host.Start().ConfigureAwait(false);

                    // 拓扑与总线互不依赖：配置库能打开就接入拓扑页（读取失败时页面显示原因并禁止保存）。须在 UI 线程上接入
                    if (_host.Topology is { } topology)
                        await Dispatcher.UIThread.InvokeAsync(() => vm.AttachTopology(topology));

                    // 主控台显示区：六层产品状态
                    if (_host.Twin is { } twin)
                        await Dispatcher.UIThread.InvokeAsync(() => vm.AttachTwin(twin));

                    // 设备页入口在总线行上：先于总线列表接入，列表一出现设备入口即可用
                    if (_host.Devices is { } devices)
                        await Dispatcher.UIThread.InvokeAsync(() => vm.AttachDevices(devices));

                    if (_host.BusService is not null)
                    {
                        var isHardware = report.Mode?.Mode == TransportMode.Hardware;
                        var modeText = isHardware ? "实机模式 (Hardware)" : "仿真模式 (Dry-Run)";
                        await vm.InitializeAsync(
                            _host.BusService,
                            modeText,
                            isHardware,
                            reason => _host.EmergencyStop(reason));
                    }

                    // 启动失败时总线服务可能为空、上面不会初始化，问题也必须让操作员看到，而不只是写进日志
                    if (CreateStartupNotice(report, _host.Logging.LogDirectory) is { } notice)
                        vm.ShowStartupNotice(notice);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// UI 线程未处理异常（如异步命令抛出）：记录、对全部总线安全跳闸，然后有序退出，不带着不可信的界面状态继续运行。
    /// </summary>
    /// <remarks>
    /// 首个异常标记为已处理并投递正常关停：关闭物理通道、刷写日志都走 Main 的 finally，
    /// 不依赖异常冒泡到进程级处理器（进程级路径不保证 finally 执行）。
    /// 关停过程中再出异常则不再拦截，交给 Main 的 catch 与进程级处理器。
    /// </remarks>
    private void OnUiThreadException(
        IClassicDesktopStyleApplicationLifetime desktop,
        AppHost host,
        DispatcherUnhandledExceptionEventArgs e)
    {
        host.Logger.LogCritical(e.Exception, "UI 线程未处理异常，对全部总线执行安全跳闸并退出程序");
        host.EmergencyStop("UI 线程未处理异常");

        if (Interlocked.Exchange(ref _uiFaulted, 1) != 0)
            return;

        e.Handled = true;
        // 投递而不是直接调用：关闭窗口时若再抛异常，作为新的调度器异常处理，而不是嵌套在本处理器内
        Dispatcher.UIThread.Post(() => desktop.Shutdown(UiFaultExitCode));
    }

    private static StartupNotice? CreateStartupNotice(StartupReport report, string logDirectory)
    {
        if (report.Fatal is { } fatal)
        {
            return new StartupNotice(
                "总线系统未能完整启动，部分或全部总线不可用",
                [fatal.Message, $"详细信息见日志目录：{logDirectory}"],
                IsError: true);
        }

        var details = new List<string>();
        foreach (var issue in report.ConfigurationIssues)
            details.Add($"配置记录 {issue.RowId} 无效：{string.Join("；", issue.Errors.Select(e => e.Message))}");

        foreach (var failure in report.RegistrationFailures)
            details.Add($"总线「{failure.Name}」未能加载：{failure.Error.Message}");

        return details.Count == 0
            ? null
            : new StartupNotice($"有 {details.Count} 条总线配置未加载，请修正后重新创建", details, IsError: false);
    }
}
