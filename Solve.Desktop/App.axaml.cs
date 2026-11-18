using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Solve.Desktop.Composition;
using Solve.UI.ViewModels;
using Solve.UI.Views;

namespace Solve.Desktop;

public partial class App : Application
{
    private readonly AppHost? _host;

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
                _ = Task.Run(async () =>
                {
                    var report = await _host.Start().ConfigureAwait(false);
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
                    if (CreateStartupNotice(report, _host.Options.LogDirectory) is { } notice)
                        vm.ShowStartupNotice(notice);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
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
