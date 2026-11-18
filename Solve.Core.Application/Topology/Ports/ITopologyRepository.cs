using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology.Ports;

/// <summary>
/// 设备拓扑存储出站端口。整棵树作为一个快照读写：拓扑是不可变对象，结构变更即整体替换。
/// </summary>
/// <remarks>
/// <para>节点 Id 只在首次建档时生成，之后必须通过本端口原样保存与读回；这是"系统按 Id 识别节点"成立的前提。</para>
/// <para>实现可能执行同步磁盘 I/O（Microsoft.Data.Sqlite 的 Async 方法实际为同步实现），
/// 调用方不要在 UI 线程或实时控制线程上直接 await。存储层面的失败返回失败的 Result，不抛异常。</para>
/// </remarks>
public interface ITopologyRepository
{
    /// <summary>
    /// 读取并完整校验已保存的拓扑。尚未建档返回 Success(null)；
    /// 任何一处数据损坏返回 Topology.Storage.Corrupt，整体拒绝，不加载残缺的树。
    /// </summary>
    Task<Result<EquipmentTopology?>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 以快照方式整体保存（单事务）：新增、修改、删除的节点一次提交，失败时库内保持原快照不变。
    /// 已有节点保留创建时间，内容未变的节点不刷新修改时间。
    /// </summary>
    Task<Result> SaveAsync(EquipmentTopology topology, CancellationToken cancellationToken);
}
