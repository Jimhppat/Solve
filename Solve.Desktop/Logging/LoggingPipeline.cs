using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Settings.Configuration;
using Serilog.Sinks.Async;
using Serilog.Sinks.File;
using Solve.Core.Application.Logging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 黑匣子日志管线：Serilog 根日志器、级别配置文件监视与 Serilog 自诊断输出，统一创建、统一释放。
/// </summary>
/// <remarks>
/// <para>怎么写（异步队列、队列满即丢弃、按天分文件）固定在代码中，级别配置文件只能调整级别：
/// 避免现场改配置让日志反过来阻塞控制路径。目录、保存天数与单个文件上限可在设置页修改
/// （<see cref="SwitchDirectory"/>、<see cref="ApplyRetention"/>），取值由 <see cref="LogRetentionPolicy"/> 限定在安全范围内。</para>
/// <para>日志必须能启动：配置文件缺失、JSON 损坏或级别值非法时退回代码默认级别，原因见 <see cref="SettingsError"/>。</para>
/// </remarks>
public sealed class LoggingPipeline : IDisposable
{
    /// <summary>
    /// Serilog 自身故障（写盘失败、运行中配置改错等）的记录文件，与日志文件同目录
    /// </summary>
    public const string SelfLogFileName = "serilog-selflog.txt";

    /// <summary>
    /// 配置文件中 Serilog 设置所在的节
    /// </summary>
    public const string SettingsSection = "Serilog";

    /// <summary>
    /// 日志文件的行格式。<see cref="LogTextParser"/> 按此格式读回历史日志，修改时两处必须同步（有往返测试把关）
    /// </summary>
    internal const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:l}{NewLine}{Exception}";

    private const int QueueCapacity = 10_000;
    private const int SelfLogLineLimit = 1_000;

    private readonly IConfigurationRoot? _settings;
    private readonly DroppedEventMonitor _monitor;
    private readonly LogFeedSink _feed;
    private readonly SwitchableFileSink _file;
    private readonly SelfLogFile _selfLog;
    private readonly RetentionState _retention;
    private readonly Lock _switchGate = new();
    private string _logDirectory;
    private int _disposed;

    private LoggingPipeline(
        Logger logger,
        IConfigurationRoot? settings,
        DroppedEventMonitor monitor,
        LogFeedSink feed,
        SwitchableFileSink file,
        SelfLogFile selfLog,
        RetentionState retention,
        string logDirectory,
        string? settingsError)
    {
        Logger = logger;
        _settings = settings;
        _monitor = monitor;
        _feed = feed;
        _file = file;
        _selfLog = selfLog;
        _retention = retention;
        _logDirectory = logDirectory;
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
    /// 日志文件与自诊断文件当前所在的目录
    /// </summary>
    public string LogDirectory => Volatile.Read(ref _logDirectory);

    /// <summary>
    /// 当前生效的保存天数与单个文件上限
    /// </summary>
    public LogRetentionPolicy Retention => _retention.Policy;

    /// <summary>
    /// 创建日志管线。<paramref name="settingsPath"/> 为 null 时不读取配置文件，使用默认级别。
    /// </summary>
    /// <param name="retention">保存天数与单个文件上限；为 null 时使用默认值</param>
    public static LoggingPipeline Create(string logDirectory, string? settingsPath, LogRetentionPolicy? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        Directory.CreateDirectory(logDirectory);
        var selfLog = SelfLogFile.Enable(Path.Combine(logDirectory, SelfLogFileName));

        var feed = new LogFeedSink();
        var state = new RetentionState(retention ?? LogRetentionPolicy.Default);
        // 文件输出在构建根日志器之前创建：下面按配置构建失败、退回默认级别重建时沿用同一个
        var file = new SwitchableFileSink(logDirectory, directory => CreateFileLogger(directory, state));
        var settings = LoadSettings(settingsPath, out var settingsError);
        if (settings is not null && settingsError is null)
        {
            var monitor = new DroppedEventMonitor();
            try
            {
                return new LoggingPipeline(
                    Build(settings, monitor, feed, file), settings, monitor, feed, file, selfLog, state, logDirectory, null);
            }
            catch (Exception ex)
            {
                // 级别值非法等：Serilog 在应用配置时抛出，此时文件输出尚未挂上、也未打开文件，丢弃配置后按默认级别重建
                settingsError = ex.GetBaseException().Message;
            }
        }

        // 配置无效时本次运行不再监视文件：修正后重启生效，避免半套配置在运行中被部分热更新
        (settings as IDisposable)?.Dispose();
        var fallbackMonitor = new DroppedEventMonitor();
        return new LoggingPipeline(
            Build(null, fallbackMonitor, feed, file), null, fallbackMonitor, feed, file, selfLog, state, logDirectory, settingsError);
    }

    /// <summary>
    /// 把之后的日志文件与自诊断输出切换到 <paramref name="directory"/>，旧目录中的文件原地保留。
    /// 调用方须先确认目录存在且可写（见 <see cref="LogDirectoryValidator.Prepare"/>）。任何线程可调用。
    /// </summary>
    public void SwitchDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // 串行化切换：保证 LogDirectory 与实际写入的目录一致
        lock (_switchGate)
        {
            _file.SwitchTo(directory);
            _selfLog.MoveTo(Path.Combine(directory, SelfLogFileName));
            Volatile.Write(ref _logDirectory, directory);
        }
    }

    /// <summary>
    /// 之后按 <paramref name="policy"/> 分卷与清理。单个文件上限变化时重新打开文件输出，新上限从下一条日志开始生效，
    /// 已有文件不动（当前文件已超过新上限时，下一条写进新的分卷）。不立即清理，见 <see cref="PruneNow"/>。任何线程可调用。
    /// </summary>
    public void ApplyRetention(LogRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_switchGate)
        {
            var previous = _retention.Policy;
            _retention.Policy = policy;
            if (previous.FileSizeLimitBytes != policy.FileSizeLimitBytes)
                _file.Reopen();
        }
    }

    /// <summary>
    /// 按 <paramref name="policy"/> 列出 <paramref name="directory"/> 中会被删除的日志文件，不做任何修改。
    /// 正在写入的文件不会列入。目录不存在时为空。在调用线程上做磁盘 I/O，不要在 UI 线程上调用。
    /// </summary>
    /// <exception cref="IOException">列举目录失败</exception>
    /// <exception cref="UnauthorizedAccessException">无权读取目录</exception>
    public IReadOnlyList<DailyLogFile> PreviewPrune(string directory, LogRetentionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(policy);

        return DailyLogFiles.SelectForDeletion(
            DailyLogFiles.List(directory), Today(), policy.RetentionDays, _retention.ActiveFilePath);
    }

    /// <summary>
    /// 立即按当前的保存天数清理当前目录中过期的日志，正在写入的文件不删。不抛出：删除失败只记自诊断。
    /// 在调用线程上做磁盘 I/O，不要在 UI 线程上调用。
    /// </summary>
    /// <returns>实际删除的文件</returns>
    public IReadOnlyList<DailyLogFile> PruneNow()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return DailyLogFiles.Prune(LogDirectory, Today(), _retention.Policy.RetentionDays, _retention.ActiveFilePath);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

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

        // 根日志器释放时通常已连带关闭文件输出；这里再显式关闭一次（可重复调用），不依赖 Serilog 的连带释放行为
        _file.Dispose();

        // 队列已刷完，最后一条也推给了面板，再结束推送
        _feed.Complete();
    }

    /// <summary>
    /// 只含文件输出的日志器，作为 <see cref="SwitchableFileSink"/> 的当前输出。级别由根日志器过滤，这里全部放行。
    /// </summary>
    /// <remarks>
    /// <para>按每条日志自身的本地日期分文件（2026_11_18.log）：午夜前产生、午夜后才出队的日志仍写进前一天。
    /// 同时最多保持两天的文件打开，跨午夜时新旧日期交替出现不会反复开关文件，更早的立即关闭。</para>
    /// <para>保留期由 <see cref="RetentionHooks"/> 只按保存天数清理，不用 Serilog 自带的按文件数保留（它只认同一天的分卷）。
    /// 清理只管这个目录：切换目录后旧目录中的日志原地保留、不再清理（与设置页的说明一致）。</para>
    /// <para>单个文件上限在创建时取定；运行中修改上限时由 <see cref="ApplyRetention"/> 重新创建。</para>
    /// </remarks>
    private static Logger CreateFileLogger(string logDirectory, RetentionState state)
    {
        var hooks = new RetentionHooks(logDirectory, state);
        var fileSizeLimitBytes = state.Policy.FileSizeLimitBytes;
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Map(
                logEvent => DateOnly.FromDateTime(logEvent.Timestamp.LocalDateTime),
                (date, dated) => dated.File(
                    path: DailyLogFiles.PathFor(logDirectory, date),
                    outputTemplate: OutputTemplate,
                    fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: null,
                    hooks: hooks),
                2)
            .CreateLogger();
    }

    private static Logger Build(IConfiguration? settings, DroppedEventMonitor monitor, LogFeedSink feed, SwitchableFileSink file)
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
                    // 按日期命名的文件输出，可在运行中切换目录（设置页），见 SwitchableFileSink 与 CreateFileLogger
                    sink.Sink(file);
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
    /// 切换日志目录时随之移到新目录，行数上限按整次运行计算。
    /// </summary>
    private sealed class SelfLogFile
    {
        private readonly Lock _gate = new();
        private string _path;
        private int _remaining = SelfLogLineLimit;

        private SelfLogFile(string path) => _path = path;

        public static SelfLogFile Enable(string path)
        {
            var file = new SelfLogFile(path);
            SelfLog.Enable(file.Write);
            return file;
        }

        public void MoveTo(string path)
        {
            lock (_gate)
            {
                _path = path;
            }
        }

        private void Write(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            lock (_gate)
            {
                if (_remaining <= 0)
                    return;

                _remaining--;
                var line = _remaining == 0 ? message + Environment.NewLine + "（自诊断记录已达上限，本次运行不再记录）" : message;
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch (Exception)
                {
                    // 自诊断输出本身也写不进去时已无处可报，放弃
                }
            }
        }
    }

    /// <summary>
    /// 保留设置与正在写入的文件，在写盘线程（打开文件时清理）与设置页（预览、立即清理）之间共享
    /// </summary>
    private sealed class RetentionState(LogRetentionPolicy policy)
    {
        private LogRetentionPolicy _policy = policy;
        private string? _activeFilePath;

        public LogRetentionPolicy Policy
        {
            get => Volatile.Read(ref _policy);
            set => Volatile.Write(ref _policy, value);
        }

        /// <summary>
        /// 最近打开的日志文件；清理时永不删除它。尚未写过日志时为 null
        /// </summary>
        public string? ActiveFilePath
        {
            get => Volatile.Read(ref _activeFilePath);
            set => Volatile.Write(ref _activeFilePath, value);
        }
    }

    /// <summary>
    /// 每次打开日志文件（启动、跨天、单文件超限续写）时按当前的保存天数清理过期的日志。
    /// 在异步队列的后台线程上执行，不占用业务线程；清理失败只记自诊断，不影响写入。
    /// </summary>
    private sealed class RetentionHooks(string directory, RetentionState state) : FileLifecycleHooks
    {
        public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
        {
            state.ActiveFilePath = path;
            DailyLogFiles.Prune(directory, Today(), state.Policy.RetentionDays, protectedPath: path);
            return underlyingStream;
        }
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
