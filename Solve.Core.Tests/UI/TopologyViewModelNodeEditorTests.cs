using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 拓扑页的节点表单：选中即修改，名称、序号、启用、下级数量一次保存；复制、删除；确认、取消、还原
/// </summary>
public sealed class TopologyViewModelNodeEditorTests
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

    // ==================== 表单内容 ====================

    [Fact]
    public async Task Cabinet_ShowsItsValues_AndNothingToSaveYet()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 3, 1, 2));

        Select(vm, 1);

        Assert.Equal("柜体 C01", vm.SelectedTitle);
        Assert.Equal("", vm.NodeNameInput);
        Assert.Equal("C01", vm.NodeNamePlaceholder);
        Assert.True(vm.ShowPositionInputs);
        Assert.Equal(1m, vm.OrdinalInput);
        Assert.True(vm.NodeEnabledInput);
        Assert.True(vm.ShowChildCountInput);
        Assert.Equal("层位数", vm.ChildCountLabel);
        Assert.Equal(2m, vm.ChildCountInput);
        Assert.False(vm.HasNodeChanges);
        Assert.False(vm.SaveNodeCommand.CanExecute(null));
        Assert.False(vm.RevertNodeCommand.CanExecute(null));
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.True(vm.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Product_HasNoChildCount()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 2));

        Select(vm, 1, 1, 1, 1, 2);

        Assert.False(vm.ShowChildCountInput);
        Assert.Null(vm.ChildCountLabel);
        Assert.Null(vm.ChildCountInput);
        Assert.True(vm.ShowPositionInputs);
    }

    [Fact]
    public async Task BlankName_IsSameAsUnnamed()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1, 1);

        vm.NodeNameInput = "高温层";
        Assert.True(vm.SaveNodeCommand.CanExecute(null));

        vm.NodeNameInput = "   ";
        Assert.False(vm.HasNodeChanges);
    }

    // ==================== 系统根 ====================

    [Fact]
    public async Task Root_EditsSystemNameAndCabinetCount_ButCannotBeDisabledOrRemoved()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));

        Assert.Same(vm.Roots[0], vm.SelectedItem);
        Assert.Equal("系统", vm.SelectedTitle);
        Assert.Equal("老化房", vm.NodeNameInput);
        Assert.False(vm.ShowPositionInputs);
        Assert.Equal("柜体数", vm.ChildCountLabel);
        Assert.Equal(2m, vm.ChildCountInput);
        Assert.False(vm.CopyCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));

        vm.NodeNameInput = "  ";
        await vm.SaveNodeCommand.ExecuteAsync();
        Assert.Equal("系统名称不能为空", vm.SaveMessage);
        Assert.Empty(_repository.Saves);

        vm.NodeNameInput = "老化房 2#";
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal("老化房 2#", Assert.Single(_repository.Saves).Name);
        Assert.Equal("已保存", vm.SaveMessage);
        Assert.Equal("系统 老化房 2#", vm.Roots[0].Header);
    }

    // ==================== 名称与启用 ====================

    [Fact]
    public async Task Rename_SavesImmediately_KeepsSelectionAndExpansionById()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var layer = Select(vm, 1, 1);
        vm.Roots[0].Children[0].Children[1].IsExpanded = true; // 不在选中路径上的 C01-L02

        vm.NodeNameInput = "  高温层 ";
        await vm.SaveNodeCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal("高温层", saved.GetNode(layer.Node.Id).Value.Name);
        Assert.Equal("已保存", vm.SaveMessage);
        Assert.True(vm.HasNodeMessage);

        // 树已按新拓扑重建：选中项与展开状态按 Id 保留，表单显示保存后的值
        Assert.NotSame(layer, vm.SelectedItem);
        Assert.Equal(layer.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.Equal("高温层", vm.SelectedItem.Name);
        Assert.True(vm.Roots[0].Children[0].Children[1].IsExpanded);
        Assert.Equal("高温层", vm.NodeNameInput);
        Assert.False(vm.HasNodeChanges);
    }

    [Fact]
    public async Task Disable_SavesWithoutConfirmation()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 3));
        var layer = Select(vm, 1, 1);

        vm.NodeEnabledInput = false;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.False(Assert.Single(_repository.Saves).GetNode(layer.Node.Id).Value.IsEnabled);
        Assert.True(vm.SelectedItem!.IsDisabled);
        Assert.Equal("停用", vm.SelectedDisabledText);
    }

    [Fact]
    public async Task Reenable_RequiresConfirmation_ThenSaves()
    {
        var stored = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1, Node(1, Node(1, Node(1), Node(2)))) with { Enabled = false })])).Value;
        var vm = await AttachedAsync(stored);
        Select(vm, 1, 1);

        vm.NodeEnabledInput = true;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Empty(_repository.Saves);
        Assert.Equal("将重新启用 C01-L01，共 5 个位置（产品位 2 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.True(Assert.Single(_repository.Saves).GetNode(TopologyAddress.Of(1, 1)).Value.IsEnabled);
        Assert.Equal("已保存", vm.SaveMessage);
    }

    // ==================== 下级数量 ====================

    [Fact]
    public async Task GrowChildCount_ConfirmsNewPositions_ThenExpandsNode()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var cabinet = Select(vm, 1);

        vm.ChildCountInput = 4;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);
        Assert.Equal("将新增并启用 C01-L03、C01-L04，共 8 个位置（产品位 2 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(4, Assert.Single(_repository.Saves).GetNode(cabinet.Node.Id).Value.Children.Count);
        Assert.Equal(cabinet.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.True(vm.SelectedItem.IsExpanded);
        Assert.Equal(4m, vm.ChildCountInput);
        Assert.Equal("已保存，新增 8 个位置", vm.SaveMessage);
    }

    [Fact]
    public async Task ShrinkChildCount_ConfirmsRemovedPositions()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 3, 1, 1, 2));
        Select(vm, 1);

        vm.ChildCountInput = 1;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal("将删除 C01-L02、C01-L03 及其下级，共 10 个位置（产品位 4 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Assert.Single(_repository.Saves).CountAt(TopologyLevel.Layer));
        Assert.Equal("已保存，删除 10 个位置", vm.SaveMessage);
    }

    [Fact]
    public async Task EmptyChildCount_IsRejected()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1);

        vm.ChildCountInput = null;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Equal("请填写层位数", vm.SaveMessage);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 序号 ====================

    [Fact]
    public async Task Renumber_RequiresConfirmation_KeepsSelectionById()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var layer = Select(vm, 1, 2);

        vm.OrdinalInput = 5;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Equal("位置编码将改变 C01-L02 → C01-L05，共 4 个位置，现场标牌需同步更换", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(TopologyAddress.Of(1, 5), Assert.Single(_repository.Saves).GetNode(layer.Node.Id).Value.Address);
        Assert.Equal(layer.Node.Id, vm.SelectedItem!.Node.Id);
        Assert.Equal("层位 C01-L05", vm.SelectedTitle);
        Assert.Equal(5m, vm.OrdinalInput);
    }

    [Fact]
    public async Task Renumber_ToOccupiedOrdinal_ShowsError_ThatEditingClears()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        Select(vm, 1, 2);

        vm.OrdinalInput = 1;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Contains("已被占用", vm.SaveMessage);
        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);

        vm.OrdinalInput = 3;

        Assert.False(vm.HasSaveMessage);
    }

    [Fact]
    public async Task AllChanges_SaveTogether_WithOneConfirmation()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 2, 1, 1, 1));
        var layer = Select(vm, 1, 2);

        vm.NodeNameInput = "高温层";
        vm.OrdinalInput = 5;
        vm.ChildCountInput = 3;
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.Contains("将新增并启用", vm.PendingConfirmationText);
        Assert.Contains("位置编码将改变 C01-L02 → C01-L05", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves).GetNode(layer.Node.Id).Value;
        Assert.Equal((TopologyAddress.Of(1, 5), "高温层", 3), (saved.Address, saved.Name, saved.Children.Count));
    }

    // ==================== 确认、取消、还原 ====================

    [Fact]
    public async Task EditingInput_WhileAwaitingConfirmation_CancelsWithoutMessage()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 3, 1, 1, 1));
        Select(vm, 1);
        vm.ChildCountInput = 1;
        await vm.SaveNodeCommand.ExecuteAsync();

        vm.ChildCountInput = 2;

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.False(vm.HasSaveMessage);
        Assert.Null(vm.PendingConfirmationText);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task SwitchingNode_WhileAwaitingConfirmation_CancelsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 1));
        Select(vm, 2);
        await vm.RemoveCommand.ExecuteAsync();
        Assert.Equal(TopologySaveState.AwaitingConfirmation, vm.State);

        Select(vm, 1);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);
    }

    [Fact]
    public async Task Cancel_SavesNothing_AndKeepsInputs()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 3, 1, 1, 1));
        Select(vm, 1);
        vm.ChildCountInput = 1;
        await vm.SaveNodeCommand.ExecuteAsync();

        vm.CancelCommand.Execute(null);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Empty(_repository.Saves);
        Assert.Equal(1m, vm.ChildCountInput);
        Assert.True(vm.HasNodeChanges);
    }

    [Fact]
    public async Task Revert_RestoresSavedValues()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 3, 1, 1, 1));
        Select(vm, 1);
        vm.NodeNameInput = "A 柜";
        vm.ChildCountInput = 1;
        await vm.SaveNodeCommand.ExecuteAsync();
        Assert.True(vm.RevertNodeCommand.CanExecute(null));

        vm.RevertNodeCommand.Execute(null);

        Assert.Equal(TopologySaveState.Idle, vm.State);
        Assert.Equal("", vm.NodeNameInput);
        Assert.Equal(3m, vm.ChildCountInput);
        Assert.False(vm.HasNodeChanges);
        Assert.Empty(_repository.Saves);
    }

    // ==================== 复制与删除 ====================

    [Fact]
    public async Task Copy_ConfirmsEnabledCopy_ThenSelectsIt()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 2));
        Select(vm, 1);

        await vm.CopyCommand.ExecuteAsync();

        Assert.Equal("将新增并启用 C03，共 6 个位置（产品位 2 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(3, Assert.Single(_repository.Saves).CountAt(TopologyLevel.Cabinet));
        Assert.Equal("柜体 C03", vm.SelectedTitle);
        Assert.Equal("已复制为 C03", vm.SaveMessage);
    }

    [Fact]
    public async Task Remove_ConfirmsThenSelectsParent()
    {
        var stored = FakeTopologyRepository.Layout(1, 3, 1, 1, 2);
        var vm = await AttachedAsync(stored);
        Select(vm, 1, 2);

        await vm.RemoveCommand.ExecuteAsync();

        Assert.Equal("将删除 C01-L02 及其下级，共 5 个位置（产品位 2 个）", vm.PendingConfirmationText);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal([1, 3], Assert.Single(_repository.Saves).GetNode(TopologyAddress.Of(1)).Value.Children.Select(c => c.Ordinal));
        Assert.Equal(stored.GetNode(TopologyAddress.Of(1)).Value.Id, vm.SelectedItem!.Node.Id);
        Assert.Equal("已删除 C01-L02", vm.SaveMessage);
    }

    // ==================== 接点 ====================

    [Fact]
    public async Task Ports_AddEditSave_AndProductsShowInheritedRequirement()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 2));
        var layer = Select(vm, 1, 1);
        Assert.Equal("产品位接点", vm.PortsLabel);
        Assert.False(vm.HasPortRows);

        vm.AddPortCommand.Execute(null);
        Assert.True(vm.HasNodeChanges);
        var row = Assert.Single(vm.PortRows);
        row.Kind = ChannelKindOption.All.Single(o => o.Kind == ChannelKind.Measure);
        row.Name = " V ";
        row.CountInput = 4;

        await vm.SaveNodeCommand.ExecuteAsync();

        var saved = Assert.Single(_repository.Saves);
        Assert.Equal([new PortGroup(ChannelKind.Measure, "V", 4)], saved.GetNode(layer.Node.Id).Value.Ports);
        Assert.Equal("已保存", vm.SaveMessage);
        Assert.False(vm.HasNodeChanges);

        // 产品位：标题是"接点"，列出从层位继承的要求
        Select(vm, 1, 1, 1, 1, 2);
        Assert.Equal("接点", vm.PortsLabel);
        Assert.Equal(["C01-L01：采集 V × 4"], vm.InheritedPorts);
        Assert.False(vm.HasPortRows);
    }

    [Fact]
    public async Task Ports_InvalidRow_IsReported_AndRevertRestores()
    {
        var vm = await AttachedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));
        Select(vm, 1);

        vm.AddPortCommand.Execute(null);
        vm.PortRows[0].CountInput = 3; // 多个接点必须有名称（前缀）
        await vm.SaveNodeCommand.ExecuteAsync();

        Assert.True(vm.IsSaveError);
        Assert.Contains("必须填写名称", vm.SaveMessage);
        Assert.Empty(_repository.Saves);

        vm.RevertNodeCommand.Execute(null);
        Assert.False(vm.HasPortRows);
        Assert.False(vm.HasNodeChanges);
    }

    [Fact]
    public async Task NodeSave_AfterLoadError_IsNotPossible()
    {
        _repository.NextLoadError = FakeTopologyRepository.CorruptError;
        var vm = await AttachedAsync(null);

        Assert.False(vm.IsNodeEditable);
        Assert.False(vm.SaveNodeCommand.CanExecute(null));
    }
}
