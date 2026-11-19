using Microsoft.Extensions.Logging;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 总线挂载设备用例服务：保存前做领域校验（无效输入不触达存储），并为每次增删改留下操作记录。
/// </summary>
/// <remarks>
/// 地址唯一、所属总线存在等跨记录约束由仓储在同一条语句内保证，这里不做"先查后写"的预检，避免并发下的检查失效。
/// </remarks>
public sealed class DeviceService : IDeviceService
{
    private readonly IDeviceRepository _repository;
    private readonly ILogger _logger;

    public DeviceService(IDeviceRepository repository, ILogger<DeviceService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public Task<Result<IReadOnlyList<DeviceDescriptor>>> ListAsync(BusId busId, CancellationToken cancellationToken)
        => _repository.ListByBusAsync(busId, cancellationToken);

    public Task<Result<int>> CountAsync(BusId busId, CancellationToken cancellationToken)
        => _repository.CountByBusAsync(busId, cancellationToken);

    public Task<Result> AddAsync(DeviceDescriptor device, CancellationToken cancellationToken)
        => SaveAsync("新增", device, _repository.AddAsync, cancellationToken);

    public Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken)
        => SaveAsync("修改", device, _repository.UpdateAsync, cancellationToken);

    public async Task<Result> DeleteAsync(DeviceDescriptor device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var deleted = await _repository.DeleteAsync(device.Id, cancellationToken).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            _logger.LogWarning("删除{Device}失败：{Error}", device, deleted.Error);
            return deleted;
        }

        _logger.LogInformation("总线 {BusId} 删除{Device}", device.BusId, device);
        return deleted;
    }

    private async Task<Result> SaveAsync(
        string operation,
        DeviceDescriptor device,
        Func<DeviceDescriptor, CancellationToken, Task<Result>> save,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var errors = device.Validate();
        if (errors.Count > 0)
            return Result.Failure(DeviceErrors.InvalidDescriptor(errors));

        var saved = await save(device, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
            _logger.LogWarning("{Operation}{Device}失败：{Error}", operation, device, saved.Error);

        return saved;
    }
}
