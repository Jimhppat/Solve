using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 拓扑页：接入、按布局首次建档、读取失败时禁止保存、节点浏览。选中节点后的表单见 <see cref="TopologyViewModelNodeEditorTests"/>
/// </summary>
public sealed class TopologyViewModelTests
{
    private readonly FakeTopologyRepository _repository = new();

    private async Task<TopologyViewModel> AttachedAsync(EquipmentTopology? stored = null)
    {
        _repository.Stored = stored;
        var service = new TopologyService(_repository, NullLogger<TopologyService>.Instance);
        await service.LoadAsync(CancellationToken.None);

        var vm = new TopologyViewModel();
        vm.Attach(service);
        return vm;
    }

    private static void SetLayout(TopologyViewModel vm, int c, int l, int s, int f, int p)
    {
        vm.CabinetsInput = c;
        vm.LayersPerCabinetInput = l;
        vm.SlotsPerLayerInput = s;
        vm.FixturesPerSlotInput = f;
        vm.ProductsPerFixtureInput = p;
    }

    private static TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children)
        => new(TopologyNodeId.New(), ordinal, children);

    // ==================== 接入 ====================

    [Fact]
    public void Designer_CannotSave()
    {
        var vm = new TopologyViewModel();

        Assert.False(vm.IsEditable);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains("不可用", vm.SummaryText);
    }

    [Fact]
    public async Task Attach_NotConfigured_OffersFirstCreation()
    {
        var vm = await AttachedAsync();

        Assert.True(vm.IsEditable);
        Assert.False(vm.IsConfigured);
        Assert.Empty(vm.Roots);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal("保存后共 6 个节点，其中产品位 1 个", vm.SummaryText);
        Assert.False(vm.IsSummaryError);
        Assert.Throws<InvalidOperationException>(() => vm.Attach(new TopologyService(_repository, NullLogger<TopologyService>.Instance)));
    }

    [Fact]
    public async Task Attach_Configured_DisablesLayoutSave_AndSelectsRoot()
    {
        var stored = FakeTopologyRepository.Layout(2, 3, 4, 1, 2);

        var vm = await AttachedAsync(stored);

        Assert.True(vm.IsConfigured);
        Assert.Contains("产品位 48", vm.SummaryText);
        Assert.Same(stored.Root, Assert.Single(vm.Roots).Node);
        Assert.Same(vm.Roots[0], vm.SelectedItem);
        Assert.Equal(stored.Root.Id.ToString(), vm.SelectedIdText);

        // 建档后不再提供整机统一布局：它会把规格不同的节点补齐，悄悄加回有意删掉的缺位
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.IsNodeEditable);
    }

    [Fact]
    public async Task LayoutSave_AfterConfigured_DoesNothing()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));
        vm.CabinetsInput = 3;

        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(_repository.Saves);
    }

    // ==================== 首次建档 ====================

    [Fact]
    public async Task Save_FirstCreation_CreatesAllNodes_AndShowsTree()
    {
        var vm = await AttachedAsync();
        vm.SystemNameInput = "老化房 1#";
        SetLayout(vm, 2, 1, 1, 1, 4);
        // 1 + 2 柜 + 2 层 + 2 槽 + 2 治具位 + 8 产品位
        Assert.Contains("共 17 个节点", vm.SummaryText);

        await vm.SaveCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal("老化房 1#", saved.Name);
        Assert.Equal(17, saved.NodeCount);
        Assert.True(vm.IsConfigured);
        Assert.Same(saved.Root, Assert.Single(vm.Roots).Node);
        Assert.False(vm.IsSaveError);
        Assert.Equal("已建档，共 17 个节点", vm.SaveMessage);
        Assert.True(vm.HasNodeMessage); // 建档后结果显示在节点表单里
        Assert.False(vm.HasPageMessage);
        Assert.Equal(TopologySaveState.Idle, vm.State);
    }

    [Fact]
    public async Task OversizedLayout_IsShownAsError_AndNotSaved()
    {
        var vm = await AttachedAsync();
        SetLayout(vm, 999, 999, 999, 999, 999);

        Assert.True(vm.IsSummaryError);
        Assert.Contains("超过上限", vm.SummaryText);

        await vm.SaveCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.True(vm.HasPageMessage);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task EmptyInputs_AreRejectedBeforePlanning()
    {
        var vm = await AttachedAsync();
        vm.SlotsPerLayerInput = null;

        Assert.Equal("请填写每层槽位数", vm.SummaryText);
        await vm.SaveCommand.ExecuteAsync();
        Assert.True(vm.IsSaveError);

        vm.SlotsPerLayerInput = 1;
        vm.SystemNameInput = "   ";
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal("系统名称不能为空", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 读取失败 ====================

    [Fact]
    public async Task LoadError_BlocksSaving_AndReloadRecovers()
    {
        _repository.NextLoadError = FakeTopologyRepository.CorruptError;
        var vm = await AttachedAsync();

        Assert.True(vm.HasLoadError);
        Assert.False(vm.IsEditable);
        Assert.False(vm.IsInputEnabled);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.IsNodeEditable);
        Assert.Contains("已禁止保存", vm.LoadErrorText);

        _repository.NextLoadError = null;
        _repository.Stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await vm.ReloadCommand.ExecuteAsync();

        Assert.False(vm.HasLoadError);
        Assert.True(vm.IsConfigured);
        Assert.True(vm.IsNodeEditable);
        Assert.False(vm.HasSaveMessage);
    }

    // ==================== 节点浏览 ====================

    [Fact]
    public async Task Selection_ShowsTitleAndId()
    {
        var stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);
        var vm = await AttachedAsync(stored);

        var product = vm.Roots[0].Children[0].Children[0].Children[0].Children[0].Children[1];
        vm.SelectedItem = product;

        Assert.Equal("产品位 C01-L01-S01-F01-P02", vm.SelectedTitle);
        Assert.Equal(stored.GetNode(TopologyAddress.Of(1, 1, 1, 1, 2)).Value.Id.ToString(), vm.SelectedIdText);
        Assert.False(vm.IsSelectedDisabled);
        Assert.Equal("产品位 C01-L01-S01-F01-P02", product.Header);
    }

    [Fact]
    public async Task Selection_ShowsWhetherDisabledBySelfOrAncestor()
    {
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1, Node(1)) with { Enabled = false, Name = "高温层" })])).Value;
        var vm = await AttachedAsync(stored);

        var layer = vm.Roots[0].Children[0].Children[0];
        vm.SelectedItem = layer;
        Assert.Equal("停用", vm.SelectedDisabledText);
        Assert.Equal("高温层", layer.Name);

        vm.SelectedItem = layer.Children[0];
        Assert.True(vm.SelectedItem!.IsDisabled);
        Assert.Equal("上级停用", vm.SelectedDisabledText);
    }

    [Fact]
    public async Task FirstDisplay_ExpandsRoot()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));

        Assert.True(vm.Roots[0].IsExpanded);
        Assert.False(vm.Roots[0].Children[0].IsExpanded);
    }
}
