using Microsoft.Extensions.Logging;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 总线挂载模块用例服务：按驱动校验地址范围与模块参数、展开批量请求，无效输入不触达存储，
/// 每次增删改留下操作记录，并在成功后重新绑定该总线的模块与驱动。
/// </summary>
/// <remarks>
/// 批量添加前先按已有模块列出冲突地址，便于操作员一次改好；真正的唯一性仍由仓储在同一事务内保证，
/// 预检与写入之间被并发占用时，写入整体失败、不留半批数据。
/// </remarks>
public sealed class DeviceService : IDeviceService
{
    private readonly IDeviceRepository _repository;
    private readonly ILogger _logger;

    public DeviceService(
        IDeviceRepository repository,
        IDriverCatalog drivers,
        IDeviceBindings bindings,
        ILogger<DeviceService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        Drivers = drivers;
        Bindings = bindings;
        _logger = logger;
    }

    public IDriverCatalog Drivers { get; }

    public IDeviceBindings Bindings { get; }

    public Task<Result<IReadOnlyList<DeviceDescriptor>>> ListAsync(BusId busId, CancellationToken cancellationToken)
        => _repository.ListByBusAsync(busId, cancellationToken);

    public Task<Result<int>> CountAsync(BusId busId, CancellationToken cancellationToken)
        => _repository.CountByBusAsync(busId, cancellationToken);

    public async Task<Result<IReadOnlyList<DeviceDescriptor>>> AddBatchAsync(
        DeviceBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Drivers.TryGet(request.Driver, out var driver))
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(DeviceErrors.UnknownDriver(request.Driver));

        var parameters = NormalizeParameters(request.Driver, request.Parameters, allowUnverified: false);
        if (parameters.IsFailure)
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(parameters.Error);

        var expanded = DeviceBatch.Expand(request with { Parameters = parameters.Value }, driver);
        if (expanded.IsFailure)
            return expanded;

        var existing = await _repository.ListByBusAsync(request.BusId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(existing.Error);

        var used = existing.Value.Select(d => d.Address).ToHashSet();
        var conflicts = expanded.Value.Select(d => d.Address).Where(used.Contains).ToList();
        if (conflicts.Count > 0)
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(DeviceErrors.AddressConflict(conflicts));

        var added = await _repository.AddRangeAsync(expanded.Value, cancellationToken).ConfigureAwait(false);
        if (added.IsFailure)
        {
            _logger.LogWarning("总线 {BusId} 添加 {Count} 个模块（驱动 {Driver}，起始地址 {Start}）失败：{Error}",
                request.BusId, request.Count, driver.Name, request.StartAddress, added.Error);
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(added.Error);
        }

        _logger.LogInformation("总线 {BusId} 添加 {Count} 个模块（驱动 {Driver}，参数 {Parameters}），地址 {Start}–{End}",
            request.BusId, request.Count, driver.Name, parameters.Value, request.StartAddress, request.StartAddress + request.Count - 1);

        await Bindings.RefreshAsync(request.BusId, cancellationToken).ConfigureAwait(false);
        return expanded;
    }

    public async Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var errors = device.Validate();
        if (errors.Count > 0)
            return Result.Failure(DeviceErrors.InvalidDescriptor(errors));

        // 驱动已从程序中移除时只校验绝对范围：仍允许改名、改备注，不让历史记录变成无法编辑。
        // 界面上只能换成目录里的驱动，未知驱动只可能是原本就在用的那个
        if (Drivers.TryGet(device.Driver, out var driver) && !driver.AcceptsAddress(device.Address))
        {
            return Result.Failure(DeviceErrors.InvalidField("Address",
                $"{driver.Name}的地址必须在 [{driver.MinAddress}, {driver.MaxAddress}] 之间，当前为 {device.Address}。"));
        }

        // 驱动未加载时无法核对参数声明，原样保留已保存的值（同样是为了不让历史记录无法编辑）
        var parameters = NormalizeParameters(device.Driver, device.Parameters, allowUnverified: true);
        if (parameters.IsFailure)
            return Result.Failure(parameters.Error);

        var normalized = device with { Parameters = parameters.Value };
        var saved = await _repository.UpdateAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            _logger.LogWarning("修改{Device}失败：{Error}", device, saved.Error);
            return saved;
        }

        await Bindings.RefreshAsync(device.BusId, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<Result<int>> DeleteAsync(IReadOnlyList<DeviceDescriptor> devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
            return Result<int>.Success(0);

        var deleted = await _repository.DeleteRangeAsync(devices.Select(d => d.Id).ToList(), cancellationToken).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            _logger.LogWarning("删除 {Count} 个模块失败：{Error}", devices.Count, deleted.Error);
            return deleted;
        }

        foreach (var device in devices)
            _logger.LogInformation("总线 {BusId} 删除{Device}", device.BusId, device);

        if (deleted.Value < devices.Count)
            _logger.LogInformation("请求删除 {Requested} 个模块，其中 {Missing} 个已不存在", devices.Count, devices.Count - deleted.Value);

        foreach (var busId in devices.Select(d => d.BusId).Distinct())
            await Bindings.RefreshAsync(busId, cancellationToken).ConfigureAwait(false);

        return deleted;
    }

    /// <summary>
    /// 有驱动实现时按其声明校验并精简参数；没有实现（通用模块、驱动未加载）时不应有参数，
    /// 只有 <paramref name="allowUnverified"/> 为 true（修改已有模块）时原样保留
    /// </summary>
    private Result<ParameterValues> NormalizeParameters(Domain.Drivers.DriverId driverId, ParameterValues values, bool allowUnverified)
    {
        if (Drivers.TryGetImplementation(driverId, out var implementation))
            return DriverParameters.Normalize(implementation.Info, values);

        return values.Count == 0 || allowUnverified
            ? Result<ParameterValues>.Success(values)
            : Result<ParameterValues>.Failure(DeviceErrors.InvalidField("Parameters", "该驱动没有可配置的参数。"));
    }
}
