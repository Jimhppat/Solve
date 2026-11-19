using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology;

/// <summary>
/// 设备拓扑用例服务：持有当前生效的拓扑，负责启动加载、按布局预览变更与保存。
/// </summary>
/// <remarks>
/// <para>保存分两步：Plan* 生成预览（会删除、启用、重新编号哪些位置）→ 需要时操作员确认 → <see cref="SaveAsync"/>。
/// 预览期间拓扑若被其他保存改动，保存会以 Topology.StalePlan 拒绝，避免基于过期预览覆盖。</para>
/// <para>读取失败（库损坏或存储故障）后禁止保存，防止用新拓扑覆盖待人工修复的数据，同时丢失节点的原有 Id。</para>
/// </remarks>
public interface ITopologyService
{
    /// <summary>
    /// 当前生效的拓扑；尚未建档或读取失败时为 null。可在任意线程读取。
    /// </summary>
    EquipmentTopology? Current { get; }

    /// <summary>
    /// 最近一次读取失败的原因；读取成功（含尚未建档）时为 null
    /// </summary>
    Error? LoadError { get; }

    /// <summary>
    /// 从配置库读取拓扑（启动时调用，读取失败后也可再次调用重试）。磁盘 I/O 在线程池上执行。
    /// </summary>
    Task<Result> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按统一布局生成变更预览：同一位置编码沿用现有节点的 Id、名称与启用状态。纯计算，不写库；
    /// 大型拓扑（十万级节点）耗时数百毫秒，界面应在线程池上调用。
    /// </summary>
    Result<TopologyChangePlan> PlanLayout(string systemName, TopologyLayout layout);

    // 以下单节点编辑：纯计算、不写库，需要重建整棵拓扑，大型拓扑耗时数百毫秒，界面应在线程池上调用。

    /// <summary>
    /// 一键拓扑：按统一布局重建节点的下级（对系统根即整机），按位置编码对齐，同一位置保留原 Id、名称与启用状态。
    /// 会抹平该节点下的个性化结构，删除或新增可用位置时保存前要求确认。
    /// </summary>
    /// <param name="counts">节点下面每一层每个节点的下级数，一直到产品位</param>
    Result<TopologyChangePlan> PlanChildLayout(TopologyNodeId id, IReadOnlyList<int> counts);

    /// <summary>
    /// 一次修改一个节点的名称、启用状态、序号与下级数量，合成一份变更预览，保存时只需确认一次。
    /// </summary>
    Result<TopologyChangePlan> PlanNodeEdit(TopologyNodeId id, TopologyNodeEdit edit);

    /// <summary>
    /// 删除节点及其整棵子树（保存前要求确认）。系统根不能删除。
    /// </summary>
    Result<TopologyChangePlan> PlanRemove(TopologyNodeId id);

    /// <summary>
    /// 把节点连同子树复制为一个同级节点（全部新 Id），放在现有最大序号之后。
    /// </summary>
    Result<TopologyChangePlan> PlanCopy(TopologyNodeId id);

    /// <summary>
    /// 保存预览中的拓扑并使其生效。多次调用串行执行；磁盘 I/O 在线程池上执行。
    /// </summary>
    Task<Result> SaveAsync(TopologyChangePlan plan, CancellationToken cancellationToken);
}
