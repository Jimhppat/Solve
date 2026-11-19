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
    public async Task Plan_Grow_KeepsIds_AndNeedsNoConfirmation()
    {
        var current = FakeTopologyRepository.Layout(1, 1, 1, 1, 2);
        await LoadedAsync(current);

        var plan = _service.PlanLayout("老化房", new TopologyLayout(2, 1, 1, 1, 2)).Value;

        Assert.Same(current, plan.Base);
        Assert.Equal(6, plan.AddedCount);
        Assert.Equal(0, plan.ChangedCount);
        Assert.False(plan.RequiresConfirmation);
        Assert.Equal(current.Root.Id, plan.Candidate.Root.Id);
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
    public async Task PlanNodeSettings_Disable_CountsAffectedPositions_NoConfirmation()
    {
        var current = FakeTopologyRepository.Layout(2, 1, 1, 1, 3);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(2)).Value;

        var plan = _service.PlanNodeSettings(cabinet.Id, "B 柜", enabled: false).Value;

        Assert.Equal(1, plan.ChangedCount);
        Assert.Equal(7, plan.NewlyDisabledCount); // 柜体 + 层位 + 槽位 + 治具位 + 3 个产品位
        Assert.Equal(0, plan.ReenabledCount);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanNodeSettings_Reenable_RequiresConfirmation_AndListsTopmostPosition()
    {
        var current = FakeTopologyRepository.Layout(2, 1, 1, 1, 3);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(2)).Value;
        var disabled = _service.PlanNodeSettings(cabinet.Id, null, enabled: false).Value;
        Assert.True((await _service.SaveAsync(disabled, CancellationToken.None)).IsSuccess);

        var plan = _service.PlanNodeSettings(cabinet.Id, null, enabled: true).Value;

        Assert.True(plan.RequiresConfirmation);
        Assert.Equal(7, plan.ReenabledCount);
        Assert.Equal(3, plan.ReenabledProductCount);
        Assert.Equal([TopologyAddress.Of(2)], plan.ReenabledRoots);
    }

    [Fact]
    public async Task PlanNodeSettings_Rename_UnderDisabledParent_IsNotAReenable()
    {
        var current = FakeTopologyRepository.Layout(1, 2, 1, 1, 1);
        await LoadedAsync(current);
        var cabinet = current.GetNode(TopologyAddress.Of(1)).Value;
        Assert.True((await _service.SaveAsync(_service.PlanNodeSettings(cabinet.Id, null, false).Value, CancellationToken.None)).IsSuccess);
        var layer = current.GetNode(TopologyAddress.Of(1, 1)).Value;

        // 上级仍停用：下级自身启用标志不变、只改名，不会解除任何屏蔽
        var plan = _service.PlanNodeSettings(layer.Id, "高温层", enabled: true).Value;

        Assert.False(plan.RequiresConfirmation);
        Assert.Equal(0, plan.ReenabledCount);
    }

    [Fact]
    public async Task PlanNodeSettings_NotConfiguredOrLoadFailed_Fails()
    {
        await LoadedAsync(null);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, _service.PlanNodeSettings(TopologyNodeId.New(), "X", true).Error.Code);

        _repository.NextLoadError = FakeTopologyRepository.CorruptError;
        await _service.LoadAsync(CancellationToken.None);
        Assert.Equal(TopologyErrors.SaveBlockedCode, _service.PlanNodeSettings(TopologyNodeId.New(), "X", true).Error.Code);
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
