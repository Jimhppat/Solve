using Microsoft.Extensions.Logging;
using Solve.Core.Application.Topology.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology;

/// <inheritdoc cref="ITopologyService"/>
public sealed class TopologyService : ITopologyService
{
    private readonly ITopologyRepository _repository;
    private readonly ILogger _logger;

    // 读取与保存串行执行：保存时"预览基准仍是当前拓扑"的判断与写库、替换必须是一个整体
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // 不可变快照整体替换，读方无锁；volatile 保证其他线程立即看到新快照
    private volatile EquipmentTopology? _current;
    private volatile Error? _loadError;

    public TopologyService(ITopologyRepository repository, ILogger<TopologyService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public EquipmentTopology? Current => _current;

    public Error? LoadError => _loadError;

    public async Task<Result> LoadAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 仓储的 Async 方法内部是同步磁盘 I/O，必须离开调用方（可能是 UI）线程
            var loaded = await Task.Run(() => _repository.LoadAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                // 读取失败时不保留任何拓扑：残缺或过期的孪生比没有更危险
                _current = null;
                _loadError = loaded.Error;
                _logger.LogError("设备拓扑读取失败，拓扑功能不可用且禁止保存：{Error}", loaded.Error);
                return Result.Failure(loaded.Error);
            }

            _current = loaded.Value;
            _loadError = null;
            return Result.Success();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Result<TopologyChangePlan> PlanLayout(string systemName, TopologyLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_loadError is { } loadError)
            return Result<TopologyChangePlan>.Failure(TopologyErrors.SaveBlocked(loadError));

        var current = _current;
        var definition = layout.ToDefinition(systemName, current);
        if (definition.IsFailure)
            return Result<TopologyChangePlan>.Failure(definition.Error);

        var operation = $"{(current is null ? "按统一布局建档" : "按统一布局修改")}「{systemName}」：" +
                        $"柜体 {layout.Cabinets} × 层位 {layout.LayersPerCabinet} × 槽位 {layout.SlotsPerLayer} × " +
                        $"治具位 {layout.FixturesPerSlot} × 产品位 {layout.ProductsPerFixture}";
        var candidate = EquipmentTopology.Create(definition.Value);
        return candidate.IsSuccess
            ? Result<TopologyChangePlan>.Success(TopologyChangePlan.Compare(current, candidate.Value, operation))
            : Result<TopologyChangePlan>.Failure(candidate.Error);
    }

    public Result<TopologyChangePlan> PlanNodeSettings(TopologyNodeId id, string? name, bool enabled)
        => PlanEdit(id,
            topology => topology.WithNodeSettings(id, name, enabled),
            node => $"设置 {node}：名称 {(name is null ? "（未命名）" : $"「{name}」")}，{(enabled ? "启用" : "停用")}");

    public Result<TopologyChangePlan> PlanAddChildren(TopologyNodeId parentId, int count, TopologyTemplate template, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(template);

        return PlanEdit(parentId,
            topology => topology.WithChildrenAdded(parentId, count, template, enabled),
            parent =>
            {
                var childLevel = parent.Level.TryGetChildLevel(out var level) ? level.DisplayName() : "下级";
                var counts = template.CountsBelow.Count == 0 ? "无下级" : string.Join(" × ", template.CountsBelow);
                return $"在 {parent} 下新增 {count} 个{childLevel}（下级模板 {counts}，{(enabled ? "直接启用" : "停用")}）";
            });
    }

    public Result<TopologyChangePlan> PlanRemove(TopologyNodeId id)
        => PlanEdit(id, topology => topology.WithNodeRemoved(id), node => $"删除 {node} 及其全部下级");

    public Result<TopologyChangePlan> PlanRenumber(TopologyNodeId id, int ordinal)
        => PlanEdit(id, topology => topology.WithOrdinal(id, ordinal), node => $"{node} 改为序号 {ordinal}");

    public Result<TopologyChangePlan> PlanCopy(TopologyNodeId id, bool enabled)
        => PlanEdit(id,
            topology => topology.WithNodeCopied(id, enabled),
            node => $"复制 {node} 为同级（{(enabled ? "直接启用" : "停用")}）");

    /// <summary>
    /// 基于当前拓扑做一次编辑并生成预览。操作说明在编辑前按原节点生成：删除、重新编号后节点已不在原位置。
    /// </summary>
    private Result<TopologyChangePlan> PlanEdit(
        TopologyNodeId id,
        Func<EquipmentTopology, Result<EquipmentTopology>> edit,
        Func<TopologyNode, string> describe)
    {
        if (_loadError is { } loadError)
            return Result<TopologyChangePlan>.Failure(TopologyErrors.SaveBlocked(loadError));

        // 只读一次快照：预览的基准与编辑对象必须是同一份拓扑，保存时据此判断预览是否过期
        if (_current is not { } current || !current.TryGetNode(id, out var node))
            return Result<TopologyChangePlan>.Failure(TopologyErrors.NodeNotFound(id));

        var candidate = edit(current);
        return candidate.IsSuccess
            ? Result<TopologyChangePlan>.Success(TopologyChangePlan.Compare(current, candidate.Value, describe(node)))
            : Result<TopologyChangePlan>.Failure(candidate.Error);
    }

    public async Task<Result> SaveAsync(TopologyChangePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadError is { } loadError)
                return Result.Failure(TopologyErrors.SaveBlocked(loadError));

            if (!ReferenceEquals(plan.Base, _current))
                return Result.Failure(TopologyErrors.StalePlan());

            var saved = await Task.Run(() => _repository.SaveAsync(plan.Candidate, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (saved.IsFailure)
            {
                // 库内仍是原快照（单事务），当前拓扑保持不变
                _logger.LogError("设备拓扑保存失败，继续使用原拓扑。操作：{Operation}；原因：{Error}",
                    plan.Operation ?? "（未注明）", saved.Error);
                return saved;
            }

            _current = plan.Candidate;
            _logger.LogInformation(
                "设备拓扑已生效 {Name} [{RootId}]：{Operation}；{NodeCount} 个节点（新增 {Added}，修改 {Changed}，删除 {Removed}，重新编号 {Renumbered}）",
                plan.Candidate.Name, plan.Candidate.Root.Id, plan.Operation ?? "（未注明）", plan.Candidate.NodeCount,
                plan.AddedCount, plan.ChangedCount, plan.RemovedCount, plan.RenumberedCount);

            // 位置编码改变后现场标牌必须同步更换，否则扫码会落到别的位置：单独记一条警告，便于事后追查
            if (plan.RenumberedCount > 0)
            {
                _logger.LogWarning("设备拓扑有 {Count} 个位置的位置编码已改变：{Changes}",
                    plan.RenumberedCount, string.Join("，", plan.RenumberedRoots));
            }

            return Result.Success();
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
