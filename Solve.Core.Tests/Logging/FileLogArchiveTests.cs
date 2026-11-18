using Microsoft.Extensions.Logging;
using Serilog.Core;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

/// <summary>
/// 历史日志读取与导出。往返测试由日志管线真实写盘再读回，把输出模板与解析器的约定钉住。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class FileLogArchiveTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 16);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly FileLogArchive _archive;

    public FileLogArchiveTests()
    {
        Directory.CreateDirectory(_directory);
        _archive = new FileLogArchive(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private void WriteFile(string name, params string[] lines)
        => File.WriteAllLines(Path.Combine(_directory, name), lines);

    private static string Line(string level, string source, string message, int second = 0)
        => $"2026-11-16 10:00:{second:00}.000 +09:00 [{level}] [7] {source}: {message}";

    private async Task<LogDay> ReadAsync(DateOnly date, int maxEntries = 100)
    {
        var result = await _archive.ReadDayAsync(date, maxEntries, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    // ==================== 往返 ====================

    [Fact]
    public async Task RoundTrip_ThroughPipeline_RestoresLevelSourceMessageAndException()
    {
        var pipeline = LoggingPipeline.Create(_directory, settingsPath: null);
        var failure = CaptureException();

        pipeline.Logger.ForContext(Constants.SourceContextPropertyName, "Solve.Core.Application.Buses.BusRuntime")
            .Information("总线 {Name} 模式={Mode}", "PLC-01", DayOfWeek.Monday);
        pipeline.Logger.Warning("没有来源的告警: 冒号也在消息里");
        pipeline.Logger.ForContext(Constants.SourceContextPropertyName, "Solve.Desktop.Composition.AppHost")
            .Fatal(failure, "启动失败");
        pipeline.Dispose();

        var day = await ReadAsync(Today);

        Assert.Equal(3, day.TotalCount);
        Assert.False(day.IsTruncated);

        var info = day.Entries[0];
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Equal("Solve.Core.Application.Buses.BusRuntime", info.Source);
        Assert.Equal("总线 PLC-01 模式=Monday", info.Message);
        Assert.Null(info.Exception);

        var warning = day.Entries[1];
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(string.Empty, warning.Source);
        Assert.Equal("没有来源的告警: 冒号也在消息里", warning.Message);

        var fatal = day.Entries[2];
        Assert.Equal(LogLevel.Critical, fatal.Level);
        Assert.Equal("启动失败", fatal.Message);
        Assert.NotNull(fatal.Exception);
        Assert.Contains("配置库损坏", fatal.Exception);
        Assert.Contains(nameof(CaptureException), fatal.Exception);
        Assert.True(Math.Abs((fatal.Timestamp - DateTimeOffset.Now).TotalMinutes) < 5);
    }

    private static InvalidOperationException CaptureException()
    {
        try
        {
            throw new InvalidOperationException("配置库损坏");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task TodaysFile_IsReadableWhilePipelineIsStillWriting()
    {
        var pipeline = LoggingPipeline.Create(_directory, settingsPath: null);
        try
        {
            pipeline.Logger.Information("still-open");

            var seen = await LogFiles.EventuallyAsync(
                () => File.Exists(DailyLogFiles.PathFor(_directory, Today))
                      && LogFiles.Read(DailyLogFiles.PathFor(_directory, Today)).Contains("still-open"));
            Assert.True(seen);

            var day = await ReadAsync(Today);
            Assert.Contains(day.Entries, e => e.Message == "still-open");
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    // ==================== 文件约定 ====================

    [Fact]
    public async Task LegacyFiles_AreReadable()
    {
        // 升级前的格式：没有 [线程号]
        WriteFile("solve-20261116.log",
            "2026-11-16 09:00:00.000 +09:00 [INF] Solve.Desktop.Composition.AppHost: 程序启动 进程=7472",
            "2026-11-16 09:00:01.000 +09:00 [ERR] Solve.Core.Application.Buses.BusManager: 全部总线安全跳闸（1 条）");

        var day = await ReadAsync(Day);

        Assert.Equal(["程序启动 进程=7472", "全部总线安全跳闸（1 条）"], day.Entries.Select(e => e.Message));
        Assert.Equal([LogLevel.Information, LogLevel.Error], day.Entries.Select(e => e.Level));
    }

    [Fact]
    public async Task AllVolumesOfADay_AreReadInOrder_AndExportedConcatenated()
    {
        WriteFile("2026_11_16_001.log", Line("INF", "Solve.Test", "third", 3));
        WriteFile("2026_11_16.log", Line("INF", "Solve.Test", "second", 2));
        WriteFile("solve-20261116.log", Line("INF", "Solve.Test", "first", 1));
        WriteFile("2026_11_17.log", Line("INF", "Solve.Test", "other-day"));

        var day = await ReadAsync(Day);
        Assert.Equal(["first", "second", "third"], day.Entries.Select(e => e.Message));

        using var exported = new MemoryStream();
        var result = await _archive.ExportDayAsync(Day, exported, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var text = System.Text.Encoding.UTF8.GetString(exported.ToArray());
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
        Assert.True(text.IndexOf("second", StringComparison.Ordinal) < text.IndexOf("third", StringComparison.Ordinal));
        Assert.DoesNotContain("other-day", text);
    }

    [Fact]
    public async Task ContinuationLines_BecomeException_AndLinesBeforeFirstHeaderAreSkipped()
    {
        WriteFile("2026_11_16.log",
            "   at Truncated.Previous.Line()",
            Line("ERR", "Solve.Test", "boom"),
            "System.IO.IOException: 端口被占用",
            "   at Solve.Port.Open()",
            "",
            Line("INF", "Solve.Test", "after"));

        var day = await ReadAsync(Day);

        Assert.Equal(2, day.TotalCount);
        Assert.Equal($"System.IO.IOException: 端口被占用{Environment.NewLine}   at Solve.Port.Open()", day.Entries[0].Exception);
        Assert.Null(day.Entries[1].Exception);
    }

    [Fact]
    public async Task LargeDay_KeepsOnlyTheLastEntries_AndReportsTotal()
    {
        WriteFile("2026_11_16.log", Enumerable.Range(0, 25).Select(i => Line("INF", "Solve.Test", $"m{i}", i % 60)).ToArray());

        var day = await ReadAsync(Day, maxEntries: 10);

        Assert.True(day.IsTruncated);
        Assert.Equal(25, day.TotalCount);
        Assert.Equal(Enumerable.Range(15, 10).Select(i => $"m{i}"), day.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task AvailableDates_AreDistinctNewestFirst_IncludingLegacy()
    {
        WriteFile("solve-20261115.log", Line("INF", "s", "a"));
        WriteFile("2026_11_16.log", Line("INF", "s", "b"));
        WriteFile("2026_11_16_001.log", Line("INF", "s", "c"));
        WriteFile("2026_11_17.log", Line("INF", "s", "d"));
        WriteFile("notes.log", "not a log");

        var dates = await _archive.GetAvailableDatesAsync(CancellationToken.None);

        Assert.True(dates.IsSuccess);
        Assert.Equal([new DateOnly(2026, 11, 17), Day, new DateOnly(2026, 11, 15)], dates.Value);
    }

    [Fact]
    public async Task MissingDay_IsAFailure_NotAnException()
    {
        var read = await _archive.ReadDayAsync(Day, 10, CancellationToken.None);
        var export = await _archive.ExportDayAsync(Day, new MemoryStream(), CancellationToken.None);

        Assert.Equal("Log.DayNotFound", read.Error.Code);
        Assert.Equal("Log.DayNotFound", export.Error.Code);
    }

    [Fact]
    public async Task CanceledRead_Throws()
    {
        WriteFile("2026_11_16.log", Line("INF", "s", "a"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _archive.ReadDayAsync(Day, 10, cts.Token));
    }

    [Fact]
    public void SuggestedExportName_FollowsDailyNaming()
    {
        Assert.Equal("2026_11_16.log", _archive.SuggestExportFileName(Day));
    }

    // ==================== 与设置页切换日志目录的配合 ====================

    [Fact]
    public async Task AfterSwitchingDirectory_DateNamedFilesAndArchiveFollowTheNewDirectory()
    {
        var first = Path.Combine(_directory, "first");
        var second = Path.Combine(_directory, "second");
        Directory.CreateDirectory(second);
        var pipeline = LoggingPipeline.Create(first, settingsPath: null);
        var archive = new FileLogArchive(() => pipeline.LogDirectory);
        try
        {
            pipeline.Logger.Information("in-first");
            Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(first).Contains("in-first")));

            pipeline.SwitchDirectory(second);
            pipeline.Logger.Information("in-second");
            Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(second).Contains("in-second")));
        }
        finally
        {
            pipeline.Dispose();
        }

        // 新目录同样按日期命名
        Assert.Equal(DailyLogFiles.FileName(Today), Path.GetFileName(Assert.Single(DailyLogFiles.List(second)).Path));

        // 历史日志读的是当前目录：只有切换后的内容
        var day = await archive.ReadDayAsync(Today, 100, CancellationToken.None);
        Assert.True(day.IsSuccess);
        Assert.Contains(day.Value.Entries, e => e.Message == "in-second");
        Assert.DoesNotContain(day.Value.Entries, e => e.Message == "in-first");
    }

    [Fact]
    public async Task AfterSwitchingDirectory_RetentionCleansOnlyTheNewDirectory()
    {
        var first = Path.Combine(_directory, "first");
        var second = Path.Combine(_directory, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var expiredName = DailyLogFiles.FileName(Today.AddDays(-DailyLogFiles.RetentionDays));

        var pipeline = LoggingPipeline.Create(first, settingsPath: null);
        try
        {
            pipeline.Logger.Information("opens-first");
            Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(first).Contains("opens-first")));

            pipeline.SwitchDirectory(second);
            // 切换之后才放进旧目录的过期文件：旧目录已不归本管线管理，原地保留
            var leftInFirst = Path.Combine(first, expiredName);
            var expiredInSecond = Path.Combine(second, expiredName);
            File.WriteAllText(leftInFirst, "old");
            File.WriteAllText(expiredInSecond, "old");

            pipeline.Logger.Information("opens-second");
            Assert.True(await LogFiles.EventuallyAsync(() => LogFiles.ReadAll(second).Contains("opens-second")));

            Assert.True(File.Exists(leftInFirst));
            Assert.False(File.Exists(expiredInSecond));
        }
        finally
        {
            pipeline.Dispose();
        }
    }
}
