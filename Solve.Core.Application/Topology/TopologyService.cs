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

        var candidate = EquipmentTopology.Create(definition.Value);
        return candidate.IsSuccess
            ? Result<TopologyChangePlan>.Success(TopologyChangePlan.Compare(current, candidate.Value))
            : Result<TopologyChangePlan>.Failure(candidate.Error);
    }

    public Result<TopologyChangePlan> PlanNodeSettings(TopologyNodeId id, string? name, bool enabled)
    {
        if (_loadError is { } loadError)
            return Result<TopologyChangePlan>.Failure(TopologyErrors.SaveBlocked(loadError));

        if (_current is not { } current)
            return Result<TopologyChangePlan>.Failure(TopologyErrors.NodeNotFound(id));

        var candidate = current.WithNodeSettings(id, name, enabled);
        return candidate.IsSuccess
            ? Result<TopologyChangePlan>.Success(TopologyChangePlan.Compare(current, candidate.Value))
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
                _logger.LogError("设备拓扑保存失败，继续使用原拓扑：{Error}", saved.Error);
                return saved;
            }

            _current = plan.Candidate;
            _logger.LogInformation(
                "设备拓扑已生效 {Name} [{RootId}]：{NodeCount} 个节点（新增 {Added}，修改 {Changed}，删除 {Removed}）",
                plan.Candidate.Name, plan.Candidate.Root.Id, plan.Candidate.NodeCount,
                plan.AddedCount, plan.ChangedCount, plan.RemovedCount);
            return Result.Success();
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
