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
                    if (_host.BusService is not null && _host.Buses is not null)
                    {
                        var isHardware = report.Mode?.Mode == TransportMode.Hardware;
                        var modeText = isHardware ? "实机模式 (Hardware)" : "仿真模式 (Dry-Run)";
                        await vm.InitializeAsync(
                            _host.BusService,
                            _host.Buses,
                            modeText,
                            isHardware,
                            reason => _host.EmergencyStop(reason));
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
