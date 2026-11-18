using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 总线管理应用服务：统筹持久化（SQLite 仓储）与运行态（BusManager）的双写事务与一致性保障。
/// </summary>
public sealed class BusManagementService : IBusManagementService
{
    private readonly BusManager _manager;
    private readonly IBusConfigurationRepository _repository;
    private readonly ILogger _logger;

    public BusManagementService(
        BusManager manager,
        IBusConfigurationRepository repository,
        ILogger<BusManagementService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _repository = repository;
        _logger = logger;
    }

    public IReadOnlyList<BusRuntime> GetAllRuntimes() => _manager.Buses;

    public EventSubscription<BusRegistryEvent> SubscribeRegistry(out IReadOnlyList<BusRuntime> current)
        => _manager.SubscribeRegistry(out current);

    public Result<BusRuntime> GetRuntime(BusId id)
    {
        return _manager.TryGet(id, out var runtime)
            ? Result<BusRuntime>.Success(runtime)
            : Result<BusRuntime>.Failure(BusErrors.NotRegistered(id));
    }

    public Task<Result<BusDescriptor?>> GetDescriptorAsync(BusId id, CancellationToken cancellationToken)
        => _repository.GetByIdAsync(id, cancellationToken);

    public async Task<Result<BusRuntime>> CreateBusAsync(BusDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 1. 领域校验
        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result<BusRuntime>.Failure(BusErrors.InvalidDescriptor(errors));

        // 2. 预检运行时物理资源冲突
        if (_manager.ValidateResourceConflict(descriptor) is { } conflict)
            return Result<BusRuntime>.Failure(conflict);

        // 3. 先落盘持久化（保证现场突然断电后配置不丢）
        var saved = await _repository.SaveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
            return Result<BusRuntime>.Failure(saved.Error);

        // 4. 注册到运行时
        var registered = _manager.Register(descriptor);
        if (registered.IsFailure)
        {
            _logger.LogError("总线 {BusId} 注册到运行时失败，执行持久化回滚：{Error}", descriptor.Id, registered.Error);
            await RollbackAsync(descriptor.Id, "删除刚写入的配置",
                () => _repository.DeleteAsync(descriptor.Id, CancellationToken.None)).ConfigureAwait(false);

            return registered;
        }

        _logger.LogInformation("总线 {BusId} {Name} 创建并注册成功", descriptor.Id, descriptor.Name);
        return registered;
    }

    public async Task<Result> UpdateBusAsync(BusDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 1. 领域校验
        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result.Failure(BusErrors.InvalidDescriptor(errors));

        // 2. 检查运行时是否已注册该总线
        if (!_manager.TryGet(descriptor.Id, out _))
            return Result.Failure(BusErrors.NotRegistered(descriptor.Id));

        // 3. 预检排除自身 ID 后的资源冲突（允许修改波特率等参数而不被自身占用阻挡）
        if (_manager.ValidateResourceConflict(descriptor, excludeBusId: descriptor.Id) is { } conflict)
            return Result.Failure(conflict);

        // 4. 读取旧配置用于回滚准备
        var oldDescriptorResult = await _repository.GetByIdAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (oldDescriptorResult.IsFailure)
            return Result.Failure(oldDescriptorResult.Error);

        var oldDescriptor = oldDescriptorResult.Value;

        // 5. 先更新持久化库
        var saved = await _repository.SaveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
            return saved;

        // 6. 运行时重配置（软参数热更新或硬参数物理通道重建）
        var reconfigured = await _manager.ReconfigureAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (reconfigured.IsFailure)
        {
            _logger.LogError("总线 {BusId} 运行时重配置失败，尝试回滚数据库至旧配置：{Error}", descriptor.Id, reconfigured.Error);

            // 库里原本没有这条记录（运行时有、库里无）时，回滚即删除刚写入的记录
            await RollbackAsync(descriptor.Id, "恢复旧配置", oldDescriptor is not null
                ? () => _repository.SaveAsync(oldDescriptor, CancellationToken.None)
                : () => _repository.DeleteAsync(descriptor.Id, CancellationToken.None)).ConfigureAwait(false);

            return reconfigured;
        }

        _logger.LogInformation("总线 {BusId} {Name} 配置更新成功", descriptor.Id, descriptor.Name);
        return Result.Success();
    }

    public async Task<Result> DeleteBusAsync(BusId id, CancellationToken cancellationToken)
    {
        // 1. 检查是否存在
        if (!_manager.TryGet(id, out _))
        {
            var existsInDb = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (existsInDb.IsFailure)
                return Result.Failure(existsInDb.Error);
            if (existsInDb.Value is null)
                return Result.Failure(BusErrors.StorageNotFound(id));
        }

        // 2. 先从运行时注销（安全断开、释放句柄、终止在途事务）
        if (_manager.TryGet(id, out _))
        {
            var unregistered = await _manager.UnregisterAsync(id, cancellationToken).ConfigureAwait(false);
            if (unregistered.IsFailure)
            {
                _logger.LogWarning("从运行时注销总线 {BusId} 返回：{Error}，继续删除持久化记录", id, unregistered.Error);
            }
        }

        // 3. 从数据库中删除
        var deleted = await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (deleted.IsFailure)
            return deleted;

        _logger.LogInformation("总线 {BusId} 已彻底删除", id);
        return Result.Success();
    }

    /// <summary>
    /// 执行持久化回滚。仓储按约定以失败的 Result 报告存储错误（不抛异常），两种失败都必须告警：
    /// 回滚失败意味着数据库与运行时已不一致，下次启动会按数据库内容加载。
    /// </summary>
    private async Task RollbackAsync(BusId id, string action, Func<Task<Result>> rollback)
    {
        try
        {
            var result = await rollback().ConfigureAwait(false);
            if (result.IsFailure)
                _logger.LogCritical("总线 {BusId} 回滚（{Action}）失败：{Error}，数据库与运行时不一致，需人工核对", id, action, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "总线 {BusId} 回滚（{Action}）异常，数据库与运行时不一致，需人工核对", id, action);
        }
    }
}
