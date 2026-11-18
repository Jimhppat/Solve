using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主窗口侧栏导航：「系统设置」只请求打开独立窗口，不切换工作台页面
/// </summary>
public sealed class MainViewModelNavigationTests : IDisposable
{
    private readonly MainViewModel _vm = new();

    public void Dispose() => _vm.Dispose();

    [Fact]
    public void SelectSettings_RaisesWindowRequest_AndKeepsCurrentPage()
    {
        _vm.SelectedNavIndex = MainViewModel.RuntimeLogNavIndex;
        var requests = 0;
        var indexNotifications = 0;
        _vm.SettingsWindowRequested += (_, _) => requests++;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedNavIndex))
                indexNotifications++;
        };

        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        Assert.Equal(1, requests);
        Assert.Equal(0, indexNotifications);
        Assert.Equal(MainViewModel.RuntimeLogNavIndex, _vm.SelectedNavIndex);
        Assert.Same(_vm.RuntimeLog, _vm.CurrentPage);
    }

    [Fact]
    public void SelectSettingsRepeatedly_RaisesRequestEachTime()
    {
        var requests = 0;
        _vm.SettingsWindowRequested += (_, _) => requests++;

        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;
        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        // 每次点击都要能把已打开的设置窗口激活到前台
        Assert.Equal(2, requests);
        Assert.Equal(MainViewModel.MasterConsoleNavIndex, _vm.SelectedNavIndex);
        Assert.Same(_vm.MasterConsole, _vm.CurrentPage);
    }

    [Fact]
    public void SelectSettings_WithoutSubscriber_DoesNotThrow()
    {
        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        Assert.Same(_vm.MasterConsole, _vm.CurrentPage);
    }
}
