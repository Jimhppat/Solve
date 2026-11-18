using Microsoft.Extensions.Logging;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;
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

    private LogLevelOption Level(LogLevel minimum) => _vm.LevelOptions.Single(o => o.Minimum == minimum);

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
    public void LevelFilter_HidesLowerLevels_AndAppliesToNewEntries()
    {
        _vm.Append([Entry(LogLevel.Debug, "debug"), Entry(LogLevel.Warning, "warn")]);

        _vm.SelectedLevel = Level(LogLevel.Warning);
        _vm.Append([Entry(LogLevel.Information, "info"), Entry(LogLevel.Error, "error")]);

        Assert.Equal(["warn", "error"], Shown());
        Assert.Equal(4, _vm.TotalCount);
        Assert.EndsWith("筛选出 2 条", _vm.SummaryText);
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
        _vm.Append(Enumerable.Range(0, RuntimeLogViewModel.Capacity + 10).Select(_ => Entry()).ToList());

        Assert.Equal(RuntimeLogViewModel.Capacity, _vm.TotalCount);
        Assert.Equal(RuntimeLogViewModel.Capacity, _vm.Entries.Count);
        Assert.Equal("m11", _vm.Entries[0].Message);
    }

    [Fact]
    public void Capacity_WithFilter_KeepsShownEntriesConsistent()
    {
        _vm.SelectedLevel = Level(LogLevel.Warning);

        // 分多批到达：第一批全部是告警，之后用普通日志把它们挤出容量
        _vm.Append(Enumerable.Range(0, 5).Select(_ => Entry(LogLevel.Warning)).ToList());
        for (var i = 0; i < 4; i++)
            _vm.Append(Enumerable.Range(0, RuntimeLogViewModel.Capacity / 4).Select(_ => Entry()).ToList());
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
        _vm.SelectedLevel = Level(LogLevel.Error);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Attach_ShowsRecentSnapshot_AndRejectsSecondAttach()
    {
        var feed = new FakeFeed([Entry(message: "startup-1"), Entry(message: "startup-2")]);

        _vm.Attach(feed, @"C:\logs");

        Assert.Equal(["startup-1", "startup-2"], Shown());
        Assert.True(_vm.HasLogDirectory);
        Assert.Throws<InvalidOperationException>(() => _vm.Attach(feed, @"C:\logs"));
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var feed = new FakeFeed([]);
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

    private sealed class FakeFeed(IReadOnlyList<LogEntry> recent) : ILogFeed
    {
        private readonly EventBroadcaster<LogEntry> _broadcaster = new(16);

        public int SubscriberCount => _broadcaster.SubscriberCount;

        public EventSubscription<LogEntry> Subscribe(out IReadOnlyList<LogEntry> snapshot)
        {
            snapshot = recent;
            return _broadcaster.Subscribe();
        }
    }
}
