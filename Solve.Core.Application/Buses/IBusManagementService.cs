using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 总线管理用例服务接口：统一协调总线配置持久化（SQLite）与内存运行时注册表（BusManager）的双写生命周期。
/// </summary>
public interface IBusManagementService
{
    /// <summary>
    /// 获取当前所有已注册的总线运行时快照
    /// </summary>
    IReadOnlyList<BusRuntime> GetAllRuntimes();

    /// <summary>
    /// 订阅总线集合变动（新增 / 删除 / 重配置），并原子地取得订阅时刻已注册的总线快照。
    /// 每个订阅者独立排队，用完 Dispose 退订。
    /// </summary>
    EventSubscription<BusRegistryEvent> SubscribeRegistry(out IReadOnlyList<BusRuntime> current);

    /// <summary>
    /// 按 Id 获取已注册的总线运行时
    /// </summary>
    Result<BusRuntime> GetRuntime(BusId id);

    /// <summary>
    /// 从持久化仓储查询指定总线描述
    /// </summary>
    Task<Result<BusDescriptor?>> GetDescriptorAsync(BusId id, CancellationToken cancellationToken);

    /// <summary>
    /// 创建并持久化一条新总线，随后动态注册到运行时内核（初始为 Disconnected 待命）。
    /// </summary>
    Task<Result<BusRuntime>> CreateBusAsync(BusDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// 修改总线配置：校验合法性与物理资源排他，落盘更新数据库，并触发运行时热更或物理通道安全重构。
    /// </summary>
    Task<Result> UpdateBusAsync(BusDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// 删除总线：先从运行时安全注销并断开释放底层硬件，再从持久化库中删除。
    /// </summary>
    Task<Result> DeleteBusAsync(BusId id, CancellationToken cancellationToken);
}
