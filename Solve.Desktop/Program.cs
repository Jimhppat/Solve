using System;
using Avalonia;
using Microsoft.Extensions.Logging;
using Solve.Desktop.Composition;

namespace Solve.Desktop;

class Program
{
    /// <summary>
    /// UI 退出后释放总线与日志的总时长上限。超时后进程照常退出，操作系统会回收串口 / Socket 句柄。
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    // 说明：AppHost.Create 只初始化 Serilog 文件日志，不依赖 Avalonia 与同步上下文；总线系统在 UI 启动后才于线程池启动。
    [STAThread]
    public static int Main(string[] args)
    {
        var host = AppHost.Create(DesktopOptions.FromArgs(args, AppContext.BaseDirectory));
        try
        {
            return BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            host.Logger.LogCritical(ex, "UI 主循环异常退出，对全部总线执行安全跳闸");
            host.EmergencyStop("UI 主循环异常退出");
            throw;
        }
        finally
        {
            host.Shutdown(ShutdownTimeout);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    // 设计器调用此无参版本：不创建 AppHost，不打开配置库，不接触任何设备。
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(AppHost? host)
        => AppBuilder.Configure(() => new App(host))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
