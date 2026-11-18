using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses.Ports;

/// <summary>
/// 一条无法加载的配置记录：原始行标识 + 全部错误。不会被加载为总线，交由上层告警/人工修复。
/// </summary>
public sealed record BusConfigurationIssue(string RowId, IReadOnlyList<Error> Errors);

/// <summary>
/// 一次完整加载的结果：有效描述 + 无效记录。单条坏记录不影响其他总线加载。
/// </summary>
public sealed record BusConfigurationSnapshot(
    IReadOnlyList<BusDescriptor> Descriptors,
    IReadOnlyList<BusConfigurationIssue> Issues);

/// <summary>
/// 总线配置存储出站端口。
/// </summary>
/// <remarks>
/// 实现可能执行同步磁盘 I/O（例如 Microsoft.Data.Sqlite 的 Async 方法实际为同步实现），
/// 调用方不要在 UI 线程或实时控制线程上直接 await。
/// 存储层面的失败（磁盘、锁、约束）返回失败的 Result，不抛异常。
/// </remarks>
public interface IBusConfigurationRepository
{
    /// <summary>
    /// 读取全部配置，逐条做领域校验。
    /// </summary>
    Task<Result<BusConfigurationSnapshot>> LoadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按 Id 读取单条配置并执行领域校验。记录不存在返回 Success(null)；配置损坏返回错误。
    /// </summary>
    Task<Result<BusDescriptor?>> GetByIdAsync(BusId id, CancellationToken cancellationToken);

    /// <summary>
    /// 按 Id 新增或更新。描述无效返回 Bus.InvalidDescriptor；串口名与其他记录冲突返回 Bus.Storage.Conflict。
    /// </summary>
    Task<Result> SaveAsync(BusDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// 删除。记录不存在返回 Bus.Storage.NotFound。
    /// </summary>
    Task<Result> DeleteAsync(BusId id, CancellationToken cancellationToken);
}
