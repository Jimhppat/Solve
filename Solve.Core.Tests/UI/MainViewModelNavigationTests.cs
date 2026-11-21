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
    public void SelectTopology_NavigatesToTopologyPage()
    {
        _vm.SelectedNavIndex = MainViewModel.TopologyNavIndex;

        Assert.Same(_vm.Topology, _vm.CurrentPage);
    }

    [Fact]
    public void SelectHardwareMapping_NavigatesToMappingPage_WithoutServices()
    {
        _vm.SelectedNavIndex = MainViewModel.HardwareMappingNavIndex;

        Assert.Same(_vm.HardwareMapping, _vm.CurrentPage);
        Assert.True(_vm.HardwareMapping.ShowEmpty);
    }

    /// <summary>
    /// 侧栏顺序：主控台、总线监控、设备拓扑、硬件映射、报文诊断（未启用）、运行日志、系统设置
    /// </summary>
    [Fact]
    public void NavIndices_FollowSidebarOrder()
        => Assert.Equal([0, 1, 2, 3, 5, 6],
        [
            MainViewModel.MasterConsoleNavIndex, MainViewModel.BusConsoleNavIndex, MainViewModel.TopologyNavIndex,
            MainViewModel.HardwareMappingNavIndex, MainViewModel.RuntimeLogNavIndex, MainViewModel.SettingsNavIndex,
        ]);

    [Fact]
    public void LeaveSettings_ReturnsToSelectedPage()
    {
        _vm.SelectedNavIndex = MainViewModel.SettingsNavIndex;

        _vm.SelectedNavIndex = MainViewModel.MasterConsoleNavIndex;

        Assert.Same(_vm.MasterConsole, _vm.CurrentPage);
    }
}
