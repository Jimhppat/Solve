using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 设置页：接入日志存储路径、整页保存，以及保存后同步到日志页的「打开日志目录」
/// </summary>
public sealed class SettingsViewModelTests
{
    private const string Current = @"C:\Solve\logs";
    private const string Default = @"C:\Solve\logs";
    private const string Other = @"D:\SolveLogs";

    [Fact]
    public void Designer_CannotSave()
    {
        var vm = new SettingsViewModel();

        Assert.False(vm.IsLogDirectoryAvailable);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Attach_ShowsCurrentDirectory_AndStartupFallback()
    {
        var vm = new SettingsViewModel();

        vm.AttachLogDirectory(new FakeLogDirectorySettings(Current, Default, startupFallbackReason: "保存的日志目录不可用"));

        Assert.True(vm.IsLogDirectoryAvailable);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Current, vm.LogDirectoryInput);
        Assert.True(vm.HasLogDirectoryStartupFallback);
        Assert.Throws<InvalidOperationException>(() => vm.AttachLogDirectory(new FakeLogDirectorySettings(Current, Default)));
    }

    [Fact]
    public async Task Save_Success_AppliesInput_AndNotifiesHost()
    {
        var changed = new List<string>();
        var vm = new SettingsViewModel(changed.Add);
        var settings = new FakeLogDirectorySettings(Current, Default);
        vm.AttachLogDirectory(settings);

        vm.LogDirectoryInput = "  " + Other + "  ";
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal([Other], settings.Requests);
        Assert.Equal(Other, vm.CurrentLogDirectory);
        Assert.Equal(Other, vm.LogDirectoryInput);
        Assert.False(vm.IsSaveError);
        Assert.Equal("设置已保存", vm.SaveMessage);
        Assert.Equal([Other], changed);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_Unchanged_StillSaves_AndClearsStartupFallback()
    {
        var vm = new SettingsViewModel();
        var settings = new FakeLogDirectorySettings(Default, Default, startupFallbackReason: "保存的日志目录不可用");
        vm.AttachLogDirectory(settings);

        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal([Default], settings.Requests);
        Assert.False(vm.HasLogDirectoryStartupFallback);
    }

    [Fact]
    public async Task Save_Failure_ShowsError_AndKeepsCurrentDirectory()
    {
        var changed = new List<string>();
        var vm = new SettingsViewModel(changed.Add);
        var settings = new FakeLogDirectorySettings(Current, Default, startupFallbackReason: "保存的日志目录不可用")
        {
            NextResult = Result<string>.Failure(LogDirectoryErrors.NotWritable(Other, "拒绝访问")),
        };
        vm.AttachLogDirectory(settings);

        vm.LogDirectoryInput = Other;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Other, vm.LogDirectoryInput);
        Assert.True(vm.IsSaveError);
        Assert.Contains("拒绝访问", vm.SaveMessage);
        Assert.True(vm.HasLogDirectoryStartupFallback);
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Save_EmptyInput_IsRejected_WithoutCallingService()
    {
        var vm = new SettingsViewModel();
        var settings = new FakeLogDirectorySettings(Current, Default);
        vm.AttachLogDirectory(settings);

        vm.LogDirectoryInput = "   ";
        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(settings.Requests);
        Assert.True(vm.IsSaveError);
        Assert.Equal(Current, vm.CurrentLogDirectory);
    }

    [Fact]
    public async Task MainViewModel_Save_UpdatesBothLogViews()
    {
        using var main = new MainViewModel();
        main.AttachLogFeed(new FakeLogFeed([new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Solve.Test", "info", null)]), Current);
        main.AttachLogDirectorySettings(new FakeLogDirectorySettings(Current, Default));

        main.Settings.LogDirectoryInput = Other;
        await main.Settings.SaveCommand.ExecuteAsync();

        Assert.Equal(Other, main.RuntimeLog.LogDirectory);
        Assert.Equal(Other, main.MasterConsole.RecentLog.LogDirectory);
    }
}
