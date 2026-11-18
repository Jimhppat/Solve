using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

/// <summary>
/// 日志管线测试。每个测试使用独立临时目录，释放管线后读取日志文件断言（释放会刷写异步队列）。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class LoggingPipelineTests : IDisposable
{
    private const string HardwareContext = "Solve.Adapters.Hardware.Buses.Tcp.TcpTransport";
    private const string OtherContext = "Solve.Core.Application.Buses.BusManager";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly List<LoggingPipeline> _pipelines = [];

    private string LogDirectory => Path.Combine(_directory, "logs");

    private string SettingsPath => Path.Combine(_directory, DesktopOptions.LoggingSettingsFileName);

    public void Dispose()
    {
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private LoggingPipeline Create(string? settingsJson = null)
    {
        if (settingsJson is not null)
            WriteSettings(settingsJson);

        var pipeline = LoggingPipeline.Create(LogDirectory, settingsJson is null ? null : SettingsPath);
        _pipelines.Add(pipeline);
        return pipeline;
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
    }

    private string DisposeAndRead(LoggingPipeline pipeline)
    {
        pipeline.Dispose();
        return LogFiles.ReadAll(LogDirectory);
    }

    private static ILogger For(LoggingPipeline pipeline, string sourceContext)
        => pipeline.Logger.ForContext(Constants.SourceContextPropertyName, sourceContext);

    private static string Levels(string defaultLevel, string hardwareLevel = "Information") => $$"""
        {
          // 与发布的 appsettings.json 一样带注释，验证注释可被解析
          "Serilog": {
            "MinimumLevel": {
              "Default": "{{defaultLevel}}",
              "Override": { "Solve.Adapters.Hardware": "{{hardwareLevel}}" }
            }
          }
        }
        """;

    // ==================== 级别配置 ====================

    [Fact]
    public void WithoutSettingsFile_UsesInformationDefault()
    {
        var pipeline = Create();

        For(pipeline, OtherContext).Debug("debug-marker");
        For(pipeline, OtherContext).Information("info-marker");
        var log = DisposeAndRead(pipeline);

        Assert.Null(pipeline.SettingsError);
        Assert.Contains("info-marker", log);
        Assert.DoesNotContain("debug-marker", log);
    }

    [Fact]
    public void MissingSettingsFile_IsNotAnError()
    {
        var pipeline = LoggingPipeline.Create(LogDirectory, Path.Combine(_directory, "absent.json"));
        _pipelines.Add(pipeline);

        Assert.Null(pipeline.SettingsError);
    }

    [Fact]
    public void Override_CanLowerLevelForOneSource()
    {
        var pipeline = Create(Levels("Information", hardwareLevel: "Debug"));

        For(pipeline, HardwareContext).Debug("hardware-debug");
        For(pipeline, OtherContext).Debug("other-debug");
        var log = DisposeAndRead(pipeline);

        Assert.Null(pipeline.SettingsError);
        Assert.Contains("hardware-debug", log);
        Assert.DoesNotContain("other-debug", log);
    }

    [Fact]
    public void MalformedJson_FallsBackToDefaults_AndReportsReason()
    {
        var pipeline = Create("{ \"Serilog\": { \"MinimumLevel\": ");

        For(pipeline, OtherContext).Information("still-logging");
        For(pipeline, OtherContext).Debug("debug-marker");
        var log = DisposeAndRead(pipeline);

        Assert.NotNull(pipeline.SettingsError);
        Assert.Contains("still-logging", log);
        Assert.DoesNotContain("debug-marker", log);
    }

    [Fact]
    public void InvalidLevelName_FallsBackToDefaults_AndReportsReason()
    {
        var pipeline = Create(Levels("Verbos"));

        For(pipeline, OtherContext).Information("still-logging");
        var log = DisposeAndRead(pipeline);

        Assert.NotNull(pipeline.SettingsError);
        Assert.Contains("still-logging", log);
    }

    [Fact]
    public void SinksInSettingsFile_AreNotApplied()
    {
        var hijackPath = Path.Combine(_directory, "hijack.log").Replace("\\", "/");
        var pipeline = Create($$"""
            {
              "Serilog": {
                "WriteTo": [ { "Name": "File", "Args": { "path": "{{hijackPath}}" } } ]
              }
            }
            """);

        For(pipeline, OtherContext).Information("info-marker");
        var log = DisposeAndRead(pipeline);

        Assert.Contains("info-marker", log);
        Assert.False(File.Exists(hijackPath));
    }

    [Fact]
    public void AvaloniaSources_DefaultToWarning()
    {
        var pipeline = Create();

        For(pipeline, "Avalonia.Layout").Information("avalonia-info");
        For(pipeline, "Avalonia.Binding").Warning("avalonia-warning");
        var log = DisposeAndRead(pipeline);

        Assert.DoesNotContain("avalonia-info", log);
        Assert.Contains("avalonia-warning", log);
    }

    // ==================== 热更新 ====================

    [Fact]
    public async Task ChangedLevel_TakesEffectWithoutRestart()
    {
        var pipeline = Create(Levels("Information"));
        var logger = For(pipeline, OtherContext);

        WriteSettings(Levels("Debug"));

        var attempt = 0;
        var applied = await LogFiles.EventuallyAsync(() =>
        {
            logger.Debug("probe-{Attempt}", ++attempt);
            return LogFiles.ReadAll(LogDirectory).Contains("probe-");
        });

        Assert.True(applied, "修改级别后 10 秒内未生效");
    }

    [Fact]
    public async Task SettingsCorruptedAtRuntime_KeepsCurrentLevels()
    {
        var pipeline = Create(Levels("Information"));
        var logger = For(pipeline, OtherContext);

        // 先确认热更新链路已生效，再写坏文件
        WriteSettings(Levels("Debug"));
        var attempt = 0;
        Assert.True(await LogFiles.EventuallyAsync(() =>
        {
            logger.Debug("probe-{Attempt}", ++attempt);
            return LogFiles.ReadAll(LogDirectory).Contains("probe-");
        }));

        WriteSettings("{ broken");
        var selfLog = Path.Combine(LogDirectory, LoggingPipeline.SelfLogFileName);
        Assert.True(await LogFiles.EventuallyAsync(
            () => File.Exists(selfLog) && LogFiles.Read(selfLog).Contains("重新加载失败")),
            "运行中写坏配置文件后，未在自诊断文件中留下记录");

        logger.Debug("after-corruption");
        var log = DisposeAndRead(pipeline);

        Assert.Contains("after-corruption", log);
    }

    // ==================== 输出格式与释放 ====================

    [Fact]
    public void Output_RendersEnumsWithoutQuotes_AndIncludesThreadId()
    {
        var pipeline = Create();

        For(pipeline, OtherContext).Information("模式={Mode}", DayOfWeek.Monday);
        var log = DisposeAndRead(pipeline);

        Assert.Contains("模式=Monday", log);
        Assert.Contains($"[{Environment.CurrentManagedThreadId}] {OtherContext}", log);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pipeline = Create();

        pipeline.Dispose();
        pipeline.Dispose();
    }

    // ==================== 按日期命名与保留期 ====================

    private static readonly MessageTemplateParser TemplateParser = new();

    private static void WriteAt(LoggingPipeline pipeline, DateTimeOffset timestamp, string text)
        => pipeline.Logger.Write(new LogEvent(timestamp, LogEventLevel.Information, null, TemplateParser.Parse(text), []));

    [Fact]
    public void Files_AreNamedByLocalDate()
    {
        var pipeline = Create();

        For(pipeline, OtherContext).Information("today-marker");
        pipeline.Dispose();

        var file = Assert.Single(DailyLogFiles.List(LogDirectory));
        Assert.Equal(DailyLogFiles.FileName(DateOnly.FromDateTime(DateTime.Now)), Path.GetFileName(file.Path));
        Assert.Contains("today-marker", LogFiles.Read(file.Path));
    }

    [Fact]
    public void EventsAroundMidnight_GoToTheDayTheyHappened_EvenWhenArrivingLate()
    {
        var pipeline = Create();
        var midnight = DateTime.Today;
        var today = DateOnly.FromDateTime(midnight);

        // 午夜后的先到、午夜前的后到：按日志自身时间分文件，而不是按写盘时间
        WriteAt(pipeline, new DateTimeOffset(midnight.AddMilliseconds(100)), "after-midnight");
        WriteAt(pipeline, new DateTimeOffset(midnight.AddMilliseconds(-100)), "before-midnight");
        pipeline.Dispose();

        var yesterdayLog = LogFiles.Read(DailyLogFiles.PathFor(LogDirectory, today.AddDays(-1)));
        var todayLog = LogFiles.Read(DailyLogFiles.PathFor(LogDirectory, today));
        Assert.Contains("before-midnight", yesterdayLog);
        Assert.DoesNotContain("after-midnight", yesterdayLog);
        Assert.Contains("after-midnight", todayLog);
        Assert.DoesNotContain("before-midnight", todayLog);
    }

    [Fact]
    public void OpeningLogFile_PrunesExpiredFiles_AndKeepsEverythingElse()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        Directory.CreateDirectory(LogDirectory);
        string Touch(string name)
        {
            var path = Path.Combine(LogDirectory, name);
            File.WriteAllText(path, "old");
            return path;
        }

        var expired = Touch(DailyLogFiles.FileName(today.AddDays(-DailyLogFiles.RetentionDays)));
        var expiredLegacy = Touch($"solve-{today.AddDays(-45):yyyyMMdd}.log");
        var kept = Touch(DailyLogFiles.FileName(today.AddDays(-(DailyLogFiles.RetentionDays - 1))));
        var unrelated = Touch("notes.log");

        var pipeline = Create();
        For(pipeline, OtherContext).Information("opens-today-file");
        pipeline.Dispose();

        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(expiredLegacy));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task AppHost_LogsInvalidSettingsAsWarning()
    {
        WriteSettings("{ broken");
        var host = AppHost.Create(new DesktopOptions(
            HardwareRequested: false,
            DatabasePath: Path.Combine(_directory, "config", "solve.db"),
            LogDirectory: LogDirectory,
            LoggingSettingsPath: SettingsPath));

        await host.DisposeAsync();
        var log = LogFiles.ReadAll(LogDirectory);

        Assert.Contains("[WRN]", log);
        Assert.Contains("日志级别配置", log);
    }
}
