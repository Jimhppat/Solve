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

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    private static LogSourceOption Option(DateOnly date) => LogSourceOption.ForDate(date, Today());

    private LogCalendarDay CalendarDay(DateOnly date)
    {
        _vm.Calendar.ShowMonthOf(date);
        return _vm.Calendar.Days.Single(d => d.IsInMonth && d.Date == date);
    }

    // ==================== 月历中的可选日期 ====================

    [Fact]
    public void Attach_ListsAvailableDates_DaysWithoutLogsAreGray()
    {
        Assert.True(_vm.CanBrowseHistory);
        Assert.Equal("实时", _vm.SelectedSource.Label);

        Assert.True(CalendarDay(Monday).HasLog);
        Assert.True(CalendarDay(Tuesday).CanSelect);

        var sunday = CalendarDay(new DateOnly(2026, 11, 15));
        Assert.False(sunday.HasLog);
        Assert.False(sunday.CanSelect);
    }

    [Fact]
    public async Task SelectingToday_LabelsItAsToday()
    {
        var today = Today();
        _archive.AddDay(today, [Entry("today")]);
        await _vm.RefreshAvailableDatesAsync();

        await _vm.SelectDateAsync(today);

        Assert.Equal($"{today:yyyy-MM-dd}（今天）", _vm.SelectedSource.Label);
        Assert.True(CalendarDay(today).IsToday);
        Assert.True(CalendarDay(today).IsSelected);
    }

    [Fact]
    public async Task SelectDate_WithLog_ShowsThatDay_AndMarksItInTheCalendar()
    {
        await _vm.SelectDateAsync(Monday);

        Assert.Equal(Monday, _vm.SelectedSource.Date);
        Assert.Equal("2026-11-16", _vm.SelectedSource.Label);
        Assert.Equal(["mon-info", "mon-warn"], Shown());
        Assert.True(CalendarDay(Monday).IsSelected);
        Assert.False(CalendarDay(Tuesday).IsSelected);
    }

    [Fact]
    public async Task SelectDate_WithoutLog_IsRejected_AndNothingIsRead()
    {
        _vm.Append([Entry("live-1")]);

        await _vm.SelectDateAsync(new DateOnly(2026, 11, 15));

        Assert.False(_vm.CanSelectDate(new DateOnly(2026, 11, 15)));
        Assert.True(_vm.IsLive);
        Assert.Null(_vm.StatusText);
        Assert.Equal(CancellationToken.None, _archive.LastReadToken);
        Assert.Equal(["live-1"], Shown());
    }

    [Fact]
    public async Task SelectDate_SameDayAgain_DoesNotReload()
    {
        await _vm.SelectDateAsync(Monday);
        var firstRead = _archive.LastReadToken;

        await _vm.SelectDateAsync(Monday);

        // 重新读取会换一个新的取消令牌
        Assert.Equal(firstRead, _archive.LastReadToken);
        Assert.False(_vm.IsLoading);
        Assert.Equal(["mon-info", "mon-warn"], Shown());
    }

    [Fact]
    public async Task Refresh_KeepsViewedDate_EvenIfItWasJustDeleted()
    {
        await _vm.SelectDateAsync(Monday);
        _archive.Days.Remove(Monday);
        _archive.AddDay(new DateOnly(2026, 11, 18), [Entry("new-day")]);

        await _vm.RefreshAvailableDatesAsync();

        Assert.Equal(Monday, _vm.SelectedSource.Date);
        Assert.Equal(["mon-info", "mon-warn"], Shown());

        // 已被清理但仍是正在查看的那天：保持选中、可点（不会重新读取），提示文件已不存在
        var monday = CalendarDay(Monday);
        Assert.False(monday.HasLog);
        Assert.True(monday.IsSelected);
        Assert.True(monday.CanSelect);
        Assert.Contains("已被清理", monday.ToolTip);
        Assert.True(CalendarDay(new DateOnly(2026, 11, 18)).HasLog);
    }

    [Fact]
    public async Task OpenDatePicker_ShowsMonthOfViewedDate_OrThisMonthWhenLive()
    {
        _archive.AddDay(new DateOnly(2026, 10, 20), [Entry("october")]);
        await _vm.RefreshAvailableDatesAsync();

        await _vm.SelectDateAsync(new DateOnly(2026, 10, 20));
        _vm.Calendar.ShowMonth(2026, 11);
        await _vm.OpenDatePickerAsync();
        Assert.Equal((2026, 10), (_vm.Calendar.DisplayYear, _vm.Calendar.DisplayMonth));

        await _vm.ShowLiveAsync();
        await _vm.OpenDatePickerAsync();
        Assert.Equal((Today().Year, Today().Month), (_vm.Calendar.DisplayYear, _vm.Calendar.DisplayMonth));
    }

    [Fact]
    public async Task OpenDatePicker_RelistsDates()
    {
        var added = new DateOnly(2026, 11, 10);
        Assert.False(_vm.CanSelectDate(added));

        _archive.AddDay(added, [Entry("added")]);
        await _vm.OpenDatePickerAsync();

        Assert.True(_vm.CanSelectDate(added));
    }

    [Fact]
    public async Task ShowToday_OnlyWhenTodayHasALogFile()
    {
        await _vm.ShowTodayAsync();
        Assert.False(_vm.Calendar.TodayHasLog);
        Assert.True(_vm.IsLive);

        _archive.AddDay(Today(), [Entry("today-file")]);
        await _vm.RefreshAvailableDatesAsync();
        await _vm.ShowTodayAsync();

        Assert.True(_vm.Calendar.TodayHasLog);
        Assert.Equal(Today(), _vm.SelectedSource.Date);
        Assert.Equal(["today-file"], Shown());
    }

    [Fact]
    public async Task ShowLive_WhenAlreadyLive_KeepsStatus()
    {
        using var destination = new MemoryStream();
        await _vm.ExportAsync(destination, "x.log");
        var status = _vm.StatusText;

        await _vm.ShowLiveAsync();

        Assert.NotNull(status);
        Assert.Equal(status, _vm.StatusText);
    }

    [Fact]
    public void WithoutArchive_HistoryIsUnavailable()
    {
        using var recent = new RuntimeLogViewModel(100, LogCategories.Operational);
        recent.Attach(new FakeLogFeed([]), @"C:\logs");

        Assert.False(recent.CanBrowseHistory);
        Assert.False(recent.CanExport);
        Assert.False(recent.CanSelectDate(Monday));
        Assert.True(recent.IsLive);
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

    // ==================== 设置页切换日志目录 ====================

    [Fact]
    public async Task DirectoryChange_WhileViewingHistory_ReturnsToLive_AndRelistsDates()
    {
        await _vm.SelectSourceAsync(Option(Monday));

        // 新目录里只有另一天
        _archive.Days.Clear();
        _archive.AddDay(new DateOnly(2026, 11, 18), [Entry("new-dir")]);
        _vm.UpdateLogDirectory(@"D:\new-logs");

        Assert.True(_vm.IsLive);
        Assert.Equal(@"D:\new-logs", _vm.LogDirectory);
        Assert.Equal("日志目录已切换，按日期查看改为读取新目录", _vm.StatusText);
        Assert.False(_vm.CanSelectDate(Monday));
        Assert.True(_vm.CanSelectDate(new DateOnly(2026, 11, 18)));
        Assert.False(CalendarDay(Monday).IsSelected);
    }

    [Fact]
    public void DirectoryChange_ToTheSameDirectory_ChangesNothing()
    {
        _vm.UpdateLogDirectory(@"C:\logs");

        Assert.Null(_vm.StatusText);
    }

    [Fact]
    public void DirectoryChange_WithoutArchive_OnlyUpdatesOpenFolderTarget()
    {
        using var recent = new RuntimeLogViewModel(100, LogCategories.Operational);
        recent.Attach(new FakeLogFeed([]), @"C:\logs");

        recent.UpdateLogDirectory(@"D:\new-logs");

        Assert.Equal(@"D:\new-logs", recent.LogDirectory);
        Assert.Null(recent.StatusText);
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
