using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 拓扑页的结构编辑：新增下级（预填模板、默认停用）、删除、修改序号、复制，以及各自的确认与取消
/// </summary>
public sealed class TopologyViewModelStructureTests
{
    private readonly FakeTopologyRepository _repository = new();

    private async Task<TopologyViewModel> AttachedAsync(EquipmentTopology? stored)
    {
        _repository.Stored = stored;
        var service = new TopologyService(_repository, NullLogger<TopologyService>.Instance);
        await service.LoadAsync(CancellationToken.None);

        var vm = new TopologyViewModel();
        vm.Attach(service);
        return vm;
    }

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

    // ==================== 可用性与预填 ====================

    [Fact]
    public async Task NotConfigured_StructureIsNotEditable()
    {
        var vm = await AttachedAsync(null);

        Assert.False(vm.IsStructureEditable);
        Assert.False(vm.AddChildrenCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cabinet_PrefillsTemplateFromFirstChild_AndDefaultsToDisabled()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 3, 1, 2));

        Select(vm, 1);

        Assert.True(vm.CanAddChildren);
        Assert.True(vm.CanEditPosition);
        Assert.Equal("个层位", vm.AddChildLevelText);
        Assert.Equal(["每层槽位", "每槽治具位", "每治具产品位"], vm.AddTemplateInputs.Select(i => i.Label));
        Assert.Equal([3m, 1m, 2m], vm.AddTemplateInputs.Select(i => i.Value!.Value));
        Assert.False(vm.NewPositionsEnabledInput);
        Assert.Equal(1m, vm.OrdinalInput);
        Assert.False(vm.RenumberCommand.CanExecute(null));

        Assert.False(vm.IsAddPreviewError);
        Assert.Equal("将新增 C01-L03，共 13 个节点（其中产品位 6 个），新位置为停用状态", vm.AddPreviewText);
        Assert.Contains("连同 26 个下级", vm.RemoveHintText);
        Assert.Contains("产品位 12 个", vm.RemoveHintText);
    }

    [Fact]
    public async Task Root_CanAddCabinets_ButCannotBeRenumberedCopiedOrRemoved()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));

        Assert.Same(vm.Roots[0], vm.SelectedItem);
        Assert.True(vm.CanAddChildren);
        Assert.Equal("个柜体", vm.AddChildLevelText);
        Assert.Equal(4, vm.AddTemplateInputs.Count);
        Assert.False(vm.CanEditPosition);
        Assert.Null(vm.OrdinalInput);
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Product_CannotAddChildren()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 2));

        Select(vm, 1, 1, 1, 1, 2);

        Assert.False(vm.CanAddChildren);
        Assert.False(vm.AddChildrenCommand.CanExecute(null));
        Assert.Null(vm.AddPreviewText);
        Assert.Empty(vm.AddTemplateInputs);
        Assert.True(vm.CanEditPosition);
        Assert.Equal("删除后它的 Id 作废，无法恢复", vm.RemoveHintText);
    }

    [Fact]
    public async Task SelectingAnotherNode_ResetsNewPositionsToDisabled()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 1);
        vm.NewPositionsEnabledInput = true;

        Select(vm, 2);

        Assert.False(vm.NewPositionsEnabledInput);
    }

    // ==================== 新增下级 ====================

    [Fact]
    public async Task Add_Disabled_SavesImmediately_ExpandsParent_NewPositionsDisabled()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var cabinet = Select(vm, 1);
        vm.AddCountInput = 2;
        vm.AddTemplateInputs[0].Value = 2; // 每层 2 个槽位

        await vm.AddChildrenCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal([1, 2, 3, 4], saved.GetNode(cabinet.Node.Id).Value.Children.Select(c => c.Ordinal));
        Assert.Equal(2, saved.GetNode(TopologyAddress.Of(1, 4)).Value.Children.Count);
        Assert.False(saved.GetNode(TopologyAddress.Of(1, 3)).Value.IsEnabled);

        Assert.Equal(cabinet.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.True(vm.SelectedItem.IsExpanded);
        Assert.True(vm.SelectedItem.Children[2].IsDisabled);
        Assert.Contains("新增 2 个层位", vm.SaveMessage);
        Assert.Contains("停用状态", vm.SaveMessage);
        Assert.Equal(1m, vm.AddCountInput); // 输入回到默认值
    }

    [Fact]
    public async Task Add_Enabled_RequiresConfirmation_ThenSaves()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1);
        vm.NewPositionsEnabledInput = true;

        await vm.AddChildrenCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Empty(_repository.Saves);
        Assert.Contains("新增并直接启用 4 个位置（其中产品位 1 个）：C01-L03", vm.PendingConfirmationText);
        Assert.Contains("安装到位", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.True(Assert.Single(_repository.Saves).GetNode(TopologyAddress.Of(1, 3)).Value.IsEffectivelyEnabled);
        Assert.Contains("已直接启用", vm.SaveMessage);
    }

    [Fact]
    public async Task EditingTemplate_WhileAddAwaitsConfirmation_CancelsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1);
        vm.NewPositionsEnabledInput = true;
        await vm.AddChildrenCommand.ExecuteAsync();

        vm.AddTemplateInputs[2].Value = 4;

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Contains("结构输入有改动", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task Add_BeyondOrdinalLimit_ShowsPreviewError_AndSavesNothing()
    {
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Node(998)])).Value;
        var vm = await AttachedAsync(stored);
        vm.AddCountInput = 5;

        Assert.True(vm.IsAddPreviewError);
        Assert.Contains("最多还能新增 1 个", vm.AddPreviewText);

        await vm.AddChildrenCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 删除 ====================

    [Fact]
    public async Task Remove_WaitsForConfirmation_ThenSelectsParent()
    {
        var stored = FakeTopologyRepository.Layout(1, 3, 1, 1, 2);
        var vm = await AttachedAsync(stored);
        Select(vm, 1, 2);

        await vm.RemoveCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Contains("删除 5 个位置（其中产品位 2 个）：C01-L02", vm.PendingConfirmationText);
        Assert.Empty(_repository.Saves);

        await vm.ConfirmCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal([1, 3], saved.GetNode(TopologyAddress.Of(1)).Value.Children.Select(c => c.Ordinal));
        Assert.Equal(stored.GetNode(TopologyAddress.Of(1)).Value.Id, vm.SelectedItem!.Node.Id);
        Assert.Contains("已删除 层位 C01-L02", vm.SaveMessage);
    }

    [Fact]
    public async Task Remove_Cancelled_SavesNothing()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 2);
        await vm.RemoveCommand.ExecuteAsync();

        vm.CancelCommand.Execute(null);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);
        Assert.Contains("已取消", vm.SaveMessage);
    }

    [Fact]
    public async Task SwitchingNode_WhileRemoveAwaits_CancelsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 2);
        await vm.RemoveCommand.ExecuteAsync();

        Select(vm, 1);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Contains("结构未修改", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task EditingNodeName_WhileRemoveAwaits_DoesNotCancelIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 2);
        await vm.RemoveCommand.ExecuteAsync();

        // 名称输入属于另一处保存，与等待确认的删除无关
        vm.NodeNameInput = "B 柜";

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, Assert.Single(_repository.Saves).CountAt(TopologyLevel.Cabinet));
    }

    // ==================== 修改序号 ====================

    [Fact]
    public async Task Renumber_RequiresConfirmation_KeepsSelectionById()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var layer = Select(vm, 1, 2);

        vm.OrdinalInput = 5;
        Assert.True(vm.RenumberCommand.CanExecute(null));
        await vm.RenumberCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Contains("C01-L02 → C01-L05", vm.PendingConfirmationText);
        Assert.Contains("现场标牌", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(TopologyAddress.Of(1, 5), Assert.Single(_repository.Saves).GetNode(layer.Node.Id).Value.Address);
        Assert.Equal(layer.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.Equal("C01-L05", vm.SelectedAddressText);
        Assert.Equal(5m, vm.OrdinalInput);
        Assert.Contains("已改为 C01-L05", vm.SaveMessage);
    }

    [Fact]
    public async Task Renumber_ToOccupiedOrdinal_ShowsError()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1, 2);
        vm.OrdinalInput = 1;

        await vm.RenumberCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Contains("已被占用", vm.SaveMessage);
        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 复制 ====================

    [Fact]
    public async Task Copy_SavesDisabledCopy_AndSelectsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 2));
        Select(vm, 1);

        await vm.CopyCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal(3, saved.CountAt(TopologyLevel.Cabinet));
        Assert.Equal("C03", vm.SelectedAddressText);
        Assert.True(vm.SelectedItem!.IsDisabled);
        Assert.Contains("已把 C01 复制为 C03", vm.SaveMessage);
    }
}
