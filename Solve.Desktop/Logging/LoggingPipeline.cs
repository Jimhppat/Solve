using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Settings.Configuration;
using Serilog.Sinks.Async;
using Solve.Core.Application.Logging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 黑匣子日志管线：Serilog 根日志器、级别配置文件监视与 Serilog 自诊断输出，统一创建、统一释放。
/// </summary>
/// <remarks>
/// <para>写到哪里、怎么写（异步队列、队列满即丢弃、滚动与保留）固定在代码中，配置文件只能调整级别：
/// 避免现场改配置让日志反过来阻塞控制路径。</para>
/// <para>日志必须能启动：配置文件缺失、JSON 损坏或级别值非法时退回代码默认级别，原因见 <see cref="SettingsError"/>。</para>
/// </remarks>
public sealed class LoggingPipeline : IDisposable
{
    /// <summary>
    /// 日志文件名前缀；滚动后的文件名形如 solve-20261118.log、solve-20261118_001.log
    /// </summary>
    public const string FilePrefix = "solve-";

    /// <summary>
    /// Serilog 自身故障（写盘失败、运行中配置改错等）的记录文件，与日志文件同目录
    /// </summary>
    public const string SelfLogFileName = "serilog-selflog.txt";

    /// <summary>
    /// 配置文件中 Serilog 设置所在的节
    /// </summary>
    public const string SettingsSection = "Serilog";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:l}{NewLine}{Exception}";

    private const int QueueCapacity = 10_000;
    private const int SelfLogLineLimit = 1_000;

    private readonly IConfigurationRoot? _settings;
    private readonly DroppedEventMonitor _monitor;
    private readonly LogFeedSink _feed;
    private int _disposed;

    private LoggingPipeline(
        Logger logger,
        IConfigurationRoot? settings,
        DroppedEventMonitor monitor,
        LogFeedSink feed,
        string? settingsError)
    {
        Logger = logger;
        _settings = settings;
        _monitor = monitor;
        _feed = feed;
        SettingsError = settingsError;
    }

    /// <summary>
    /// Serilog 根日志器。业务代码不直接使用，经 Microsoft.Extensions.Logging 桥接获取日志器。
    /// </summary>
    public Logger Logger { get; }

    /// <summary>
    /// 启动时读取级别配置失败的原因；为 null 表示配置已应用或未提供配置文件
    /// </summary>
    public string? SettingsError { get; }

    /// <summary>
    /// 异步队列满而被丢弃的日志条数
    /// </summary>
    public long DroppedCount => _monitor.DroppedCount;

    /// <summary>
    /// 推给界面日志面板的实时日志
    /// </summary>
    public ILogFeed Feed => _feed;

    /// <summary>
    /// 创建日志管线。<paramref name="settingsPath"/> 为 null 时不读取配置文件，使用默认级别。
    /// </summary>
    public static LoggingPipeline Create(string logDirectory, string? settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        Directory.CreateDirectory(logDirectory);
        EnableSelfLog(Path.Combine(logDirectory, SelfLogFileName));

        var feed = new LogFeedSink();
        var settings = LoadSettings(settingsPath, out var settingsError);
        if (settings is not null && settingsError is null)
        {
            var monitor = new DroppedEventMonitor();
            try
            {
                return new LoggingPipeline(Build(logDirectory, settings, monitor, feed), settings, monitor, feed, null);
            }
            catch (Exception ex)
            {
                // 级别值非法等：Serilog 在应用配置时抛出，此时尚未创建任何文件输出，丢弃配置后按默认级别重建
                settingsError = ex.GetBaseException().Message;
            }
        }

        // 配置无效时本次运行不再监视文件：修正后重启生效，避免半套配置在运行中被部分热更新
        (settings as IDisposable)?.Dispose();
        var fallbackMonitor = new DroppedEventMonitor();
        return new LoggingPipeline(Build(logDirectory, null, fallbackMonitor, feed), null, fallbackMonitor, feed, settingsError);
    }

    /// <summary>
    /// 刷写异步队列并关闭日志文件。可重复调用，任何线程可调用。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var dropped = _monitor.DroppedCount;
        if (dropped > 0)
        {
            Logger.ForContext<LoggingPipeline>()
                .Warning("本次运行日志队列曾满，共丢弃 {Count} 条日志，黑匣子记录不完整", dropped);
        }

        // 先停止监视配置文件，避免关停过程中触发热更新
        (_settings as IDisposable)?.Dispose();
        Logger.Dispose();

        // 队列已刷完，最后一条也推给了面板，再结束推送
        _feed.Complete();
    }

    private static Logger Build(string logDirectory, IConfiguration? settings, DroppedEventMonitor monitor, LogFeedSink feed)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Avalonia 框架日志以 Debug / Verbose 为主且量大，默认只收告警；配置文件可覆盖
            .MinimumLevel.Override(AvaloniaLogSink.SourceContextPrefix, LogEventLevel.Warning);

        if (settings is not null)
        {
            // 只提供 Serilog 核心程序集：配置文件里即使写了 WriteTo 等输出项也找不到实现，输出位置始终由代码决定。
            // 同时免去启动时按依赖清单扫描程序集。
            configuration.ReadFrom.Configuration(
                settings,
                new ConfigurationReaderOptions(typeof(LoggerConfiguration).Assembly) { SectionName = SettingsSection });
        }

        return configuration
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            // 异步写盘：业务线程只入队；队列满时丢弃而不是阻塞（控制路径优先于日志完整性）。
            // 调试输出也放在同一后台队列里：带调试器时 OutputDebugString 会等调试器处理，不能在业务线程上同步调用
            .WriteTo.Async(
                sink =>
                {
                    sink.File(
                        path: Path.Combine(logDirectory, FilePrefix + ".log"),
                        outputTemplate: OutputTemplate,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        fileSizeLimitBytes: 50L * 1024 * 1024,
                        rollOnFileSizeLimit: true);
#if DEBUG
                    sink.Debug(outputTemplate: OutputTemplate);
#endif
                    // 界面日志面板：同样在后台线程上转换与推送
                    sink.Sink(feed);
                },
                bufferSize: QueueCapacity,
                blockWhenFull: false,
                monitor: monitor)
            .CreateLogger();
    }

    private static IConfigurationRoot? LoadSettings(string? settingsPath, out string? error)
    {
        error = null;
        if (settingsPath is null)
            return null;

        string? initialError = null;
        var initializing = true;
        var settings = new ConfigurationBuilder()
            .AddJsonFile(source =>
            {
                source.Path = Path.GetFullPath(settingsPath);
                source.Optional = true;
                source.ReloadOnChange = true;
                source.ResolveFileProvider();
                source.OnLoadException = context =>
                {
                    var reason = context.Exception.GetBaseException().Message;
                    if (initializing)
                        initialError = reason;
                    else
                        SelfLog.WriteLine("日志配置文件 {0} 重新加载失败，保持当前级别：{1}", settingsPath, reason);

                    // 不抛出：首次加载由调用方退回默认级别；运行中改坏不能让文件监视线程上的异常影响进程
                    context.Ignore = true;
                };
            })
            .Build();

        initializing = false;
        error = initialError;
        return settings;
    }

    /// <summary>
    /// Serilog 自身出错时默认静默。写到日志目录下的单独文件，并限制行数，防止磁盘满等持续故障时无限增长。
    /// </summary>
    private static void EnableSelfLog(string path)
    {
        var gate = new Lock();
        var remaining = SelfLogLineLimit;

        SelfLog.Enable(message =>
        {
            System.Diagnostics.Debug.WriteLine(message);
            lock (gate)
            {
                if (remaining <= 0)
                    return;

                remaining--;
                var line = remaining == 0 ? message + Environment.NewLine + "（自诊断记录已达上限，本次运行不再记录）" : message;
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch (Exception)
                {
                    // 自诊断输出本身也写不进去时已无处可报，放弃
                }
            }
        });
    }

    /// <summary>
    /// 读取异步队列的丢弃计数。停止监视后仍保留检查器：释放时还要读取最终值。
    /// </summary>
    private sealed class DroppedEventMonitor : IAsyncLogEventSinkMonitor
    {
        private IAsyncLogEventSinkInspector? _inspector;

        public long DroppedCount => Volatile.Read(ref _inspector)?.DroppedMessagesCount ?? 0;

        public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => Volatile.Write(ref _inspector, inspector);

        public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
        {
        }
    }
}
