using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Solve.Desktop.Composition;
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
            desktop.MainWindow = new MainWindow();
        }

        // 窗口先显示，总线系统在线程池启动；设计模式下绝不打开配置库或创建总线
        if (_host is not null && !Design.IsDesignMode)
        {
            _ = _host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
