using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 运行日志页视图模型。直接调用 Append 模拟一批批到达的日志，不依赖 UI 线程。
/// </summary>
public sealed class RuntimeLogViewModelTests : IDisposable
{
    private readonly RuntimeLogViewModel _vm = new();
    private int _sequence;

    public void Dispose() => _vm.Dispose();

    private LogEntry Entry(LogLevel level = LogLevel.Information, string? message = null, string source = "Solve.Core.Application.Buses.BusRuntime")
        => new(DateTimeOffset.Now, level, source, message ?? $"m{++_sequence}", null);

    private IReadOnlyList<string> Shown() => _vm.Entries.Select(e => e.Message).ToList();

    private void Show(params LogCategory[] categories) => _vm.SetShownCategories(categories);

    private LogLevelFilterOption Option(LogCategory category) => _vm.LevelFilters.Single(o => o.Category == category);

    [Fact]
    public void Append_ShowsEntriesInOrder_AndCountsWarningsAndErrors()
    {
        _vm.Append([
            Entry(message: "a"),
            Entry(LogLevel.Warning, "b"),
            Entry(LogLevel.Error, "c"),
            Entry(LogLevel.Critical, "d"),
        ]);

        Assert.Equal(["a", "b", "c", "d"], Shown());
        Assert.Equal(4, _vm.TotalCount);
        Assert.Equal(1, _vm.WarningCount);
        Assert.Equal(2, _vm.ErrorCount);
        Assert.Equal("共 4 条 · 警告 1 · 错误 2", _vm.SummaryText);
    }

    [Fact]
    public void ProblemsOnly_ShowsWarningErrorCritical_AndAppliesToNewEntries()
    {
        _vm.Append([Entry(LogLevel.Debug, "debug"), Entry(LogLevel.Warning, "warn")]);

        _vm.ShowProblemsOnlyCommand.Execute(null);
        _vm.Append([Entry(LogLevel.Information, "info"), Entry(LogLevel.Error, "error"), Entry(LogLevel.Critical, "fatal")]);

        Assert.Equal(["warn", "error", "fatal"], Shown());
        Assert.Equal(5, _vm.TotalCount);
        Assert.EndsWith("筛选出 3 条", _vm.SummaryText);
        Assert.Equal("警告+", _vm.LevelFilterText);
    }

    // ==================== 五类级别、多选 ====================

    [Fact]
    public void LevelFilters_AreFiveCategories_FromDebugToCritical()
    {
        Assert.Equal(["调试", "信息", "警告", "错误", "致命"], _vm.LevelFilters.Select(o => o.Label));
        Assert.All(_vm.LevelFilters, o => Assert.True(o.IsSelected));
        Assert.Equal("全部", _vm.LevelFilterText);
    }

    [Fact]
    public void Trace_IsShownAndFilteredAsDebug()
    {
        _vm.Append([Entry(LogLevel.Trace, "trace"), Entry(LogLevel.Debug, "debug"), Entry(message: "info")]);

        Show(LogCategory.Debug);

        Assert.Equal(["trace", "debug"], Shown());
        Assert.All(_vm.Entries, e => Assert.Equal("调试", e.LevelText));
        Assert.Equal(2, Option(LogCategory.Debug).Count);
    }

    [Theory]
    [InlineData(LogCategory.Debug, "debug")]
    [InlineData(LogCategory.Information, "info")]
    [InlineData(LogCategory.Warning, "warn")]
    [InlineData(LogCategory.Error, "error")]
    [InlineData(LogCategory.Critical, "fatal")]
    public void SingleCategory_ShowsOnlyThatLevel_NotHigherOnes(LogCategory category, string expected)
    {
        _vm.Append([
            Entry(LogLevel.Debug, "debug"),
            Entry(LogLevel.Information, "info"),
            Entry(LogLevel.Warning, "warn"),
            Entry(LogLevel.Error, "error"),
            Entry(LogLevel.Critical, "fatal"),
        ]);

        Show(category);

        Assert.Equal([expected], Shown());
        Assert.Equal(category.DisplayName(), _vm.LevelFilterText);
    }

    [Fact]
    public void UncheckingAnOption_RefiltersImmediately()
    {
        _vm.Append([Entry(LogLevel.Warning, "warn"), Entry(LogLevel.Error, "error")]);

        Option(LogCategory.Warning).IsSelected = false;

        Assert.Equal(["error"], Shown());
        Assert.Equal("4 级", _vm.LevelFilterText);
        Assert.Equal("显示：调试、信息、错误、致命", _vm.LevelFilterDetail);
    }

    [Theory]
    [InlineData(new[] { LogCategory.Information, LogCategory.Warning, LogCategory.Error, LogCategory.Critical }, "信息+")]
    [InlineData(new[] { LogCategory.Error, LogCategory.Critical }, "错误+")]
    [InlineData(new[] { LogCategory.Debug, LogCategory.Warning }, "2 级")]
    [InlineData(new[] { LogCategory.Warning, LogCategory.Error }, "2 级")]
    [InlineData(new LogCategory[0], "无")]
    public void LevelFilterText_SummarizesSelection(LogCategory[] categories, string expected)
    {
        Show(categories);

        Assert.Equal(expected, _vm.LevelFilterText);
    }

    [Fact]
    public void NothingSelected_ShowsExplicitHint()
    {
        _vm.Append([Entry()]);

        Show();

        Assert.True(_vm.IsEmpty);
        Assert.Equal("未选择任何级别", _vm.EmptyText);
        Assert.Equal("未选择任何级别", _vm.LevelFilterDetail);
    }

    [Fact]
    public void Presets_UpdateCheckboxes_AndRefilterOnce()
    {
        var raised = 0;
        _vm.EntriesChanged += (_, _) => raised++;

        _vm.ShowProblemsOnlyCommand.Execute(null);

        Assert.Equal(1, raised);
        Assert.Equal([false, false, true, true, true], _vm.LevelFilters.Select(o => o.IsSelected));

        _vm.ShowAllLevelsCommand.Execute(null);
        Assert.All(_vm.LevelFilters, o => Assert.True(o.IsSelected));
    }

    [Fact]
    public void CategoryCounts_FollowAppendTrimAndClear()
    {
        using var small = new RuntimeLogViewModel(capacity: 3, LogCategories.All);

        small.Append([Entry(LogLevel.Warning), Entry(LogLevel.Critical), Entry(), Entry()]);

        Assert.Equal(0, small.LevelFilters.Single(o => o.Category == LogCategory.Warning).Count);
        Assert.Equal(1, small.LevelFilters.Single(o => o.Category == LogCategory.Critical).Count);
        Assert.Equal(2, small.LevelFilters.Single(o => o.Category == LogCategory.Information).Count);

        small.ClearCommand.Execute(null);
        Assert.All(small.LevelFilters, o => Assert.Equal(0, o.Count));
    }

    [Fact]
    public void CriticalItem_IsAlsoAnError()
    {
        var item = new LogEntryItem(new LogEntry(DateTimeOffset.Now, LogLevel.Critical, "", "m", null));

        Assert.Equal("致命", item.LevelText);
        Assert.True(item.IsCritical);
        Assert.True(item.IsError);
    }

    [Fact]
    public void Search_MatchesMessageOrSource_IgnoringCase()
    {
        _vm.Append([
            Entry(message: "串口 COM3 打开失败"),
            Entry(message: "配置库就绪", source: "Solve.Adapters.Persistence.Sqlite.SqliteDatabase"),
            Entry(message: "无关"),
        ]);

        _vm.SearchText = "com3";
        Assert.Equal(["串口 COM3 打开失败"], Shown());

        _vm.SearchText = "  sqlite ";
        Assert.Equal(["配置库就绪"], Shown());

        _vm.SearchText = "";
        Assert.Equal(3, _vm.Entries.Count);
    }

    [Fact]
    public void Capacity_DropsOldest()
    {
        _vm.Append(Enumerable.Range(0, _vm.Capacity + 10).Select(_ => Entry()).ToList());

        Assert.Equal(_vm.Capacity, _vm.TotalCount);
        Assert.Equal(_vm.Capacity, _vm.Entries.Count);
        Assert.Equal("m11", _vm.Entries[0].Message);
    }

    [Fact]
    public void Capacity_WithFilter_KeepsShownEntriesConsistent()
    {
        Show(LogCategory.Warning);

        // 分多批到达：第一批全部是告警，之后用普通日志把它们挤出容量
        _vm.Append(Enumerable.Range(0, 5).Select(_ => Entry(LogLevel.Warning)).ToList());
        for (var i = 0; i < 4; i++)
            _vm.Append(Enumerable.Range(0, _vm.Capacity / 4).Select(_ => Entry()).ToList());
        _vm.Append([Entry(LogLevel.Warning, "latest-warning")]);

        Assert.Equal(["latest-warning"], Shown());
        Assert.Equal(1, _vm.WarningCount);
    }

    [Fact]
    public void Clear_EmptiesView_AndCounters()
    {
        _vm.Append([Entry(LogLevel.Error), Entry(LogLevel.Warning)]);

        _vm.ClearCommand.Execute(null);

        Assert.True(_vm.IsEmpty);
        Assert.Equal(0, _vm.TotalCount);
        Assert.Equal(0, _vm.WarningCount);
        Assert.Equal(0, _vm.ErrorCount);
        Assert.Equal("暂无日志", _vm.EmptyText);
    }

    [Fact]
    public void EmptyText_DistinguishesNoLogsFromNoMatches()
    {
        Assert.Equal("暂无日志", _vm.EmptyText);

        _vm.Append([Entry(message: "a")]);
        _vm.SearchText = "zzz";

        Assert.True(_vm.IsEmpty);
        Assert.Equal("没有符合筛选条件的日志", _vm.EmptyText);
    }

    [Fact]
    public void EntriesChanged_FiresOncePerBatchAndOnRefilter()
    {
        var raised = 0;
        _vm.EntriesChanged += (_, _) => raised++;

        _vm.Append([Entry(), Entry(), Entry()]);
        Show(LogCategory.Error);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Attach_ShowsRecentSnapshot_AndRejectsSecondAttach()
    {
        var feed = new FakeLogFeed([Entry(message: "startup-1"), Entry(message: "startup-2")]);

        _vm.Attach(feed, @"C:\logs");

        Assert.Equal(["startup-1", "startup-2"], Shown());
        Assert.True(_vm.HasLogDirectory);
        Assert.Throws<InvalidOperationException>(() => _vm.Attach(feed, @"C:\logs"));
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var feed = new FakeLogFeed([]);
        _vm.Attach(feed, @"C:\logs");

        _vm.Dispose();

        Assert.Equal(0, feed.SubscriberCount);
    }

    [Fact]
    public void Item_ShortensSource_AndBuildsDetail()
    {
        var item = new LogEntryItem(new LogEntry(DateTimeOffset.Now, LogLevel.Error, "Solve.A.B.TcpTransport", "msg", "stack"));

        Assert.Equal("TcpTransport", item.SourceText);
        Assert.Equal("错误", item.LevelText);
        Assert.True(item.HasException);
        Assert.Contains("Solve.A.B.TcpTransport", item.DetailText);
        Assert.EndsWith($"msg{Environment.NewLine}stack", item.DetailText);
    }

    // ==================== 主控台最近日志所用的配置 ====================

    [Fact]
    public void CustomCapacityAndCategories_AreApplied()
    {
        using var recent = new RuntimeLogViewModel(capacity: 3, LogCategories.Operational);

        recent.Append([Entry(LogLevel.Debug, "debug"), Entry(message: "a"), Entry(message: "b"), Entry(message: "c"), Entry(message: "d")]);

        Assert.Equal(3, recent.Capacity);
        Assert.Equal("信息+", recent.LevelFilterText);
        Assert.False(recent.LevelFilters.Single(o => o.Category == LogCategory.Debug).IsSelected);
        Assert.Equal(["b", "c", "d"], recent.Entries.Select(e => e.Message));
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeLogViewModel(0, LogCategories.All));
        Assert.Throws<ArgumentNullException>(() => new RuntimeLogViewModel(10, null!));
    }

    [Fact]
    public void Item_ShortTime_DropsMilliseconds()
    {
        var item = new LogEntryItem(new LogEntry(new DateTimeOffset(2026, 11, 18, 9, 5, 7, 123, TimeSpan.Zero), LogLevel.Information, "", "m", null));

        Assert.Equal(item.Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"), item.ShortTimeText);
        Assert.EndsWith(".123", item.TimeText);
    }

    // ==================== 自动滚动关闭期间的未读计数 ====================

    [Fact]
    public void WhileFollowing_NothingIsUnseen()
    {
        _vm.Append([Entry(), Entry()]);

        Assert.True(_vm.AutoScroll);
        Assert.Equal(0, _vm.UnseenCount);
        Assert.False(_vm.HasUnseen);
    }

    [Fact]
    public void WhilePaused_CountsOnlyEntriesThatAreShown()
    {
        _vm.Append([Entry()]);
        _vm.AutoScroll = false;
        Show(LogCategory.Warning);

        // 只勾了警告：信息与错误都不显示，不计入未读
        _vm.Append([Entry(LogLevel.Information), Entry(LogLevel.Warning), Entry(LogLevel.Error)]);
        _vm.Append([Entry(LogLevel.Warning)]);

        Assert.Equal(2, _vm.UnseenCount);
        Assert.True(_vm.HasUnseen);
        Assert.Equal("2 条新日志", _vm.UnseenText);
    }

    [Fact]
    public void JumpToLatest_ResumesFollowing_AndClearsUnseen()
    {
        _vm.AutoScroll = false;
        _vm.Append([Entry(), Entry()]);

        _vm.JumpToLatestCommand.Execute(null);

        Assert.True(_vm.AutoScroll);
        Assert.Equal(0, _vm.UnseenCount);
    }

    [Fact]
    public void ReenablingAutoScroll_ClearsUnseen()
    {
        _vm.AutoScroll = false;
        _vm.Append([Entry()]);

        _vm.AutoScroll = true;

        Assert.False(_vm.HasUnseen);
    }

    [Fact]
    public void RefilterOrClear_ResetsUnseen_ButKeepsPaused()
    {
        _vm.AutoScroll = false;
        _vm.Append([Entry(LogLevel.Warning)]);
        Show(LogCategory.Warning);

        Assert.Equal(0, _vm.UnseenCount);
        Assert.False(_vm.AutoScroll);

        _vm.Append([Entry(LogLevel.Warning)]);
        _vm.ClearCommand.Execute(null);

        Assert.Equal(0, _vm.UnseenCount);
        Assert.False(_vm.AutoScroll);
    }

    [Fact]
    public void Unseen_NeverExceedsShownEntries_AndCapsDisplayAt99()
    {
        using var small = new RuntimeLogViewModel(capacity: 3, LogCategories.All) { AutoScroll = false };
        small.Append(Enumerable.Range(0, 5).Select(_ => Entry()).ToList());
        Assert.Equal(3, small.UnseenCount);

        _vm.AutoScroll = false;
        _vm.Append(Enumerable.Range(0, 150).Select(_ => Entry()).ToList());
        Assert.Equal(150, _vm.UnseenCount);
        Assert.Equal("99+ 条新日志", _vm.UnseenText);
    }

    [Fact]
    public void UnseenChanges_AreNotified()
    {
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.AutoScroll = false;

        _vm.Append([Entry()]);
        Assert.Contains(nameof(RuntimeLogViewModel.HasUnseen), changed);

        changed.Clear();
        _vm.JumpToLatestCommand.Execute(null);
        Assert.Contains(nameof(RuntimeLogViewModel.HasUnseen), changed);
        Assert.Contains(nameof(RuntimeLogViewModel.AutoScroll), changed);
    }
}
