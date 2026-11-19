using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 拓扑页：接入、一键拓扑（首次建档、整机重建、选中节点重建）、读取失败时禁止保存、节点浏览。
/// 选中节点后的表单见 <see cref="TopologyViewModelNodeEditorTests"/>
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

    private static void SetLayout(TopologyViewModel vm, params int[] counts)
    {
        for (var i = 0; i < counts.Length; i++)
            vm.LayoutInputs[i].Value = counts[i];
    }

    private static decimal?[] LayoutValues(TopologyViewModel vm) => vm.LayoutInputs.Select(input => input.Value).ToArray();

    /// <summary>
    /// 沿序号逐级找到树上的节点并选中；不传序号即系统根
    /// </summary>
    private static TopologyTreeItemViewModel Select(TopologyViewModel vm, params int[] ordinals)
    {
        var item = vm.Roots[0];
        foreach (var ordinal in ordinals)
            item = item.Children.First(child => child.Node.Ordinal == ordinal);

        vm.SelectedItem = item;
        return item;
    }

    private static TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children)
        => new(TopologyNodeId.New(), ordinal, children);

    // ==================== 接入 ====================

    [Fact]
    public void Designer_CannotGenerate()
    {
        var vm = new TopologyViewModel();

        Assert.False(vm.IsEditable);
        Assert.False(vm.GenerateCommand.CanExecute(null));
        Assert.Equal("配置库不可用", vm.LayoutSummaryText);
    }

    [Fact]
    public async Task Attach_NotConfigured_OffersFirstCreation()
    {
        var vm = await AttachedAsync();

        Assert.False(vm.IsConfigured);
        Assert.Empty(vm.Roots);
        Assert.True(vm.ShowSystemNameInput);
        Assert.False(vm.ShowScope);
        Assert.Equal(["柜体数", "每柜层位", "每层槽位", "每槽治具位", "每治具产品位"], vm.LayoutInputs.Select(i => i.Label));
        Assert.All(vm.LayoutInputs, input => Assert.Equal(1m, input.Value));
        Assert.Equal("生成后共 6 个节点，其中产品位 1 个", vm.LayoutSummaryText);
        Assert.True(vm.GenerateCommand.CanExecute(null));
        Assert.Throws<InvalidOperationException>(() => vm.Attach(new TopologyService(_repository, NullLogger<TopologyService>.Instance)));
    }

    // ==================== 首次建档 ====================

    [Fact]
    public async Task FirstCreation_CreatesAllNodes_ThenCardPrefillsTheNewLayout()
    {
        var vm = await AttachedAsync();
        vm.SystemNameInput = "老化房 1#";
        SetLayout(vm, 2, 1, 1, 1, 4);
        // 1 + 2 柜 + 2 层 + 2 槽 + 2 治具位 + 8 产品位
        Assert.Equal("生成后共 17 个节点，其中产品位 8 个", vm.LayoutSummaryText);

        await vm.GenerateCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal(("老化房 1#", 17), (saved.Name, saved.NodeCount));
        Assert.True(vm.IsConfigured);
        Assert.Same(saved.Root, Assert.Single(vm.Roots).Node);
        Assert.Equal("已建档，共 17 个节点", vm.SaveMessage);
        Assert.True(vm.HasLayoutMessage);
        Assert.False(vm.HasNodeMessage);

        // 建档后卡片仍在：不再填系统名称，可选范围，数量按新拓扑预填
        Assert.False(vm.ShowSystemNameInput);
        Assert.True(vm.ShowScope);
        Assert.Equal([2m, 1m, 1m, 1m, 4m], LayoutValues(vm));
    }

    [Fact]
    public async Task OversizedLayout_IsShownAsError_AndNotSaved()
    {
        var vm = await AttachedAsync();
        SetLayout(vm, 999, 999, 999, 999, 999);

        Assert.True(vm.IsLayoutSummaryError);
        Assert.Contains("超过上限", vm.LayoutSummaryText);

        await vm.GenerateCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.True(vm.HasLayoutMessage);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task EmptyInputs_AreRejectedBeforePlanning()
    {
        var vm = await AttachedAsync();
        vm.LayoutInputs[2].Value = null;

        Assert.Equal("请填写每层槽位数", vm.LayoutSummaryText);
        await vm.GenerateCommand.ExecuteAsync();
        Assert.True(vm.IsSaveError);

        vm.LayoutInputs[2].Value = 1;
        vm.SystemNameInput = "   ";
        await vm.GenerateCommand.ExecuteAsync();
        Assert.Equal("系统名称不能为空", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 整机重建 ====================

    [Fact]
    public async Task Configured_PrefillsUniformLevels_AndLeavesMixedLevelsEmpty()
    {
        // 1 号柜 2 层、2 号柜 1 层
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            Node(1, Node(1, Node(1, Node(1, Node(1), Node(2)))), Node(2, Node(1, Node(1, Node(1), Node(2))))),
            Node(2, Node(1, Node(1, Node(1, Node(1), Node(2))))),
        ])).Value;

        var vm = await AttachedAsync(stored);

        Assert.True(vm.IsWholeDeviceScope);
        Assert.Equal([2m, null, 1m, 1m, 2m], LayoutValues(vm));
        Assert.Equal("不统一", vm.LayoutInputs[1].Placeholder);
        Assert.Null(vm.LayoutInputs[0].Placeholder);
        Assert.Equal("请填写每柜层位数", vm.LayoutSummaryText);
    }

    [Fact]
    public async Task Regenerate_Unchanged_SavesNothing()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 2));

        await vm.GenerateCommand.ExecuteAsync();

        Assert.Empty(_repository.Saves);
        Assert.Equal("结构没有变化", vm.SaveMessage);
    }

    [Fact]
    public async Task Regenerate_WholeDevice_ConfirmsInCard_AndKeepsIds()
    {
        var stored = FakeTopologyRepository.Layout(2, 1, 1, 1, 2);
        var vm = await AttachedAsync(stored);
        vm.LayoutInputs[1].Value = 2;
        Assert.Equal("生成后共 23 个节点，其中产品位 8 个", vm.LayoutSummaryText);

        await vm.GenerateCommand.ExecuteAsync();

        Assert.True(vm.IsLayoutAwaitingConfirmation);
        Assert.False(vm.IsNodeAwaitingConfirmation);
        Assert.Equal("将新增并启用 C01-L02、C02-L02，共 10 个位置（产品位 4 个）", vm.PendingConfirmationText);
        Assert.Empty(_repository.Saves);

        await vm.ConfirmCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        foreach (var node in TopologyLevels.All.SelectMany(stored.GetNodes))
            Assert.Equal(node.Address, saved.GetNode(node.Id).Value.Address);
        Assert.Equal("已生成，新增 10 个位置", vm.SaveMessage);
        Assert.True(vm.HasLayoutMessage);
    }

    [Fact]
    public async Task EditingLayoutInput_WhileAwaitingConfirmation_CancelsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        vm.LayoutInputs[0].Value = 1;
        await vm.GenerateCommand.ExecuteAsync();
        Assert.True(vm.IsLayoutAwaitingConfirmation);

        vm.LayoutInputs[0].Value = 3;

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task BrowsingTree_WhileWholeDeviceGenerationAwaits_DoesNotCancelIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        vm.LayoutInputs[0].Value = 1;
        await vm.GenerateCommand.ExecuteAsync();

        // 点开要删除的 2 号柜看看：整机生成与选中哪个节点无关，不取消
        Select(vm, 2);

        Assert.True(vm.IsLayoutAwaitingConfirmation);
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, Assert.Single(_repository.Saves).CountAt(TopologyLevel.Cabinet));
    }

    // ==================== 选中节点重建 ====================

    [Fact]
    public async Task NodeScope_PrefillsFromSelectedNode()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 2, 1, 1, 1));
        Select(vm, 2);

        Assert.True(vm.CanScopeToNode);
        Assert.Equal("选中：柜体 C02", vm.NodeScopeText);

        vm.IsNodeScope = true;

        Assert.False(vm.IsWholeDeviceScope);
        Assert.Equal(["层位数", "每层槽位", "每槽治具位", "每治具产品位"], vm.LayoutInputs.Select(i => i.Label));
        Assert.Equal([2m, 1m, 1m, 1m], LayoutValues(vm));
    }

    [Fact]
    public async Task NodeScope_RegeneratesOnlyThatSubtree_ThenSelectsIt()
    {
        var stored = FakeTopologyRepository.Layout(2, 2, 1, 1, 1);
        var vm = await AttachedAsync(stored);
        var cabinet = Select(vm, 2);
        vm.IsNodeScope = true;
        vm.LayoutInputs[0].Value = 1;
        Assert.Equal("生成后共 15 个节点，其中产品位 3 个", vm.LayoutSummaryText);

        await vm.GenerateCommand.ExecuteAsync();

        Assert.Equal("将删除 C02-L02 及其下级，共 4 个位置（产品位 1 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal(2, saved.GetDescendants(TopologyAddress.Of(1), TopologyLevel.Layer).Count);
        Assert.Single(saved.GetDescendants(TopologyAddress.Of(2), TopologyLevel.Layer));
        Assert.Equal(cabinet.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.True(vm.SelectedItem.IsExpanded);
        Assert.True(vm.IsNodeScope);
        Assert.Equal([1m, 1m, 1m, 1m], LayoutValues(vm));
        Assert.Equal("已生成，删除 4 个位置", vm.SaveMessage);
    }

    [Fact]
    public async Task NodeScope_IsUnavailableForRootAndProduct()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));

        vm.IsNodeScope = true; // 选中的是系统根：就是整机
        Assert.True(vm.IsWholeDeviceScope);

        Select(vm, 1, 1, 1, 1, 1);
        Assert.False(vm.CanScopeToNode);
        vm.IsNodeScope = true;
        Assert.True(vm.IsWholeDeviceScope);
        Assert.Equal(5, vm.LayoutInputs.Count);
    }

    [Fact]
    public async Task NodeScope_SelectingRoot_FallsBackToWholeDevice()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 1);
        vm.IsNodeScope = true;

        Select(vm);

        Assert.True(vm.IsWholeDeviceScope);
        Assert.Equal(5, vm.LayoutInputs.Count);
    }

    [Fact]
    public async Task NodeScope_SwitchingNode_CancelsPendingGeneration_AndPrefillsNewNode()
    {
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            Node(1, Node(1, Node(1, Node(1, Node(1)))), Node(2, Node(1, Node(1, Node(1))))),
            Node(2, Node(1, Node(1, Node(1, Node(1))))),
        ])).Value;
        var vm = await AttachedAsync(stored);
        Select(vm, 1);
        vm.IsNodeScope = true;
        vm.LayoutInputs[0].Value = 1;
        await vm.GenerateCommand.ExecuteAsync();
        Assert.True(vm.IsLayoutAwaitingConfirmation);

        Select(vm, 2);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Equal("选中：柜体 C02", vm.NodeScopeText);
        Assert.Equal([1m, 1m, 1m, 1m], LayoutValues(vm));
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
        Assert.False(vm.IsLayoutInputEnabled);
        Assert.False(vm.GenerateCommand.CanExecute(null));
        Assert.False(vm.IsNodeEditable);
        Assert.Contains("已禁止保存", vm.LoadErrorText);
        Assert.Equal("拓扑未能读取", vm.LayoutSummaryText);

        _repository.NextLoadError = null;
        _repository.Stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await vm.ReloadCommand.ExecuteAsync();

        Assert.False(vm.HasLoadError);
        Assert.True(vm.IsConfigured);
        Assert.True(vm.GenerateCommand.CanExecute(null));
        Assert.True(vm.IsNodeEditable);
        Assert.False(vm.HasSaveMessage);
    }

    // ==================== 节点浏览 ====================

    [Fact]
    public async Task Configured_SelectsRoot_AndSummarizesLevels()
    {
        var stored = FakeTopologyRepository.Layout(2, 3, 4, 1, 2);

        var vm = await AttachedAsync(stored);

        Assert.Contains("产品位 48", vm.SummaryText);
        Assert.Same(stored.Root, Assert.Single(vm.Roots).Node);
        Assert.Same(vm.Roots[0], vm.SelectedItem);
        Assert.Equal(stored.Root.Id.ToString(), vm.SelectedIdText);
    }

    [Fact]
    public async Task Selection_ShowsTitleAndId()
    {
        var stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);
        var vm = await AttachedAsync(stored);

        var product = Select(vm, 1, 1, 1, 1, 2);

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

        var layer = Select(vm, 1, 1);
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
