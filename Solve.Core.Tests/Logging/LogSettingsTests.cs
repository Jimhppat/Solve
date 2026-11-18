using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

/// <summary>
/// 日志设置：目录校验、设置文件、启动时的退回、运行中保存（切换目录、保存天数、单个文件上限）与立即清理。
/// 每个测试使用独立临时目录，只在本机临时目录内读写。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class LogSettingsTests : IAsyncDisposable
{
    private const string Context = "Solve.Core.Application.Buses.BusManager";

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly List<LoggingPipeline> _pipelines = [];
    private readonly List<AppHost> _hosts = [];

    private string DefaultDirectory => Path.Combine(_root, "logs");

    private string OtherDirectory => Path.Combine(_root, "other-logs");

    private string LocationPath => Path.Combine(_root, "config", LogSettingsStore.FileName);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static LogRetentionPolicy Policy(int days, int megabytes = LogRetentionPolicy.DefaultFileSizeLimitMegabytes)
        => LogRetentionPolicy.Create(days, megabytes).Value;

    private LoggingPipeline CreatePipeline(string directory, LogRetentionPolicy? retention = null)
    {
        var pipeline = LoggingPipeline.Create(directory, settingsPath: null, retention);
        _pipelines.Add(pipeline);
        return pipeline;
    }

    // 服务的日志写进同一条管线：切换、清理的说明日志也要能在文件里断言
    private LogSettingsService CreateService(LoggingPipeline pipeline, LogSettingsStore? store)
        => new(pipeline, store, DefaultDirectory, new LogSettingsStartup(pipeline.LogDirectory, null, pipeline.Retention, null),
            new SerilogLoggerFactory(pipeline.Logger, dispose: false).CreateLogger(typeof(LogSettingsService).FullName!));

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

    private static string Touch(string directory, DateOnly date, int bytes = 3)
    {
        Directory.CreateDirectory(directory);
        var path = DailyLogFiles.PathFor(directory, date);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// 写一条日志并等它落盘：切换前队列里的日志可能落在任一目录，等落盘后再切换，断言才确定
    /// </summary>
    private static async Task WriteAndWaitAsync(LoggingPipeline pipeline, string marker)
    {
        var directory = pipeline.LogDirectory;
        For(pipeline).Information(marker);
        Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(directory).Contains(marker)), $"{marker} 未在 10 秒内写入 {directory}");
    }

    private static IReadOnlyList<string> FileNames(string directory)
        => DailyLogFiles.List(directory).Select(f => Path.GetFileName(f.Path)).ToList();

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

    // ==================== 保留设置的取值范围 ====================

    [Theory]
    [InlineData(1, 10)]
    [InlineData(365, 1024)]
    [InlineData(30, 50)]
    public void Policy_AcceptsValuesInRange(int days, int megabytes)
    {
        var policy = LogRetentionPolicy.Create(days, megabytes);

        Assert.True(policy.IsSuccess);
        Assert.Equal(megabytes * 1024L * 1024L, policy.Value.FileSizeLimitBytes);
    }

    [Theory]
    [InlineData(0, 50, LogRetentionErrors.RetentionDaysOutOfRangeCode)]
    [InlineData(366, 50, LogRetentionErrors.RetentionDaysOutOfRangeCode)]
    [InlineData(30, 9, LogRetentionErrors.FileSizeLimitOutOfRangeCode)]
    [InlineData(30, 1025, LogRetentionErrors.FileSizeLimitOutOfRangeCode)]
    public void Policy_RejectsValuesOutOfRange(int days, int megabytes, string code)
    {
        var policy = LogRetentionPolicy.Create(days, megabytes);

        Assert.True(policy.IsFailure);
        Assert.Equal(code, policy.Error.Code);
    }

    [Fact]
    public void Policy_ComparesByValue()
    {
        Assert.Equal(Policy(30), LogRetentionPolicy.Default);
        Assert.NotEqual(Policy(7), LogRetentionPolicy.Default);
    }

    // ==================== 设置文件 ====================

    [Fact]
    public void Store_MissingFile_MeansNotConfigured()
    {
        var loaded = new LogSettingsStore(LocationPath).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Equal(StoredLogSettings.NotConfigured, loaded.Value);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips_IncludingNonAsciiPath()
    {
        var store = new LogSettingsStore(LocationPath);
        var directory = Path.Combine(_root, "日志");

        Assert.True(store.Save(new StoredLogSettings(directory, 90, 200)).IsSuccess);
        Assert.Equal(new StoredLogSettings(directory, 90, 200), store.Load().Value);
        Assert.Contains("日志", File.ReadAllText(LocationPath));
        Assert.False(File.Exists(LocationPath + ".tmp"));

        Assert.True(store.Save(StoredLogSettings.NotConfigured).IsSuccess);
        Assert.Equal(StoredLogSettings.NotConfigured, store.Load().Value);
    }

    [Fact]
    public void Store_FileFromEarlierVersion_WithOnlyDirectory_StillLoads()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, """{ "LogDirectory": "D:\\SolveLogs" }""");

        var loaded = new LogSettingsStore(LocationPath).Load();

        Assert.Equal(new StoredLogSettings(@"D:\SolveLogs", null, null), loaded.Value);
    }

    [Fact]
    public void Store_AcceptsHandEditedFile_WithCommentsAndQuotedNumbers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, """
            {
              // 现场手工改的
              "logDirectory": "D:\\SolveLogs",
              "retentionDays": "60",
              "fileSizeLimitMegabytes": 100,
            }
            """);

        Assert.Equal(new StoredLogSettings(@"D:\SolveLogs", 60, 100), new LogSettingsStore(LocationPath).Load().Value);
    }

    [Fact]
    public void Store_CorruptFile_ReportsFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, "{ \"LogDirectory\": ");

        var loaded = new LogSettingsStore(LocationPath).Load();

        Assert.True(loaded.IsFailure);
        Assert.Equal(LogDirectoryErrors.LoadFailedCode, loaded.Error.Code);
    }

    // ==================== 运行中切换目录 ====================

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
        Assert.Throws<ObjectDisposedException>(() => pipeline.ApplyRetention(Policy(7)));
    }

    [Fact]
    public async Task Service_Save_SwitchesDirectory_SavesAndLogsInNewDirectory()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogSettingsStore(LocationPath);
        var service = CreateService(pipeline, store);
        await WriteAndWaitAsync(pipeline, "before-switch");

        var result = await service.SaveAsync(OtherDirectory, LogRetentionPolicy.Default, CancellationToken.None);
        For(pipeline).Information("after-switch");
        pipeline.Dispose();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(OtherDirectory, result.Value.Directory);
        Assert.True(result.Value.Deleted.IsEmpty);
        Assert.Equal(OtherDirectory, service.CurrentDirectory);
        Assert.Equal(OtherDirectory, store.Load().Value.LogDirectory);
        var newLog = LogFiles.ReadAll(OtherDirectory);
        Assert.Contains("日志目录已切换", newLog);
        Assert.Contains("after-switch", newLog);
        Assert.Contains("before-switch", LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task Service_InvalidDirectory_ChangesNothing_AndSavesNothing()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogSettingsStore(LocationPath);
        var service = CreateService(pipeline, store);

        var result = await service.SaveAsync("relative-logs", Policy(7), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.InvalidPathCode, result.Error.Code);
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
        Assert.Equal(LogRetentionPolicy.Default, service.Retention);
        Assert.False(File.Exists(LocationPath));
    }

    [Fact]
    public async Task Service_RestoreDefault_SwitchesBack_AndClearsDirectorySetting()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogSettingsStore(LocationPath);
        var service = CreateService(pipeline, store);
        Assert.True((await service.SaveAsync(OtherDirectory, LogRetentionPolicy.Default, CancellationToken.None)).IsSuccess);

        var result = await service.SaveAsync(null, LogRetentionPolicy.Default, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
        Assert.True(store.Load().IsSuccess);
        Assert.Null(store.Load().Value.LogDirectory);
    }

    [Fact]
    public async Task Service_ChoosingDefaultPathExplicitly_IsSavedAsNotConfigured()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogSettingsStore(LocationPath);
        var service = CreateService(pipeline, store);

        var result = await service.SaveAsync(DefaultDirectory.ToUpperInvariant(), LogRetentionPolicy.Default, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(File.Exists(LocationPath));
        if (OperatingSystem.IsWindows())
            Assert.Null(store.Load().Value.LogDirectory);
    }

    [Fact]
    public async Task Service_WithoutStore_RefusesToSave()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, store: null);

        var result = await service.SaveAsync(OtherDirectory, Policy(7), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(LogDirectoryErrors.SettingsUnavailableCode, result.Error.Code);
        Assert.Equal(DefaultDirectory, service.CurrentDirectory);
        Assert.Equal(LogRetentionPolicy.Default, service.Retention);
    }

    // ==================== 保存天数：预览与立即清理 ====================

    [Fact]
    public async Task Preview_ListsWhatWouldBeDeleted_WithoutDeletingAnything()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, new LogSettingsStore(LocationPath));
        var tenDaysAgo = Touch(DefaultDirectory, Today.AddDays(-10), bytes: 2048);
        var twelveDaysAgo = Touch(DefaultDirectory, Today.AddDays(-12), bytes: 1024);
        var threeDaysAgo = Touch(DefaultDirectory, Today.AddDays(-3));

        var preview = await service.PreviewAsync(DefaultDirectory, Policy(7), CancellationToken.None);

        Assert.True(preview.IsSuccess, preview.ToString());
        Assert.Equal(new LogPrunePreview(2, 3072, Today.AddDays(-12), Today.AddDays(-10)), preview.Value);
        Assert.True(File.Exists(tenDaysAgo));
        Assert.True(File.Exists(twelveDaysAgo));
        Assert.True(File.Exists(threeDaysAgo));
        Assert.False(File.Exists(LocationPath));
    }

    [Theory]
    [InlineData("relative-logs")]
    [InlineData(@"\\server\share\logs")]
    public async Task Preview_UnusablePath_HasNothingToDelete(string directory)
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, new LogSettingsStore(LocationPath));

        var preview = await service.PreviewAsync(directory, Policy(1), CancellationToken.None);

        Assert.True(preview.IsSuccess);
        Assert.True(preview.Value.IsEmpty);
    }

    [Fact]
    public async Task Preview_MissingDirectory_HasNothingToDelete_AndDoesNotCreateIt()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, new LogSettingsStore(LocationPath));

        var preview = await service.PreviewAsync(OtherDirectory, Policy(1), CancellationToken.None);

        Assert.True(preview.Value.IsEmpty);
        Assert.False(Directory.Exists(OtherDirectory));
    }

    [Fact]
    public async Task Save_ShorterRetention_DeletesExpiredFilesImmediately_AndKeepsTheRest()
    {
        var tenDaysAgo = Touch(DefaultDirectory, Today.AddDays(-10));
        var threeDaysAgo = Touch(DefaultDirectory, Today.AddDays(-3));
        var pipeline = CreatePipeline(DefaultDirectory);
        var store = new LogSettingsStore(LocationPath);
        var service = CreateService(pipeline, store);
        await WriteAndWaitAsync(pipeline, "today");

        var result = await service.SaveAsync(DefaultDirectory, Policy(7), CancellationToken.None);
        pipeline.Dispose();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(new LogPrunePreview(1, 3, Today.AddDays(-10), Today.AddDays(-10)), result.Value.Deleted);
        Assert.False(File.Exists(tenDaysAgo));
        Assert.True(File.Exists(threeDaysAgo));
        Assert.True(File.Exists(DailyLogFiles.PathFor(DefaultDirectory, Today)));
        Assert.Equal(Policy(7), service.Retention);
        Assert.Equal(new StoredLogSettings(null, 7, LogRetentionPolicy.DefaultFileSizeLimitMegabytes), store.Load().Value);
        Assert.Contains("已按日志保留设置删除 1 个日志文件", LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task Save_OneDayRetention_NeverDeletesTodaysFiles()
    {
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, new LogSettingsStore(LocationPath));
        await WriteAndWaitAsync(pipeline, "today");

        var result = await service.SaveAsync(DefaultDirectory, Policy(1), CancellationToken.None);
        await WriteAndWaitAsync(pipeline, "still-writing");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(result.Value.Deleted.IsEmpty);
        Assert.Contains("today", LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task Save_LongerRetentionAndNewDirectory_NewRetentionAppliesBeforeTheNewDirectoryIsPruned()
    {
        // 新目录里有 60 天前的日志：若先切目录再改天数，新目录第一次打开文件时会按旧的 30 天把它删掉
        var sixtyDaysAgo = Touch(OtherDirectory, Today.AddDays(-60));
        var pipeline = CreatePipeline(DefaultDirectory);
        var service = CreateService(pipeline, new LogSettingsStore(LocationPath));

        var result = await service.SaveAsync(OtherDirectory, Policy(90), CancellationToken.None);
        await WriteAndWaitAsync(pipeline, "in-new-directory");
        pipeline.Dispose();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(File.Exists(sixtyDaysAgo));
    }

    // ==================== 单个文件上限 ====================

    [Fact]
    public async Task FileSizeLimit_Exceeded_ContinuesInNextVolume()
    {
        var pipeline = CreatePipeline(DefaultDirectory, Policy(30, LogRetentionPolicy.MinFileSizeLimitMegabytes));

        For(pipeline).Information(new string('x', 11 * 1024 * 1024));
        await WriteAndWaitAsync(pipeline, "second-volume");
        pipeline.Dispose();

        var date = DailyLogFiles.FileName(Today)[..^4];
        Assert.Equal([$"{date}.log", $"{date}_001.log"], FileNames(DefaultDirectory));
        Assert.Contains("second-volume", LogFiles.Read(DailyLogFiles.List(DefaultDirectory)[1].Path));
    }

    [Fact]
    public async Task SmallerFileSizeLimit_AppliesToTheNextLog()
    {
        var pipeline = CreatePipeline(DefaultDirectory, Policy(30, 50));
        For(pipeline).Information(new string('x', 11 * 1024 * 1024));
        await WriteAndWaitAsync(pipeline, "first-volume");

        // 当前文件已超过新的上限：下一条写进新的分卷
        pipeline.ApplyRetention(Policy(30, LogRetentionPolicy.MinFileSizeLimitMegabytes));
        await WriteAndWaitAsync(pipeline, "after-change");
        pipeline.Dispose();

        var files = DailyLogFiles.List(DefaultDirectory);
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain("after-change", LogFiles.Read(files[0].Path));
        Assert.Contains("after-change", LogFiles.Read(files[1].Path));
    }

    [Fact]
    public async Task ChangingFileSizeLimit_UnderTheLimit_KeepsWritingTheSameFile()
    {
        // 重新打开文件输出时若旧句柄未先关闭，新输出打不开同一文件，会平白多出一个 _001
        var pipeline = CreatePipeline(DefaultDirectory, Policy(30, 50));
        await WriteAndWaitAsync(pipeline, "before-change");

        pipeline.ApplyRetention(Policy(30, 100));
        await WriteAndWaitAsync(pipeline, "after-change");
        pipeline.Dispose();

        Assert.Equal([DailyLogFiles.FileName(Today)], FileNames(DefaultDirectory));
        Assert.Equal(Policy(30, 100), pipeline.Retention);
    }

    // ==================== 启动 ====================

    [Fact]
    public void ResolveStartup_WithoutStoreOrSetting_UsesDefaults()
    {
        var expected = new LogSettingsStartup(DefaultDirectory, null, LogRetentionPolicy.Default, null);

        Assert.Equal(expected, LogSettingsService.ResolveStartup(DefaultDirectory, null));
        Assert.Equal(expected, LogSettingsService.ResolveStartup(DefaultDirectory, new LogSettingsStore(LocationPath)));
    }

    [Fact]
    public void ResolveStartup_UsableSettings_AreUsed()
    {
        var store = new LogSettingsStore(LocationPath);
        Assert.True(store.Save(new StoredLogSettings(OtherDirectory, 90, 200)).IsSuccess);

        var startup = LogSettingsService.ResolveStartup(DefaultDirectory, store);

        Assert.Equal(new LogSettingsStartup(OtherDirectory, OtherDirectory, Policy(90, 200), null), startup);
    }

    [Fact]
    public void ResolveStartup_UnusableDirectory_FallsBackToDefault_AndKeepsSetting()
    {
        var store = new LogSettingsStore(LocationPath);
        Assert.True(store.Save(new StoredLogSettings("relative-logs", 90)).IsSuccess);

        var startup = LogSettingsService.ResolveStartup(DefaultDirectory, store);

        Assert.Equal(DefaultDirectory, startup.Directory);
        Assert.Equal("relative-logs", startup.ConfiguredDirectory);
        Assert.Equal(90, startup.Retention.RetentionDays);
        Assert.Contains("保存的日志目录不可用", startup.FallbackReason);
        // 不改写设置：目录恢复可用后重启即可继续使用
        Assert.Equal("relative-logs", store.Load().Value.LogDirectory);
    }

    [Theory]
    [InlineData(0, 50, "保存天数 0")]
    [InlineData(9999, 50, "保存天数 9999")]
    [InlineData(30, 1, "单个文件上限 1 MB")]
    [InlineData(30, 4096, "单个文件上限 4096 MB")]
    public void ResolveStartup_OutOfRangeRetention_FallsBackPerItem(int days, int megabytes, string expected)
    {
        var store = new LogSettingsStore(LocationPath);
        Assert.True(store.Save(new StoredLogSettings(null, days, megabytes)).IsSuccess);

        var startup = LogSettingsService.ResolveStartup(DefaultDirectory, store);

        Assert.Contains(expected, startup.FallbackReason);
        Assert.InRange(startup.Retention.RetentionDays, LogRetentionPolicy.MinRetentionDays, LogRetentionPolicy.MaxRetentionDays);
        Assert.InRange(startup.Retention.FileSizeLimitMegabytes,
            LogRetentionPolicy.MinFileSizeLimitMegabytes, LogRetentionPolicy.MaxFileSizeLimitMegabytes);
        // 另一项是合法值时照常使用
        if (days is >= LogRetentionPolicy.MinRetentionDays and <= LogRetentionPolicy.MaxRetentionDays)
            Assert.Equal(days, startup.Retention.RetentionDays);
        else
            Assert.Equal(megabytes, startup.Retention.FileSizeLimitMegabytes);
    }

    [Fact]
    public void ResolveStartup_SeveralProblems_AreAllReported()
    {
        var store = new LogSettingsStore(LocationPath);
        Assert.True(store.Save(new StoredLogSettings("relative-logs", 0, 1)).IsSuccess);

        var reason = LogSettingsService.ResolveStartup(DefaultDirectory, store).FallbackReason;

        Assert.Contains("保存的日志目录不可用", reason);
        Assert.Contains("保存天数 0", reason);
        Assert.Contains("单个文件上限 1 MB", reason);
    }

    [Fact]
    public void ResolveStartup_CorruptSetting_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocationPath)!);
        File.WriteAllText(LocationPath, "{ broken");

        var startup = LogSettingsService.ResolveStartup(DefaultDirectory, new LogSettingsStore(LocationPath));

        Assert.Equal(DefaultDirectory, startup.Directory);
        Assert.Equal(LogRetentionPolicy.Default, startup.Retention);
        Assert.NotNull(startup.FallbackReason);
    }

    [Fact]
    public async Task AppHost_UsesSavedSettings()
    {
        Assert.True(new LogSettingsStore(LocationPath).Save(new StoredLogSettings(OtherDirectory, 90, 200)).IsSuccess);

        var host = CreateHost();
        await host.DisposeAsync();

        Assert.Equal(OtherDirectory, host.Logging.LogDirectory);
        Assert.Equal(OtherDirectory, host.LogSettings.CurrentDirectory);
        Assert.Equal(Policy(90, 200), host.Logging.Retention);
        Assert.Equal(Policy(90, 200), host.LogSettings.Retention);
        Assert.Contains("程序启动", LogFiles.ReadAll(OtherDirectory));
        Assert.Equal(string.Empty, LogFiles.ReadAll(DefaultDirectory));
    }

    [Fact]
    public async Task AppHost_UnusableSavedDirectory_LogsWarningInDefaultDirectory()
    {
        Assert.True(new LogSettingsStore(LocationPath).Save(new StoredLogSettings("relative-logs")).IsSuccess);

        var host = CreateHost();
        await host.DisposeAsync();

        Assert.Equal(DefaultDirectory, host.LogSettings.CurrentDirectory);
        Assert.NotNull(host.LogSettings.StartupFallbackReason);
        var log = LogFiles.ReadAll(DefaultDirectory);
        Assert.Contains("[WRN]", log);
        Assert.Contains("保存的日志目录不可用", log);
    }

    [Fact]
    public void DesktopOptions_LogSettingsFile_IsUnderConfig_WithTheOriginalName()
    {
        var options = DesktopOptions.FromArgs([], _root);

        Assert.Equal(Path.Combine(_root, "config", "log-location.json"), options.LogLocationPath);
    }
}
