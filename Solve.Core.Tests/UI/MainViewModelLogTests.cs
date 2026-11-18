using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主窗口宿主与日志相关的接线：两处日志列表的接入、主控台「全部」跳转到运行日志页
/// </summary>
public sealed class MainViewModelLogTests : IDisposable
{
    private readonly MainViewModel _vm = new();

    public void Dispose() => _vm.Dispose();

    private static LogEntry Entry(LogLevel level, string message)
        => new(DateTimeOffset.Now, level, "Solve.Test", message, null);

    [Fact]
    public void AttachLogFeed_SubscribesBothLists_WithTheirOwnLevel()
    {
        var feed = new FakeLogFeed([Entry(LogLevel.Debug, "debug"), Entry(LogLevel.Information, "info")]);

        _vm.AttachLogFeed(feed, @"C:\logs");

        Assert.Equal(2, feed.SubscriberCount);
        Assert.Equal(["debug", "info"], _vm.RuntimeLog.Entries.Select(e => e.Message));
        // 主控台只显示信息及以上
        Assert.Equal(["info"], _vm.MasterConsole.RecentLog.Entries.Select(e => e.Message));
        Assert.Equal(MasterConsoleViewModel.RecentLogCapacity, _vm.MasterConsole.RecentLog.Capacity);
    }

    [Fact]
    public void RecentLogFilters_AreIndependentOfRuntimeLogPage()
    {
        _vm.AttachLogFeed(new FakeLogFeed([Entry(LogLevel.Information, "info")]), @"C:\logs");

        _vm.RuntimeLog.SearchText = "nothing-matches";
        _vm.RuntimeLog.ClearCommand.Execute(null);

        Assert.Equal(["info"], _vm.MasterConsole.RecentLog.Entries.Select(e => e.Message));
    }

    [Fact]
    public void OpenRuntimeLogCommand_NavigatesToRuntimeLogPage()
    {
        Assert.Same(_vm.MasterConsole, _vm.CurrentPage);

        Assert.True(_vm.MasterConsole.OpenRuntimeLogCommand.CanExecute(null));
        _vm.MasterConsole.OpenRuntimeLogCommand.Execute(null);

        Assert.Equal(MainViewModel.RuntimeLogNavIndex, _vm.SelectedNavIndex);
        Assert.Same(_vm.RuntimeLog, _vm.CurrentPage);
    }

    [Fact]
    public void DesignerMasterConsole_CannotNavigate()
    {
        using var designer = new MasterConsoleViewModel();

        Assert.False(designer.OpenRuntimeLogCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_UnsubscribesBothLists()
    {
        var feed = new FakeLogFeed([]);
        _vm.AttachLogFeed(feed, @"C:\logs");

        _vm.Dispose();

        Assert.Equal(0, feed.SubscriberCount);
    }
}
