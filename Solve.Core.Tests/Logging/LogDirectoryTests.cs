using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

/// <summary>
/// 日志目录：校验、设置文件、运行中切换与启动时的退回。每个测试使用独立临时目录，只在本机临时目录内读写。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class LogDirectoryTests : IAsyncDisposable
{
    private const string Context = "Solve.Core.Application.Buses.BusManager";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly List<LoggingPipeline> _pipelines = [];
    private readonly List<AppHost> _hosts = [];

    private string DefaultDirectory => Path.Combine(_root, "logs");

    private string OtherDirectory => Path.Combine(_root, "other-logs");

    private string LocationPath => Path.Combine(_root, "config", LogLocationStore.FileName);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LoggingPipeline CreatePipeline(string directory)
    {
        var pipeline = LoggingPipeline.Create(directory, settingsPath: null);
        _pipelines.Add(pipeline);
        return pipeline;
    }

    // 服务的日志写进同一条管线：切换前后的说明日志也要能在文件里断言
    private LogDirectoryService CreateService(LoggingPipeline pipeline, LogLocationStore? store)
        => new(pipeline, store, DefaultDirectory, new LogDirectoryStartup(pipeline.LogDirectory, null, null),
            new SerilogLoggerFactory(pipeline.Logger, dispose: false).CreateLogger(typeof(LogDirectoryService).FullName!));

    private AppHost CreateHost()
    {
        var host = AppHost.Create(new DesktopOptions(
            HardwareRequested: false,
            DatabasePath: Path.Combine(_root, "config", "solve.db"),
            LogDirectory: DefaultDirectory,
            LogLocationPath: LocationPath));
        _hosts.Add(host);
        return host;
    }

    private static ILogger For(LoggingPipeline pipeline)
        => pipeline.Logger.ForContext(Constants.SourceContextPropertyName, Context);

    /// <summary>
    /// 写一条日志并等它落盘：切换前队列里的日志可能落在任一目录，等落盘后再切换，断言才确定
    /// </summary>
    private static async Task WriteAndWaitAsync(LoggingPipeline pipeline, string marker)
    {
        var directory = pipeline.LogDirectory;
        For(pipeline).Information(marker);
        Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(directory).Contains(marker)), $"{marker} 未在 10 秒内写入 {directory}");
    }

    // ==================== 校验 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative-logs")]
    [InlineData(@"logs\sub")]
    public void Prepare_RejectsEmptyAndRelativePaths(string directory)
    {
        var result = LogDirectoryValidator.Prepare(directory);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.InvalidPathCode, result.Error.Code);
    }

    [Theory]
    [InlineData(@"\\server\share\logs")]
    [InlineData(@"\\?\C:\logs")]
    public void Prepare_RejectsNetworkAndDevicePaths(string directory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = LogDirectoryValidator.Prepare(directory);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.NetworkPathCode, result.Error.Code);
    }

    [Fact]
    public void Prepare_RejectsMissingDrive()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).FirstOrDefault(c => !used.Contains(c));
        if (free == default)
            return;

        var result = LogDirectoryValidator.Prepare($@"{free}:\SolveLogs");

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.InvalidPathCode, result.Error.Code);
    }

    [Fact]
    public void Prepare_CreatesDirectory_NormalizesPath_AndLeavesNoProbeFile()
    {
        var input = "  " + Path.Combine(_root, "a", "..", "new-logs") + Path.DirectorySeparatorChar + "  ";

        var result = LogDirectoryValidator.Prepare(input);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Path.Combine(_root, "new-logs"), result.Value);
        Assert.True(Directory.Exists(result.Value));
        Assert.Empty(Directory.GetFiles(result.Value));
    }

    [Fact]
    public void SameDirectory_IgnoresTrailingSeparator_AndCaseOnWindows()
    {
        Assert.True(LogDirectoryValidator.SameDirectory(DefaultDirectory, DefaultDirectory + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
            Assert.True(LogDirectoryValidator.SameDirectory(DefaultDirectory, DefaultDirectory.ToUpperInvariant()));
        Assert.False(LogDirectoryValidator.SameDirectory(DefaultDirectory, OtherDirectory));
    }

    // ==================== 设置文件 ====================

    [Fact]
    public void Store_MissingFile_MeansNotConfigured()
    {
        var loaded = new LogLocationStore(LocationPath).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips_IncludingNonAsciiPath()
    {
        var store = new LogLocationStore(LocationPath);
        var directory = Path.Combine(_root, "日志");

        Assert.True(store.Save(directory).IsSuccess);
        Assert.Equal(directory, store.Load().Value);
        Assert.Contains("日志", File.ReadAllText(LocationPath));
        Assert.False(File.Exists(LocationPath + ".tmp"));

        Assert.True(store.Save(null).IsSuccess);
        Assert.Null(store.Load().Value);
    }

    [Fact]
    public void Store_AcceptsHandEditedFileWithComments()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, """
            {
              // 现场手工改的
              "logDirectory": "D:\\SolveLogs",
            }
            """);

        Assert.Equal(@"D:\SolveLogs", new LogLocationStore(LocationPath).Load().Value);
    }

    [Fact]
    public void Store_CorruptFile_ReportsFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, "{ \"LogDirectory\": ");

        var loaded = new LogLocationStore(LocationPath).Load();

        Assert.True(loaded.IsFailure);
        Assert.Equal(LogDirectoryErrors.LoadFailedCode, loaded.Error.Code);
    }

    // ==================== 运行中切换 ====================

    [Fact]
    public async Task SwitchDirectory_NewLogsGoToNewDirectory_OldLogsStay()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        await WriteAndWaitAsync(pipeline, "before-switch");
        Directory.CreateDirectory(OtherDirectory);

        pipeline.SwitchDirectory(OtherDirectory);
        For(pipeline).Information("after-switch");
        pipeline.Dispose();

        Assert.Equal(OtherDirectory, pipeline.LogDirectory);
        var oldLog = LogFiles.ReadAll(DefaultDirectory);
        var newLog = LogFiles.ReadAll(OtherDirectory);
        Assert.Contains("before-switch", oldLog);
        Assert.DoesNotContain("after-switch", oldLog);
        Assert.Contains("after-switch", newLog);
        Assert.DoesNotContain("before-switch", newLog);
    }

    [Fact]
    public void SwitchDirectory_AfterDispose_Throws()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        pipeline.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pipeline.SwitchDirectory(OtherDirectory));
    }

    [Fact]
    public async Task Service_Change_SwitchesSavesAndLogsInNewDirectory()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogLocationStore(LocationPath);
        var service = CreateService(pipeline, store);
        await WriteAndWaitAsync(pipeline, "before-switch");

        var result = await service.ChangeDirectoryAsync(OtherDirectory, CancellationToken.None);
        For(pipeline).Information("after-switch");
        pipeline.Dispose();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(OtherDirectory, result.Value);
        Assert.Equal(OtherDirectory, service.CurrentDirectory);
        Assert.Equal(OtherDirectory, store.Load().Value);
        var newLog = LogFiles.ReadAll(OtherDirectory);
        Assert.Contains("日志目录已切换", newLog);
        Assert.Contains("after-switch", newLog);
        Assert.Contains("before-switch", LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task Service_InvalidDirectory_KeepsCurrentDirectory_AndSavesNothing()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogLocationStore(LocationPath);
        var service = CreateService(pipeline, store);

        var result = await service.ChangeDirectoryAsync("relative-logs", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.InvalidPathCode, result.Error.Code);
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
        Assert.False(File.Exists(LocationPath));
    }

    [Fact]
    public async Task Service_RestoreDefault_SwitchesBack_AndClearsSetting()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogLocationStore(LocationPath);
        var service = CreateService(pipeline, store);
        Assert.True((await service.ChangeDirectoryAsync(OtherDirectory, CancellationToken.None)).IsSuccess);

        var result = await service.ChangeDirectoryAsync(null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
        Assert.True(store.Load().IsSuccess);
        Assert.Null(store.Load().Value);
    }

    [Fact]
    public async Task Service_ChoosingDefaultPathExplicitly_IsSavedAsNotConfigured()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogLocationStore(LocationPath);
        var service = CreateService(pipeline, store);

        var result = await service.ChangeDirectoryAsync(DefaultDirectory.ToUpperInvariant(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(File.Exists(LocationPath));
        if (OperatingSystem.IsWindows())
            Assert.Null(store.Load().Value);
    }

    [Fact]
    public async Task Service_WithoutStore_RefusesToSwitch()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, store: null);

        var result = await service.ChangeDirectoryAsync(OtherDirectory, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.SettingsUnavailableCode, result.Error.Code);
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
    }

    // ==================== 启动 ====================

    [Fact]
    public void ResolveStartup_WithoutStoreOrSetting_UsesDefault()
    {
        Assert.Equal(new LogDirectoryStartup(DefaultDirectory, null, null), LogDirectoryService.ResolveStartup(DefaultDirectory, null));
        Assert.Equal(new LogDirectoryStartup(DefaultDirectory, null, null),
            LogDirectoryService.ResolveStartup(DefaultDirectory, new LogLocationStore(LocationPath)));
    }

    [Fact]
    public void ResolveStartup_UsableSetting_IsUsed()
    {
        var store = new LogLocationStore(LocationPath);
        Assert.True(store.Save(OtherDirectory).IsSuccess);

        var startup = LogDirectoryService.ResolveStartup(DefaultDirectory, store);

        Assert.Equal(OtherDirectory, startup.Directory);
        Assert.Equal(OtherDirectory, startup.Configured);
        Assert.Null(startup.FallbackReason);
    }

    [Fact]
    public void ResolveStartup_UnusableSetting_FallsBackToDefault_AndKeepsSetting()
    {
        var store = new LogLocationStore(LocationPath);
        Assert.True(store.Save("relative-logs").IsSuccess);

        var startup = LogDirectoryService.ResolveStartup(DefaultDirectory, store);

        Assert.Equal(DefaultDirectory, startup.Directory);
        Assert.Equal("relative-logs", startup.Configured);
        Assert.NotNull(startup.FallbackReason);
        // 不改写设置：目录恢复可用后重启即可继续使用
        Assert.Equal("relative-logs", store.Load().Value);
    }

    [Fact]
    public void ResolveStartup_CorruptSetting_FallsBackToDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, "{ broken");

        var startup = LogDirectoryService.ResolveStartup(DefaultDirectory, new LogLocationStore(LocationPath));

        Assert.Equal(DefaultDirectory, startup.Directory);
        Assert.NotNull(startup.FallbackReason);
    }

    [Fact]
    public async Task AppHost_UsesSavedDirectory()
    {
        Assert.True(new LogLocationStore(LocationPath).Save(OtherDirectory).IsSuccess);

        var host = CreateHost();
        await host.DisposeAsync();

        Assert.Equal(OtherDirectory, host.Logging.LogDirectory);
        Assert.Equal(OtherDirectory, host.LogDirectorySettings.CurrentDirectory);
        Assert.Contains("程序启动", LogFiles.ReadAll(OtherDirectory));
        Assert.Equal(string.Empty, LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task AppHost_UnusableSavedDirectory_LogsWarningInDefaultDirectory()
    {
        Assert.True(new LogLocationStore(LocationPath).Save("relative-logs").IsSuccess);

        var host = CreateHost();
        await host.DisposeAsync();

        Assert.Equal(DefaultDirectory, host.LogDirectorySettings.CurrentDirectory);
        Assert.NotNull(host.LogDirectorySettings.StartupFallbackReason);
        var log = LogFiles.ReadAll(DefaultDirectory);
        Assert.Contains("[WRN]", log);
        Assert.Contains("保存的日志目录不可用", log);
    }

    [Fact]
    public void DesktopOptions_LogLocationFile_IsUnderConfig()
    {
        var options = DesktopOptions.FromArgs([], _root);

        Assert.Equal(Path.Combine(_root, "config", LogLocationStore.FileName), options.LogLocationPath);
    }
}
