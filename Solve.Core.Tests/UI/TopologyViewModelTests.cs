using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 拓扑页：接入、按布局建档 / 扩缩容、删除位置前的确认、读取失败时禁止保存、节点浏览的两套身份
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
        Assert.Contains("尚未建档", vm.SummaryText);
        Assert.Throws<InvalidOperationException>(() => vm.Attach(new TopologyService(_repository, NullLogger<TopologyService>.Instance)));
    }

    [Fact]
    public async Task Attach_Configured_DisablesLayoutSave_AndSelectsRoot()
    {
        var stored = FakeTopologyRepository.Layout(2, 3, 4, 1, 2);

        var vm = await AttachedAsync(stored);

        Assert.True(vm.IsConfigured);
        Assert.Contains("产品位 48", vm.SummaryText);
        Assert.Equal(stored.Root.Id.ToString(), vm.SystemIdText);
        Assert.Same(stored.Root, Assert.Single(vm.Roots).Node);
        Assert.Same(vm.Roots[0], vm.SelectedItem);

        // 建档后不再提供整机统一布局：它会把规格不同的节点补齐，悄悄加回有意删掉的缺位
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.IsStructureEditable);
    }

    [Fact]
    public async Task LayoutSave_AfterConfigured_DoesNothing()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));
        vm.CabinetsInput = 3;

        await vm.SaveCommand.ExecuteAsync();

        Assert.Empty(_repository.Saves);
    }

    // ==================== 保存 ====================

    [Fact]
    public async Task Save_FirstCreation_CreatesAllNodes_AndShowsTree()
    {
        var vm = await AttachedAsync();
        vm.SystemNameInput = "老化房 1#";
        SetLayout(vm, 2, 1, 1, 1, 4);
        // 1 + 2 柜 + 2 层 + 2 槽 + 2 治具位 + 8 产品位
        Assert.Contains("共 17 个节点", vm.LayoutPreviewText);

        await vm.SaveCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal("老化房 1#", saved.Name);
        Assert.Equal(17, saved.NodeCount);
        Assert.True(vm.IsConfigured);
        Assert.Same(saved.Root, Assert.Single(vm.Roots).Node);
        Assert.False(vm.IsSaveError);
        Assert.Contains("已建档", vm.SaveMessage);
        Assert.Equal(TopologySaveState.Idle, vm.State);
    }

    [Fact]
    public async Task OversizedLayout_IsShownAsError_AndNotSaved()
    {
        var vm = await AttachedAsync();
        SetLayout(vm, 999, 999, 999, 999, 999);

        Assert.True(vm.IsLayoutPreviewError);
        Assert.Contains("超过上限", vm.LayoutPreviewText);

        await vm.SaveCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task EmptyInputs_AreRejectedBeforePlanning()
    {
        var vm = await AttachedAsync();
        vm.SlotsPerLayerInput = null;

        Assert.Equal("请填写每层槽位数", vm.LayoutPreviewText);
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
        Assert.Contains("已禁止保存", vm.LoadErrorText);

        Assert.False(vm.IsStructureEditable);
        Assert.False(vm.AddChildrenCommand.CanExecute(null));

        _repository.NextLoadError = null;
        _repository.Stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await vm.ReloadCommand.ExecuteAsync();

        Assert.False(vm.HasLoadError);
        Assert.True(vm.IsConfigured);
        Assert.True(vm.AddChildrenCommand.CanExecute(null));
    }

    // ==================== 节点浏览 ====================

    [Fact]
    public async Task Selection_ShowsBothIdentities()
    {
        var stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);
        var vm = await AttachedAsync(stored);

        var product = vm.Roots[0].Children[0].Children[0].Children[0].Children[0].Children[1];
        vm.SelectedItem = product;

        Assert.Equal("产品位", vm.SelectedLevelText);
        Assert.Equal("C01-L01-S01-F01-P02", vm.SelectedAddressText);
        Assert.Equal(stored.GetNode(TopologyAddress.Of(1, 1, 1, 1, 2)).Value.Id.ToString(), vm.SelectedIdText);
        Assert.Equal("启用", vm.SelectedStatusText);
        Assert.Contains("最底层", vm.SelectedChildrenText);
        Assert.Equal("产品位 C01-L01-S01-F01-P02", product.Header);
    }

    [Fact]
    public async Task Selection_OfMaskedSubtree_ExplainsWhichAncestorIsDisabled()
    {
        TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children) => new(TopologyNodeId.New(), ordinal, children);
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1, Node(1)) with { Enabled = false, Name = "高温层" })])).Value;
        var vm = await AttachedAsync(stored);

        var layer = vm.Roots[0].Children[0].Children[0];
        vm.SelectedItem = layer.Children[0];

        Assert.True(layer.IsDisabled);
        Assert.Equal("高温层", layer.Name);
        Assert.True(vm.SelectedItem!.IsDisabled);
        Assert.Contains("层位 C01-L01 已停用", vm.SelectedStatusText);
    }

    [Fact]
    public async Task FirstDisplay_ExpandsRoot()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));

        Assert.True(vm.Roots[0].IsExpanded);
        Assert.False(vm.Roots[0].Children[0].IsExpanded);
    }

    // ==================== 节点编辑 ====================

    /// <summary>
    /// 选中 1 号柜下的层位 C01-L01
    /// </summary>
    private static TopologyTreeItemViewModel SelectLayer(TopologyViewModel vm)
    {
        var layer = vm.Roots[0].Children[0].Children[0];
        vm.SelectedItem = layer;
        return layer;
    }

    [Fact]
    public async Task Root_NameIsSystemName_ButRootCannotBeDisabled()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));

        Assert.Same(vm.Roots[0], vm.SelectedItem);
        Assert.True(vm.IsNodeEditable);
        Assert.False(vm.IsNodeEnabledInputEnabled);
        Assert.True(vm.HasNodeEditHint);
        Assert.Equal("老化房", vm.NodeNameInput);
        Assert.False(vm.SaveNodeCommand.CanExecute(null));

        vm.NodeNameInput = "  ";
        await vm.SaveNodeCommand.ExecuteAsync();
        Assert.Equal("系统名称不能为空", vm.SaveMessage);
        Assert.Empty(_repository.Saves);

        vm.NodeNameInput = "老化房 2#";
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal("老化房 2#", Assert.Single(_repository.Saves).Name);
        Assert.Equal("系统名称已改为「老化房 2#」", vm.SaveMessage);
        Assert.Equal("系统 老化房 2#", vm.Roots[0].Header);
    }

    [Fact]
    public async Task Selecting_ShowsNodeValues_AndSaveNeedsAChange()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));

        SelectLayer(vm);

        Assert.True(vm.IsNodeEditable);
        Assert.Equal("", vm.NodeNameInput);
        Assert.True(vm.NodeEnabledInput);
        Assert.False(vm.HasNodeChanges);
        Assert.False(vm.SaveNodeCommand.CanExecute(null));

        vm.NodeNameInput = "高温层";
        Assert.True(vm.SaveNodeCommand.CanExecute(null));

        vm.NodeNameInput = "   "; // 空白等同未命名，不算修改
        Assert.False(vm.HasNodeChanges);
    }

    [Fact]
    public async Task Rename_SavesImmediately_KeepsSelectionAndExpansionById()
    {
        var stored = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        var vm = await AttachedAsync(stored);
        var layer = SelectLayer(vm);
        vm.Roots[0].Children[0].Children[1].IsExpanded = true; // 不在选中路径上的 C01-L02

        vm.NodeNameInput = "  高温层 ";
        await vm.SaveNodeCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal("高温层", saved.GetNode(layer.Node.Id).Value.Name);
        Assert.Contains("已命名为「高温层」", vm.SaveMessage);

        // 树已按新拓扑重建：选中项与展开状态按 Id 保留，节点输入显示保存后的值
        Assert.NotSame(layer, vm.SelectedItem);
        Assert.Equal(layer.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.Equal("高温层", vm.SelectedItem.Name);
        Assert.True(vm.Roots[0].Children[0].Children[1].IsExpanded);
        Assert.Equal("高温层", vm.NodeNameInput);
        Assert.False(vm.HasNodeChanges);
    }

    [Fact]
    public async Task Disable_SavesWithoutConfirmation_AndReportsAffectedPositions()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 3));
        var layer = SelectLayer(vm);

        vm.NodeEnabledInput = false;
        await vm.SaveNodeCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.False(saved.GetNode(layer.Node.Id).Value.IsEnabled);
        Assert.Contains("已停用，6 个位置不再接受命令", vm.SaveMessage); // 层位 + 槽位 + 治具位 + 3 个产品位
        Assert.True(vm.SelectedItem!.IsDisabled);
        Assert.Contains("已停用", vm.SelectedStatusText);
    }

    [Fact]
    public async Task Reenable_RequiresConfirmation_ThenSaves()
    {
        TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children) => new(TopologyNodeId.New(), ordinal, children);
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1, Node(1, Node(1, Node(1), Node(2)))) with { Enabled = false })])).Value;
        var vm = await AttachedAsync(stored);
        SelectLayer(vm);

        vm.NodeEnabledInput = true;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Empty(_repository.Saves);
        Assert.Contains("重新启用 5 个位置（其中产品位 2 个）：C01-L01", vm.PendingConfirmationText);
        Assert.Contains("维护已结束", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.True(Assert.Single(_repository.Saves).GetNode(TopologyAddress.Of(1, 1)).Value.IsEnabled);
        Assert.Contains("已启用，5 个位置恢复可用", vm.SaveMessage);
    }

    [Fact]
    public async Task SwitchingNode_WhileNodeChangeAwaitsConfirmation_CancelsIt()
    {
        TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children) => new(TopologyNodeId.New(), ordinal, children);
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1) with { Enabled = false }, Node(2))])).Value;
        var vm = await AttachedAsync(stored);
        SelectLayer(vm);
        vm.NodeEnabledInput = true;
        await vm.SaveNodeCommand.ExecuteAsync();
        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);

        vm.SelectedItem = vm.Roots[0].Children[0].Children[1];

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Contains("已切换节点", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task NodeSave_AfterLoadError_IsNotPossible()
    {
        _repository.NextLoadError = FakeTopologyRepository.CorruptError;
        var vm = await AttachedAsync();

        Assert.False(vm.IsNodeEditable);
        Assert.False(vm.SaveNodeCommand.CanExecute(null));
    }
}
