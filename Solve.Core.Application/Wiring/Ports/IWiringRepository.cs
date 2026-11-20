using Solve.Core.Domain.Common;
using Solve.Core.Domain.Wiring;

namespace Solve.Core.Application.Wiring.Ports;

/// <summary>
/// 接线存储出站端口。
/// </summary>
/// <remarks>
/// <para>实现可能执行同步磁盘 I/O（Microsoft.Data.Sqlite 的 Async 方法实际为同步实现），调用方不要在 UI 线程上直接 await。
/// 存储层面的失败返回失败的 Result，不抛异常。</para>
/// <para>联动由存储保证：删除模块（含删除总线级联删除的模块）时其接线随之删除；拓扑保存删除位置时，落在这些位置上的接线
/// 在同一事务内删除。</para>
/// </remarks>
public interface IWiringRepository
{
    /// <summary>
    /// 读取全部接线。无法解析的行不返回，由实现记录错误日志。
    /// </summary>
    Task<Result<IReadOnlyList<Wire>>> LoadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 在同一事务内新增一批接线：全部成功或全部不写。通道已被占用、位置或模块已不存在时返回 Wiring.Storage.Conflict。
    /// </summary>
    Task<Result> AddRangeAsync(IReadOnlyList<Wire> wires, CancellationToken cancellationToken);

    /// <summary>
    /// 修改用途与线号（落点、通道、功能不可变）。不存在时返回 Wiring.Storage.NotFound。
    /// </summary>
    Task<Result> UpdateAsync(Wire wire, CancellationToken cancellationToken);

    /// <summary>
    /// 在同一事务内拆除一批接线，返回实际删除的条数（已不存在的不计入，也不算失败）。
    /// </summary>
    Task<Result<int>> DeleteRangeAsync(IReadOnlyList<WireId> ids, CancellationToken cancellationToken);
}
