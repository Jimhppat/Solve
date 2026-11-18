using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 设置页的日志目录：接入、应用、恢复默认，以及切换后同步到日志页的「打开日志目录」
/// </summary>
public sealed class SettingsViewModelTests
{
    private const string Current = @"C:\Solve\logs";
    private const string Default = @"C:\Solve\logs";
    private const string Other = @"D:\SolveLogs";

    [Fact]
    public void Designer_CannotChangeDirectory()
    {
        var vm = new SettingsViewModel();

        Assert.False(vm.IsLogDirectoryAvailable);
        Assert.False(vm.ApplyLogDirectoryCommand.CanExecute(null));
        Assert.False(vm.RestoreDefaultLogDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public void Attach_ShowsCurrentDirectory_AndStartupFallback()
    {
        var vm = new SettingsViewModel();

        vm.AttachLogDirectory(new FakeLogDirectorySettings(Current, Default, startupFallbackReason: "保存的日志目录不可用"));

        Assert.True(vm.IsLogDirectoryAvailable);
        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Current, vm.LogDirectoryInput);
        Assert.Equal(Default, vm.DefaultLogDirectory);
        Assert.True(vm.HasLogDirectoryStartupFallback);
        Assert.Throws<InvalidOperationException>(() => vm.AttachLogDirectory(new FakeLogDirectorySettings(Current, Default)));
    }

    [Fact]
    public void Apply_RequiresInput()
    {
        var vm = new SettingsViewModel();
        vm.AttachLogDirectory(new FakeLogDirectorySettings(Current, Default));

        vm.LogDirectoryInput = "   ";

        Assert.False(vm.ApplyLogDirectoryCommand.CanExecute(null));
        Assert.True(vm.RestoreDefaultLogDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_Success_UpdatesCurrentDirectory_AndNotifiesHost()
    {
        var changed = new List<string>();
        var vm = new SettingsViewModel(changed.Add);
        var settings = new FakeLogDirectorySettings(Current, Default);
        vm.AttachLogDirectory(settings);

        vm.LogDirectoryInput = Other;
        await vm.ApplyLogDirectoryCommand.ExecuteAsync();

        Assert.Equal([Other], settings.Requests);
        Assert.Equal(Other, vm.CurrentLogDirectory);
        Assert.False(vm.IsLogDirectoryError);
        Assert.True(vm.HasLogDirectoryMessage);
        Assert.Equal([Other], changed);
        Assert.True(vm.ApplyLogDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_Failure_ShowsError_AndKeepsCurrentDirectory()
    {
        var changed = new List<string>();
        var vm = new SettingsViewModel(changed.Add);
        var settings = new FakeLogDirectorySettings(Current, Default)
        {
            NextResult = Result<string>.Failure(LogDirectoryErrors.NotWritable(Other, "拒绝访问")),
        };
        vm.AttachLogDirectory(settings);

        vm.LogDirectoryInput = Other;
        await vm.ApplyLogDirectoryCommand.ExecuteAsync();

        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Other, vm.LogDirectoryInput);
        Assert.True(vm.IsLogDirectoryError);
        Assert.Contains("拒绝访问", vm.LogDirectoryMessage);
        Assert.Empty(changed);
    }

    [Fact]
    public async Task RestoreDefault_RequestsDefault()
    {
        var vm = new SettingsViewModel();
        var settings = new FakeLogDirectorySettings(Other, Default);
        vm.AttachLogDirectory(settings);

        await vm.RestoreDefaultLogDirectoryCommand.ExecuteAsync();

        Assert.Equal([null], settings.Requests);
        Assert.Equal(Default, vm.CurrentLogDirectory);
        Assert.Equal(Default, vm.LogDirectoryInput);
    }

    [Fact]
    public async Task MainViewModel_DirectoryChange_UpdatesBothLogViews()
    {
        using var main = new MainViewModel();
        main.AttachLogFeed(new FakeLogFeed([new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Solve.Test", "info", null)]), Current);
        main.AttachLogDirectorySettings(new FakeLogDirectorySettings(Current, Default));

        main.Settings.LogDirectoryInput = Other;
        await main.Settings.ApplyLogDirectoryCommand.ExecuteAsync();

        Assert.Equal(Other, main.RuntimeLog.LogDirectory);
        Assert.Equal(Other, main.MasterConsole.RecentLog.LogDirectory);
    }
}
