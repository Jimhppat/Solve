using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Topology.TestSupport;

namespace Solve.Core.Tests.Topology;

/// <summary>
/// 拓扑用例服务：启动加载、读取失败时禁止保存、按布局预览变更、保存与过期预览拦截
/// </summary>
public sealed class TopologyServiceTests
{
    private readonly FakeTopologyRepository _repository = new();
    private readonly TopologyService _service;

    public TopologyServiceTests()
    {
        _service = new TopologyService(_repository, NullLogger<TopologyService>.Instance);
    }

    private async Task<TopologyService> LoadedAsync(EquipmentTopology? stored)
    {
        _repository.Stored = stored;
        Assert.True((await _service.LoadAsync(CancellationToken.None)).IsSuccess);
        return _service;
    }

    // ---------- 加载 ----------

    [Fact]
    public async Task Load_NotConfigured_HasNoTopologyAndNoError()
    {
        await LoadedAsync(null);

        Assert.Null(_service.Current);
        Assert.Null(_service.LoadError);
    }

    [Fact]
    public async Task Load_Stored_BecomesCurrent()
    {
        var stored = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);

        await LoadedAsync(stored);

        Assert.Same(stored, _service.Current);
    }

    [Fact]
    public async Task LoadFailure_BlocksPlanAndSave_UntilReloadSucceeds()
    {
        _repository.NextLoadError = FakeTopologyRepository.CorruptError;

        var loaded = await _service.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsFailure);
        Assert.Null(_service.Current);
        Assert.Same(FakeTopologyRepository.CorruptError, _service.LoadError);
        Assert.Equal(TopologyErrors.SaveBlockedCode, _service.PlanLayout("S", new TopologyLayout(1, 1, 1, 1, 1)).Error.Code);

        _repository.NextLoadError = null;
        Assert.True((await _service.LoadAsync(CancellationToken.None)).IsSuccess);
        Assert.Null(_service.LoadError);
        Assert.True(_service.PlanLayout("S", new TopologyLayout(1, 1, 1, 1, 1)).IsSuccess);
    }

    // ---------- 预览 ----------

    [Fact]
    public async Task Plan_FirstCreation_AddsEverything_NoConfirmation()
    {
        await LoadedAsync(null);

        var plan = _service.PlanLayout("老化房", new TopologyLayout(2, 1, 1, 1, 2)).Value;

        Assert.True(plan.IsFirstCreation);
        Assert.True(plan.HasChanges);
        Assert.False(plan.RequiresConfirmation);
        Assert.Equal(plan.Candidate.NodeCount, plan.AddedCount);
        Assert.Null(plan.Base);
    }

    [Fact]
    public async Task Plan_Grow_KeepsIds_AndConfirmsNewEnabledPositions()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);
        await LoadedAsync(current);

        var plan = _service.PlanLayout("老化房", new TopologyLayout(2, 1, 1, 1, 2)).Value;

        Assert.Same(current, plan.Base);
        Assert.Equal(6, plan.AddedCount);
        Assert.Equal(0, plan.ChangedCount);
        Assert.Equal(current.Root.Id, plan.Candidate.Root.Id);

        // 统一布局补出的位置默认启用：已有设备上新增可用位置必须确认，不能悄悄放行
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(6, plan.AddedEnabledCount);
        Assert.Equal(2, plan.AddedEnabledProductCount);
        Assert.Equal([TopologyAddress.Of(2)], plan.AddedEnabledRoots);
        Assert.Contains("按统一布局修改", plan.Operation);
    }

    [Fact]
    public async Task Plan_Shrink_RequiresConfirmation_AndListsOnlyTopmostRemovedPositions()
    {
        await LoadedAsync(FakeTopologyRepository.Layout(3, 1, 1, 1, 2));

        var plan = _service.PlanLayout("老化房", new TopologyLayout(1, 1, 1, 1, 1)).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(13, plan.RemovedCount); // 2、3 号柜各 6 个节点 + 1 号柜的 P02
        Assert.Equal(5, plan.RemovedProductCount);
        Assert.Equal(
            [TopologyAddress.Of(1, 1, 1, 1, 2), TopologyAddress.Of(2), TopologyAddress.Of(3)],
            plan.RemovedRoots);
    }

    [Fact]
    public async Task Plan_Unchanged_HasNoChanges()
    {
        await LoadedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 2));

        var plan = _service.PlanLayout("老化房", new TopologyLayout(2, 1, 1, 1, 2)).Value;

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public async Task Plan_RenameSystem_CountsAsOneChange()
    {
        await LoadedAsync(FakeTopologyRepository.Layout(2, 1, 1, 1, 2));

        var plan = _service.PlanLayout("老化房 2#", new TopologyLayout(2, 1, 1, 1, 2)).Value;

        Assert.True(plan.HasChanges);
        Assert.Equal(1, plan.ChangedCount);
        Assert.Equal(0, plan.AddedCount + plan.RemovedCount);
    }

    [Fact]
    public async Task Plan_InvalidInput_Fails()
    {
        await LoadedAsync(null);

        Assert.Equal(TopologyErrors.InvalidLayoutCode, _service.PlanLayout("S", new TopologyLayout(0, 1, 1, 1, 1)).Error.Code);
        Assert.Equal(TopologyErrors.InvalidDefinitionCode, _service.PlanLayout("  ", new TopologyLayout(1, 1, 1, 1, 1)).Error.Code);
    }

    // ---------- 单节点修改的预览 ----------

    [Fact]
    public async Task PlanNodeEdit_Disable_CountsAffectedPositions_NoConfirmation()
    {
        var current = FakeTopologyRepository.Layout(2, 1, 1, 1, 3);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(2)).Value;

        var plan = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit("B 柜", Enabled: false)).Value;

        Assert.Equal(1, plan.ChangedCount);
        Assert.Equal(7, plan.NewlyDisabledCount); // 柜体 + 层位 + 槽位 + 治具位 + 3 个产品位
        Assert.Equal(0, plan.ReenabledCount);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanNodeEdit_Reenable_RequiresConfirmation_AndListsTopmostPosition()
    {
        var current = FakeTopologyRepository.Layout(2, 1, 1, 1, 3);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(2)).Value;
        var disabled = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, Enabled: false)).Value;
        Assert.True((await _service.SaveAsync(disabled, CancellationToken.None)).IsSuccess);

        var plan = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, Enabled: true)).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(7, plan.ReenabledCount);
        Assert.Equal(3, plan.ReenabledProductCount);
        Assert.Equal([TopologyAddress.Of(2)], plan.ReenabledRoots);
    }

    [Fact]
    public async Task PlanNodeEdit_Rename_UnderDisabledParent_IsNotAReenable()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(1)).Value;
        Assert.True((await _service.SaveAsync(
            _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, false)).Value, CancellationToken.None)).IsSuccess);
        var layer = current.GetNode(TopologyAddress.Of(1, 1)).Value;

        // 上级仍停用：下级自身启用标志不变、只改名，不会解除任何屏蔽
        var plan = _service.PlanNodeEdit(layer.Id, new TopologyNodeEdit("高温层", true)).Value;

        Assert.False(plan.RequiresConfirmation);
        Assert.Equal(0, plan.ReenabledCount);
    }

    [Fact]
    public async Task PlanNodeEdit_Unchanged_HasNoChanges()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var layer = current.GetNode(TopologyAddress.Of(1, 2)).Value;

        var plan = _service.PlanNodeEdit(layer.Id, new TopologyNodeEdit("  ", true, Ordinal: 2, ChildCount: 1)).Value;

        Assert.False(plan.HasChanges);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanNodeEdit_GrowChildren_NewEnabledPositions_RequireConfirmation()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(1)).Value;

        var plan = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, true, Ordinal: 1, ChildCount: 4)).Value;

        Assert.Equal(8, plan.AddedCount); // 2 个层位，各带 1 槽 1 治具位 1 产品位
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(8, plan.AddedEnabledCount);
        Assert.Equal(2, plan.AddedEnabledProductCount);
        Assert.Equal([TopologyAddress.Of(1, 3), TopologyAddress.Of(1, 4)], plan.AddedEnabledRoots);
        Assert.Contains($"柜体 C01 [{cabinet.Id}]", plan.Operation);
        Assert.Contains("下级数量 2 → 4", plan.Operation);
    }

    [Fact]
    public async Task PlanNodeEdit_GrowUnderDisabledNode_NeedsNoConfirmation()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(1)).Value;
        Assert.True((await _service.SaveAsync(
            _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, false)).Value, CancellationToken.None)).IsSuccess);

        var plan = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, false, Ordinal: 1, ChildCount: 3)).Value;

        Assert.Equal(8, plan.AddedCount);
        Assert.Equal(0, plan.AddedEnabledCount);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanNodeEdit_ShrinkChildren_RequiresConfirmation_AndListsRemoved()
    {
        var current = FakeTopologyRepository.Layout(1, 3, 1, 1, 2);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(1)).Value;

        var plan = _service.PlanNodeEdit(cabinet.Id, new TopologyNodeEdit(null, true, Ordinal: 1, ChildCount: 1)).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(10, plan.RemovedCount);
        Assert.Equal(4, plan.RemovedProductCount);
        Assert.Equal([TopologyAddress.Of(1, 2), TopologyAddress.Of(1, 3)], plan.RemovedRoots);
    }

    [Fact]
    public async Task PlanNodeEdit_Renumber_RequiresConfirmation_AndListsOldAndNewAddress()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 2);
        await LoadedAsync(current);
        var layer = current.GetNode(TopologyAddress.Of(1, 2)).Value;

        var plan = _service.PlanNodeEdit(layer.Id, new TopologyNodeEdit(null, true, Ordinal: 5, ChildCount: 1)).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(5, plan.RenumberedCount); // 层位 + 槽位 + 治具位 + 2 个产品位
        Assert.Equal(5, plan.ChangedCount);
        Assert.Equal(0, plan.AddedCount + plan.RemovedCount);
        var change = Assert.Single(plan.RenumberedRoots);
        Assert.Equal(TopologyAddress.Of(1, 2), change.From);
        Assert.Equal(TopologyAddress.Of(1, 5), change.To);
        Assert.Equal("C01-L02 → C01-L05", change.ToString());
    }

    [Fact]
    public async Task PlanNodeEdit_AllChangesTogether_MakeOnePlan()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var layer = current.GetNode(TopologyAddress.Of(1, 2)).Value;

        var plan = _service.PlanNodeEdit(layer.Id, new TopologyNodeEdit("高温层", true, Ordinal: 5, ChildCount: 3)).Value;

        var edited = plan.Candidate.GetNode(layer.Id).Value;
        Assert.Equal((TopologyAddress.Of(1, 5), "高温层", 3), (edited.Address, edited.Name, edited.Children.Count));
        Assert.Equal(6, plan.AddedCount);
        Assert.Equal(TopologyAddress.Of(1, 5), Assert.Single(plan.RenumberedRoots).To);
        Assert.Contains("名称改为「高温层」，序号 2 → 5，下级数量 1 → 3", plan.Operation);
    }

    [Fact]
    public async Task PlanNodeEdit_FailingStep_FailsWholeEdit()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var layer = current.GetNode(TopologyAddress.Of(1, 1)).Value;

        // 改名本身有效，但序号 2 已被占用：整个修改失败，不会只改一半
        var result = _service.PlanNodeEdit(layer.Id, new TopologyNodeEdit("高温层", true, Ordinal: 2, ChildCount: 1));

        Assert.Equal(TopologyErrors.InvalidEditCode, result.Error.Code);
        Assert.Same(current, _service.Current);
    }

    [Fact]
    public async Task PlanNodeEdit_RootWithoutName_Fails()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await LoadedAsync(current);

        var result = _service.PlanNodeEdit(current.Root.Id, new TopologyNodeEdit("  ", true, ChildCount: 1));

        Assert.Equal(TopologyErrors.InvalidDefinitionCode, result.Error.Code);
    }

    [Fact]
    public async Task PlanRemove_RequiresConfirmation_AndListsRemovedSubtree()
    {
        var current = FakeTopologyRepository.Layout(1, 3, 1, 1, 2);
        await LoadedAsync(current);
        var layer = current.GetNode(TopologyAddress.Of(1, 2)).Value;

        var plan = _service.PlanRemove(layer.Id).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(5, plan.RemovedCount);
        Assert.Equal(2, plan.RemovedProductCount);
        Assert.Equal([TopologyAddress.Of(1, 2)], plan.RemovedRoots);
        Assert.Equal(0, plan.RenumberedCount); // 删除中间的层位不会让后面的层位重新编号
        Assert.Contains("删除 层位 C01-L02", plan.Operation);
    }

    [Fact]
    public async Task PlanCopy_OfEnabledNode_RequiresConfirmation()
    {
        var current = FakeTopologyRepository.Layout(2, 1, 1, 1, 3);
        await LoadedAsync(current);

        var plan = _service.PlanCopy(current.GetNode(TopologyAddress.Of(1)).Value.Id).Value;

        Assert.Equal(7, plan.AddedCount);
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal([TopologyAddress.Of(3)], plan.AddedEnabledRoots);
    }

    [Fact]
    public async Task PlanEdits_InvalidOrBlocked_Fail()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(null);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, _service.PlanNodeEdit(TopologyNodeId.New(), new TopologyNodeEdit("X", true)).Error.Code);

        await LoadedAsync(current);
        Assert.Equal(TopologyErrors.InvalidEditCode, _service.PlanRemove(current.Root.Id).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, _service.PlanCopy(TopologyNodeId.New()).Error.Code);

        _repository.NextLoadError = FakeTopologyRepository.CorruptError;
        await _service.LoadAsync(CancellationToken.None);
        Assert.Equal(TopologyErrors.SaveBlockedCode, _service.PlanNodeEdit(current.Root.Id, new TopologyNodeEdit("X", true)).Error.Code);
        Assert.Equal(TopologyErrors.SaveBlockedCode, _service.PlanRemove(current.Root.Id).Error.Code);
    }

    [Fact]
    public async Task Edit_Saved_ThenNextPlanUsesNewBase()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await LoadedAsync(current);
        var grow = _service.PlanNodeEdit(current.Root.Id, new TopologyNodeEdit("老化房", true, ChildCount: 2)).Value;
        Assert.True((await _service.SaveAsync(grow, CancellationToken.None)).IsSuccess);

        var remove = _service.PlanRemove(grow.Candidate.GetNode(TopologyAddress.Of(2)).Value.Id).Value;

        Assert.Same(grow.Candidate, remove.Base);
        Assert.True((await _service.SaveAsync(remove, CancellationToken.None)).IsSuccess);
        Assert.Equal(current.NodeCount, _service.Current!.NodeCount);
        Assert.Equal(2, _repository.Saves.Count);
    }

    [Fact]
    public async Task PlanChildLayout_OfNode_ConfirmsRemovals_AndDescribesLayout()
    {
        var current = FakeTopologyRepository.Layout(2, 2, 1, 1, 1);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(2)).Value;

        var plan = _service.PlanChildLayout(cabinet.Id, [1, 1, 1, 2]).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal([TopologyAddress.Of(2, 2)], plan.RemovedRoots);
        Assert.Equal(4, plan.RemovedCount);
        Assert.Equal(1, plan.AddedEnabledCount); // C02-L01-S01-F01-P02
        Assert.Contains($"按布局重建 柜体 C02 [{cabinet.Id}] 的下级：1 × 1 × 1 × 2", plan.Operation);
    }

    [Fact]
    public async Task PlanChildLayout_SameLayout_HasNoChanges()
    {
        var current = FakeTopologyRepository.Layout(2, 2, 1, 1, 1);
        await LoadedAsync(current);

        var plan = _service.PlanChildLayout(current.Root.Id, [2, 2, 1, 1, 1]).Value;

        Assert.False(plan.HasChanges);
    }

    // ---------- 保存 ----------

    [Fact]
    public async Task Save_PersistsAndReplacesCurrent()
    {
        await LoadedAsync(null);
        var plan = _service.PlanLayout("老化房", new TopologyLayout(1, 1, 1, 1, 2)).Value;

        var saved = await _service.SaveAsync(plan, CancellationToken.None);

        Assert.True(saved.IsSuccess);
        Assert.Same(plan.Candidate, _service.Current);
        Assert.Same(plan.Candidate, Assert.Single(_repository.Saves));
    }

    [Fact]
    public async Task Save_StalePlan_IsRejected()
    {
        await LoadedAsync(FakeTopologyRepository.Layout(1, 1, 1, 1, 1));
        var first = _service.PlanLayout("S", new TopologyLayout(2, 1, 1, 1, 1)).Value;
        var second = _service.PlanLayout("S", new TopologyLayout(3, 1, 1, 1, 1)).Value;
        Assert.True((await _service.SaveAsync(first, CancellationToken.None)).IsSuccess);

        var result = await _service.SaveAsync(second, CancellationToken.None);

        Assert.Equal(TopologyErrors.StalePlanCode, result.Error.Code);
        Assert.Same(first.Candidate, _service.Current);
        Assert.Single(_repository.Saves);
    }

    [Fact]
    public async Task Save_StorageFailure_KeepsCurrent()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 1);
        await LoadedAsync(current);
        var plan = _service.PlanLayout("S", new TopologyLayout(2, 1, 1, 1, 1)).Value;
        _repository.NextSaveError = new Error(TopologyErrors.StorageFailedCode, "磁盘满");

        var result = await _service.SaveAsync(plan, CancellationToken.None);

        Assert.Equal(TopologyErrors.StorageFailedCode, result.Error.Code);
        Assert.Same(current, _service.Current);
    }
}
