using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Domain.Devices;

/// <summary>
/// 一次批量添加的请求：用同一个驱动，从起始地址开始按连续地址添加若干个模块。
/// </summary>
/// <param name="Name">模块名称。只添加 1 个时原样使用；多个时追加地址后缀，如"电源-03"，同一总线上地址唯一，名称也就不重复。</param>
public sealed record DeviceBatchRequest(BusId BusId, DriverId Driver, string Name, int StartAddress, int Count)
{
    public string? Remark { get; init; }

    /// <summary>
    /// 模块参数，同一批的每个模块都使用这组值
    /// </summary>
    public ParameterValues Parameters { get; init; } = ParameterValues.Empty;

    /// <summary>
    /// 通道配置，同一批的每个模块都使用这组设置
    /// </summary>
    public ModuleChannelConfig Channels { get; init; } = ModuleChannelConfig.Empty;
}

/// <summary>
/// 把批量请求展开成逐个模块的描述（纯计算，不访问存储）。
/// </summary>
public static class DeviceBatch
{
    /// <summary>
    /// 校验请求并按驱动的地址范围展开。地址是否已被同总线其他模块占用不在这里判断（需要查存储）。
    /// </summary>
    public static Result<IReadOnlyList<DeviceDescriptor>> Expand(DeviceBatchRequest request, DriverDescriptor driver)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(driver);

        if (request.Driver != driver.Id)
            throw new ArgumentException($"请求的驱动 {request.Driver} 与传入的驱动 {driver.Id} 不一致。", nameof(driver));

        var errors = new List<Error>();

        if (request.Count < 1 || request.Count > driver.AddressCapacity)
            errors.Add(DeviceErrors.InvalidField("Count", $"数量必须在 [1, {driver.AddressCapacity}] 之间，当前为 {request.Count}。"));

        if (!driver.AcceptsAddress(request.StartAddress))
        {
            errors.Add(DeviceErrors.InvalidField("StartAddress",
                $"{driver.Name}的地址必须在 [{driver.MinAddress}, {driver.MaxAddress}] 之间，当前起始地址为 {request.StartAddress}。"));
        }
        else if (errors.Count == 0)
        {
            // 用 long 计算末地址，超大数量不会溢出成看似合法的值
            var last = (long)request.StartAddress + request.Count - 1;
            if (last > driver.MaxAddress)
            {
                errors.Add(DeviceErrors.InvalidField("Count",
                    $"从地址 {request.StartAddress} 起添加 {request.Count} 个会超出{driver.Name}的地址上限 {driver.MaxAddress}，最多还能添加 {driver.MaxAddress - request.StartAddress + 1} 个。"));
            }
        }

        if (errors.Count > 0)
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(DeviceErrors.InvalidDescriptor(errors));

        var name = request.Name?.Trim() ?? string.Empty;
        var remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim();
        var devices = new List<DeviceDescriptor>(request.Count);
        for (var i = 0; i < request.Count; i++)
        {
            var address = request.StartAddress + i;
            var deviceName = request.Count == 1 ? name : $"{name}-{address:D2}";
            var device = new DeviceDescriptor(DeviceId.New(), request.BusId, request.Driver, deviceName, address)
            {
                Remark = remark,
                Parameters = request.Parameters,
                Channels = request.Channels,
            };

            // 同一批的名称规则相同，名称过长等错误每个模块都会重复报，只报一次
            foreach (var error in device.Validate())
            {
                if (!errors.Exists(e => e.Code == error.Code))
                    errors.Add(error);
            }

            devices.Add(device);
        }

        return errors.Count > 0
            ? Result<IReadOnlyList<DeviceDescriptor>>.Failure(DeviceErrors.InvalidDescriptor(errors))
            : Result<IReadOnlyList<DeviceDescriptor>>.Success(devices);
    }
}
