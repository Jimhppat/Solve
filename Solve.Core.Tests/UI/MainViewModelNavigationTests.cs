using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主窗口侧栏导航：「系统设置」切换到设置页
/// </summary>
public sealed class MainViewModelNavigationTests : IDisposable
{
    private readonly MainViewModel _vm = new();

    public void Dispose() => _vm.Dispose();

    [Fact]
    public void SelectSettings_NavigatesToSettingsPage()
    {
        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        Assert.Equal(MainViewModel.SettingsNavIndex, _vm.SelectedNavIndex);
        Assert.Same(_vm.Settings, _vm.CurrentPage);
    }

    [Fact]
    public void LeaveSettings_ReturnsToSelectedPage()
    {
        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        _vm.SelectedNavIndex = MainViewModel.MasterConsoleNavIndex;

        Assert.Same(_vm.MasterConsole, _vm.CurrentPage);
    }
}
