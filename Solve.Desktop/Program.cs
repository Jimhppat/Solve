using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Microsoft.Extensions.Logging;
using Solve.Desktop.Composition;
using Solve.Desktop.Logging;

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
        int exitCode;
        try
        {
            exitCode = BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
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

        // 只在正常退出时重启：异常退出已在上面重新抛出，UI 线程故障退出（非 0 退出码）也不重启，避免带着故障反复拉起。
        // 此时总线已释放、日志已关闭，新实例不会与本进程争用文件和通道
        if (host.RestartRequested && exitCode == 0)
            return Relaunch(args) ? exitCode : RelaunchFailedExitCode;

        return exitCode;
    }

    /// <summary>
    /// 模式切换后重启新实例失败时的退出码。日志此时已关闭，只能写标准错误；模式已保存，手动启动即按新模式运行
    /// </summary>
    private const int RelaunchFailedExitCode = 2;

    private static bool Relaunch(string[] args)
    {
        var startInfo = ProcessRelaunch.CreateStartInfo(args);
        if (startInfo is null)
        {
            Console.Error.WriteLine("模式已切换，但无法确定程序路径，未能自动重启。请手动启动程序。");
            return false;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is not null)
                return true;

            Console.Error.WriteLine("模式已切换，但新实例未能启动。请手动启动程序。");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"模式已切换，但自动重启失败：{ex.Message}。请手动启动程序。");
            return false;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    // 设计器调用此无参版本：不创建 AppHost，不打开配置库，不接触任何设备。
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(AppHost? host)
    {
        var builder = AppBuilder.Configure(() => new App(host))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont();

        // 有组合根时框架日志（绑定错误等）进黑匣子；设计器没有组合根，仍输出到 Trace
        return host is null ? builder.LogToTrace() : builder.LogToSerilog(host.Logging.Logger);
    }
}
