using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.UI.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 设置页：接入日志设置、整页保存、会删除日志时的确认，以及保存后同步到日志页的「打开日志目录」
/// </summary>
public sealed class SettingsViewModelTests
{
    private const string Current = @"C:\Solve\logs";
    private const string Default = @"C:\Solve\logs";
    private const string Other = @"D:\SolveLogs";

    private static readonly LogPrunePreview TwelveFiles =
        new(12, 356L * 1024 * 1024, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 18));

    private static LogRetentionPolicy Policy(int days, int megabytes = LogRetentionPolicy.DefaultFileSizeLimitMegabytes)
        => LogRetentionPolicy.Create(days, megabytes).Value;

    private static (SettingsViewModel Vm, FakeLogSettings Settings, List<string> Changed) Attached(string? startupFallbackReason = null)
    {
        var changed = new List<string>();
        var vm = new SettingsViewModel(changed.Add);
        var settings = new FakeLogSettings(Current, Default, startupFallbackReason);
        vm.AttachLogSettings(settings);
        return (vm, settings, changed);
    }

    // ==================== 接入 ====================

    [Fact]
    public void Designer_CannotSave()
    {
        var vm = new SettingsViewModel();

        Assert.False(vm.IsLogSettingsAvailable);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(SettingsSaveState.Idle, vm.State);
    }

    [Fact]
    public void Attach_ShowsCurrentSettings_AndStartupFallback()
    {
        var vm = new SettingsViewModel();
        var settings = new FakeLogSettings(Current, Default, startupFallbackReason: "保存的保存天数 0 超出范围") { Retention = Policy(90, 100) };

        vm.AttachLogSettings(settings);

        Assert.True(vm.IsLogSettingsAvailable);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Current, vm.LogDirectoryInput);
        Assert.Equal(90m, vm.RetentionDaysInput);
        Assert.Equal(100m, vm.FileSizeLimitMegabytesInput);
        Assert.True(vm.HasStartupFallback);
        Assert.Throws<InvalidOperationException>(() => vm.AttachLogSettings(new FakeLogSettings(Current, Default)));
    }

    [Fact]
    public void InputRanges_MatchThePolicy()
    {
        var vm = new SettingsViewModel();

        Assert.Equal(1m, vm.MinRetentionDays);
        Assert.Equal(365m, vm.MaxRetentionDays);
        Assert.Equal(10m, vm.MinFileSizeLimitMegabytes);
        Assert.Equal(1024m, vm.MaxFileSizeLimitMegabytes);
    }

    // ==================== 保存 ====================

    [Fact]
    public async Task Save_NothingToDelete_SavesDirectlyWithAllSettings_AndNotifiesHost()
    {
        var (vm, settings, changed) = Attached();

        vm.LogDirectoryInput = "  " + Other + "  ";
        vm.RetentionDaysInput = 90;
        vm.FileSizeLimitMegabytesInput = 200;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal([(Other, Policy(90, 200))], settings.Previews);
        Assert.Equal([(Other, Policy(90, 200))], settings.Saves);
        Assert.Equal(Other, vm.CurrentLogDirectory);
        Assert.Equal(Other, vm.LogDirectoryInput);
        Assert.False(vm.IsSaveError);
        Assert.Equal("设置已保存", vm.SaveMessage);
        Assert.Equal([Other], changed);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_Unchanged_StillSaves_AndClearsStartupFallback()
    {
        var (vm, settings, _) = Attached(startupFallbackReason: "保存的日志目录不可用");

        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal([(Current, LogRetentionPolicy.Default)], settings.Saves);
        Assert.False(vm.HasStartupFallback);
    }

    [Fact]
    public async Task Save_Failure_ShowsError_AndKeepsCurrentSettings()
    {
        var (vm, settings, changed) = Attached(startupFallbackReason: "保存的日志目录不可用");
        settings.NextResult = Result<LogSettingsSaved>.Failure(LogDirectoryErrors.NotWritable(Other, "拒绝访问"));

        vm.LogDirectoryInput = Other;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal(Current, vm.CurrentLogDirectory);
        Assert.Equal(Other, vm.LogDirectoryInput);
        Assert.True(vm.IsSaveError);
        Assert.Contains("拒绝访问", vm.SaveMessage);
        Assert.True(vm.HasStartupFallback);
        Assert.Empty(changed);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
    }

    [Fact]
    public async Task Save_EmptyDirectory_IsRejected_WithoutCallingService()
    {
        var (vm, settings, _) = Attached();

        vm.LogDirectoryInput = "   ";
        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(settings.Previews);
        Assert.Empty(settings.Saves);
        Assert.True(vm.IsSaveError);
        Assert.Equal(Current, vm.CurrentLogDirectory);
    }

    [Theory]
    [InlineData(null, 50d, "请填写保存天数")]
    [InlineData(0d, 50d, "保存天数须在")]
    [InlineData(366d, 50d, "保存天数须在")]
    [InlineData(7.5d, 50d, "保存天数须在")]
    [InlineData(30d, null, "请填写单个文件上限")]
    [InlineData(30d, 9d, "单个文件上限须在")]
    [InlineData(30d, 99999999999d, "单个文件上限须在")]
    public async Task Save_InvalidRetention_IsRejected_WithoutCallingService(double? days, double? megabytes, string expected)
    {
        var (vm, settings, _) = Attached();

        vm.RetentionDaysInput = (decimal?)days;
        vm.FileSizeLimitMegabytesInput = (decimal?)megabytes;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(settings.Previews);
        Assert.Empty(settings.Saves);
        Assert.True(vm.IsSaveError);
        Assert.Contains(expected, vm.SaveMessage);
    }

    [Fact]
    public async Task PreviewFailure_IsShown_AndNothingIsSaved()
    {
        var (vm, settings, _) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Failure(LogRetentionErrors.PreviewFailed(Current, "拒绝访问"));

        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(settings.Saves);
        Assert.True(vm.IsSaveError);
        Assert.Contains("拒绝访问", vm.SaveMessage);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
    }

    [Fact]
    public async Task PreviewThrowing_IsContained()
    {
        var (vm, settings, _) = Attached();
        settings.PreviewException = new InvalidOperationException("意外");

        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(settings.Saves);
        Assert.True(vm.IsSaveError);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
    }

    // ==================== 会删除日志时的确认 ====================

    [Fact]
    public async Task ShorterRetention_ThatDeletesFiles_WaitsForConfirmation_AndSavesNothingYet()
    {
        var (vm, settings, changed) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Success(TwelveFiles);

        vm.RetentionDaysInput = 7;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal(SettingsSaveState.AwaitingConfirmation, vm.State);
        Assert.True(vm.IsAwaitingConfirmation);
        Assert.Empty(settings.Saves);
        Assert.Empty(changed);
        Assert.Equal("按新的设置，保存后将立即删除 12 个日志文件（2026-10-01 ～ 2026-10-18，共 356 MB），删除后无法恢复。",
            vm.PendingDeletionText);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.ConfirmDeletionCommand.CanExecute(null));
        Assert.True(vm.CancelDeletionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirm_SavesWhatWasPreviewed_AndReportsDeletedCount()
    {
        var (vm, settings, changed) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Success(TwelveFiles);
        vm.RetentionDaysInput = 7;
        await vm.SaveCommand.ExecuteAsync();

        await vm.ConfirmDeletionCommand.ExecuteAsync();

        Assert.Equal([(Current, Policy(7))], settings.Saves);
        Assert.Equal(7m, vm.RetentionDaysInput);
        Assert.Equal("设置已保存，已删除 12 个日志文件", vm.SaveMessage);
        Assert.Equal([Current], changed);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
        Assert.Null(vm.PendingDeletionText);
    }

    [Fact]
    public async Task Cancel_SavesNothing()
    {
        var (vm, settings, changed) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Success(TwelveFiles);
        vm.RetentionDaysInput = 7;
        await vm.SaveCommand.ExecuteAsync();

        vm.CancelDeletionCommand.Execute(null);

        Assert.Empty(settings.Saves);
        Assert.Empty(changed);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
        Assert.Equal("已取消，设置未保存", vm.SaveMessage);
        Assert.False(vm.ConfirmDeletionCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditingInputWhileAwaitingConfirmation_CancelsIt()
    {
        var (vm, settings, _) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Success(TwelveFiles);
        vm.RetentionDaysInput = 7;
        await vm.SaveCommand.ExecuteAsync();

        vm.RetentionDaysInput = 5;
        await vm.ConfirmDeletionCommand.ExecuteAsync();

        Assert.Empty(settings.Saves);
        Assert.Equal(SettingsSaveState.Idle, vm.State);
        Assert.Equal("设置有改动，请重新保存", vm.SaveMessage);
    }

    [Fact]
    public async Task SingleDayDeletion_ShowsOneDate()
    {
        var (vm, settings, _) = Attached();
        settings.NextPreview = Result<LogPrunePreview>.Success(new LogPrunePreview(1, 2048, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1)));

        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal("按新的设置，保存后将立即删除 1 个日志文件（2026-10-01，共 2 KB），删除后无法恢复。", vm.PendingDeletionText);
    }

    // ==================== 与主窗口联动 ====================

    [Fact]
    public async Task MainViewModel_Save_UpdatesBothLogViews()
    {
        using var main = new MainViewModel();
        main.AttachLogFeed(new FakeLogFeed([new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Solve.Test", "info", null)]), Current);
        main.AttachLogSettings(new FakeLogSettings(Current, Default));

        main.Settings.LogDirectoryInput = Other;
        await main.Settings.SaveCommand.ExecuteAsync();

        Assert.Equal(Other, main.RuntimeLog.LogDirectory);
        Assert.Equal(Other, main.MasterConsole.RecentLog.LogDirectory);
    }
}
