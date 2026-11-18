using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 运行日志页按日期查看历史与导出。替身的读取在调用线程上同步完成（或由测试手动放行），不依赖 UI 线程。
/// </summary>
public sealed class RuntimeLogHistoryTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly DateOnly Tuesday = new(2026, 11, 17);

    private readonly RuntimeLogViewModel _vm = new();
    private readonly FakeLogArchive _archive = new();
    private readonly FakeLogFeed _feed = new([]);

    public RuntimeLogHistoryTests()
    {
        _archive.AddDay(Monday, [Entry("mon-info"), Entry("mon-warn", LogLevel.Warning)]);
        _archive.AddDay(Tuesday, [Entry("tue-error", LogLevel.Error)]);
        _vm.Attach(_feed, @"C:\logs", _archive);
    }

    public void Dispose() => _vm.Dispose();

    private static LogEntry Entry(string message, LogLevel level = LogLevel.Information)
        => new(DateTimeOffset.Now, level, "Solve.Test", message, null);

    private IReadOnlyList<string> Shown() => _vm.Entries.Select(e => e.Message).ToList();

    private LogSourceOption Option(DateOnly date) => _vm.Sources.Single(o => o.Date == date);

    // ==================== 来源列表 ====================

    [Fact]
    public async Task Sources_LiveFirst_ThenDatesNewestFirst()
    {
        await _vm.RefreshSourcesAsync();

        Assert.True(_vm.CanBrowseHistory);
        Assert.Equal([null, Tuesday, Monday], _vm.Sources.Select(o => o.Date));
        Assert.Equal("实时", _vm.Sources[0].Label);
        Assert.Equal("2026-11-17", _vm.Sources[1].Label);
    }

    [Fact]
    public async Task Sources_MarkToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _archive.AddDay(today, [Entry("today")]);

        await _vm.RefreshSourcesAsync();

        Assert.Equal($"{today:yyyy-MM-dd}（今天）", Option(today).Label);
    }

    [Fact]
    public async Task Refresh_KeepsViewedDate_EvenIfItWasJustDeleted()
    {
        await _vm.SelectSourceAsync(Option(Monday));
        _archive.Days.Remove(Monday);
        _archive.AddDay(new DateOnly(2026, 11, 18), [Entry("new-day")]);

        await _vm.RefreshSourcesAsync();

        Assert.Equal(Monday, _vm.SelectedSource.Date);
        Assert.Contains(_vm.Sources, o => o.Date == Monday);
        Assert.Contains(_vm.Sources, o => o.Date == new DateOnly(2026, 11, 18));
        Assert.Equal(["mon-info", "mon-warn"], Shown());
    }

    [Fact]
    public void WithoutArchive_HistoryIsUnavailable()
    {
        using var recent = new RuntimeLogViewModel(100, LogCategories.Operational);
        recent.Attach(new FakeLogFeed([]), @"C:\logs");

        Assert.False(recent.CanBrowseHistory);
        Assert.False(recent.CanExport);
        Assert.Equal([LogSourceOption.Live], recent.Sources);
    }

    // ==================== 查看历史 ====================

    [Fact]
    public async Task SelectingADate_ShowsThatDay_AndDisablesLiveOnlyActions()
    {
        _vm.Append([Entry("live-1")]);

        await _vm.SelectSourceAsync(Option(Monday));

        Assert.False(_vm.IsLive);
        Assert.False(_vm.IsLoading);
        Assert.Equal(["mon-info", "mon-warn"], Shown());
        Assert.Equal("该日共 2 条", _vm.StatusText);
        Assert.Equal(1, _vm.WarningCount);
        Assert.False(_vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task TruncatedDay_SaysHowManyAreShown()
    {
        _archive.AddDay(Monday, [Entry("last")], totalCount: 25_000);

        await _vm.SelectSourceAsync(Option(Monday));

        Assert.Equal("该日共 25000 条，仅显示最后 1 条", _vm.StatusText);
    }

    [Fact]
    public async Task LiveLogs_KeepAccumulatingWhileViewingHistory_AndReappearWhenSwitchingBack()
    {
        _vm.Append([Entry("live-1")]);
        await _vm.SelectSourceAsync(Option(Monday));

        _vm.Append([Entry("live-2")]);
        Assert.Equal(["mon-info", "mon-warn"], Shown());
        Assert.Equal(0, _vm.UnseenCount);

        await _vm.SelectSourceAsync(LogSourceOption.Live);

        Assert.True(_vm.IsLive);
        Assert.Null(_vm.StatusText);
        Assert.Equal(["live-1", "live-2"], Shown());
        Assert.True(_vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task LevelFilterAndSearch_ApplyToHistory()
    {
        await _vm.SelectSourceAsync(Option(Monday));

        _vm.SetShownCategories([LogCategory.Warning]);
        Assert.Equal(["mon-warn"], Shown());

        _vm.SetShownCategories(LogCategories.All);
        _vm.SearchText = "INFO";
        Assert.Equal(["mon-info"], Shown());
    }

    [Fact]
    public async Task ReadFailure_IsShownAsStatus_NotThrown()
    {
        _archive.ReadError = new Error("Log.ReadFailed", "文件被占用");

        await _vm.SelectSourceAsync(Option(Monday));

        Assert.Equal("读取失败：文件被占用", _vm.StatusText);
        Assert.False(_vm.IsLoading);
        Assert.True(_vm.IsEmpty);
        Assert.Equal("该日没有可显示的日志", _vm.EmptyText);
    }

    [Fact]
    public async Task ArchiveThrowing_IsContained()
    {
        _archive.ReadException = new IOException("磁盘错误");

        await _vm.SelectSourceAsync(Option(Monday));

        Assert.Equal("读取失败：磁盘错误", _vm.StatusText);
        Assert.False(_vm.IsLoading);
    }

    [Fact]
    public async Task WhileLoading_ShowsNothingButTheLoadingHint()
    {
        _vm.Append([Entry("live-1")]);
        var held = _archive.Hold(Monday);

        var loading = _vm.SelectSourceAsync(Option(Monday));

        Assert.True(_vm.IsLoading);
        Assert.True(_vm.IsEmpty);
        Assert.Equal("正在读取日志…", _vm.EmptyText);

        held.SetResult(Result<LogDay>.Success(_archive.Days[Monday]));
        await loading;
        Assert.Equal(["mon-info", "mon-warn"], Shown());
    }

    [Fact]
    public async Task SwitchingDuringLoad_OnlyTheLastSelectionWins()
    {
        var held = _archive.Hold(Monday);
        var first = _vm.SelectSourceAsync(Option(Monday));
        var firstToken = _archive.LastReadToken;

        await _vm.SelectSourceAsync(Option(Tuesday));
        held.SetResult(Result<LogDay>.Success(_archive.Days[Monday]));
        await first;

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(Tuesday, _vm.SelectedSource.Date);
        Assert.Equal(["tue-error"], Shown());
    }

    [Fact]
    public void Dispose_CancelsPendingLoad()
    {
        _archive.Hold(Monday);
        _ = _vm.SelectSourceAsync(Option(Monday));

        _vm.Dispose();

        Assert.True(_archive.LastReadToken.IsCancellationRequested);
    }

    // ==================== 导出 ====================

    [Fact]
    public async Task Export_UsesSelectedDate_AndReportsResult()
    {
        await _vm.SelectSourceAsync(Option(Tuesday));
        using var destination = new MemoryStream();

        Assert.Equal("2026_11_17.log", _vm.ExportFileName);
        await _vm.ExportAsync(destination, "2026_11_17.log");

        Assert.Equal([Tuesday], _archive.Exported);
        Assert.Equal("已导出 2026-11-17 的日志：2026_11_17.log", _vm.StatusText);
        Assert.True(_vm.CanExport);
    }

    [Fact]
    public async Task Export_InLiveMode_ExportsToday()
    {
        await _vm.ExportAsync(new MemoryStream(), "today.log");

        Assert.Equal([DateOnly.FromDateTime(DateTime.Now)], _archive.Exported);
    }

    [Fact]
    public async Task ExportFailure_IsShownAsStatus()
    {
        _archive.ExportError = new Error("Log.ExportFailed", "磁盘已满");

        await _vm.ExportAsync(new MemoryStream(), "x.log");
        Assert.Equal("导出失败：磁盘已满", _vm.StatusText);

        _vm.ReportExportFailure("U 盘被拔出");
        Assert.Equal("导出失败：U 盘被拔出", _vm.StatusText);
    }
}
