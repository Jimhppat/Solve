using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Adapters.Persistence.Devices;

/// <summary>
/// 领域描述 ⇄ 数据库行的显式转换。读取时把库内数据视为不可信输入：解析错误与领域校验错误一并收集。
/// </summary>
internal static class DeviceRowMapper
{
    public static DeviceRow ToRow(DeviceDescriptor device, DateTimeOffset now)
    {
        var timestamp = now.UtcDateTime.ToString("O");
        return new DeviceRow
        {
            Id = device.Id.ToString(),
            BusId = device.BusId.Value.ToString("D"),
            Address = device.Address,
            Name = device.Name,

            // 空白备注统一存 NULL，读回后与"未填"一致
            Remark = string.IsNullOrWhiteSpace(device.Remark) ? null : device.Remark,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    /// <summary>
    /// 行 → 描述。失败时返回该行的全部问题（解析错误 + 领域校验错误）。
    /// </summary>
    public static Result<DeviceDescriptor> ToDescriptor(DeviceRow row, out IReadOnlyList<Error> problems)
    {
        var errors = new List<Error>();

        if (!DeviceId.TryParse(row.Id, out var id))
            errors.Add(DeviceErrors.StorageCorruptRow("id", $"'{row.Id}' 不是有效的 GUID"));

        if (!BusId.TryParse(row.BusId, out var busId))
            errors.Add(DeviceErrors.StorageCorruptRow("bus_id", $"'{row.BusId}' 不是有效的 GUID"));

        // 先确认在 int 范围内再收窄，避免手工改库写入的超大值截断后恰好落进合法区间
        var address = 0;
        if (row.Address is < int.MinValue or > int.MaxValue)
            errors.Add(DeviceErrors.StorageCorruptRow("address", $"{row.Address} 超出 32 位整数范围"));
        else
            address = (int)row.Address;

        var device = new DeviceDescriptor(id, busId, row.Name, address) { Remark = row.Remark };

        // 解析无误后再做领域校验：库文件可能被手工修改成越界值
        if (errors.Count == 0)
            errors.AddRange(device.Validate());

        problems = errors;
        return errors.Count == 0
            ? Result<DeviceDescriptor>.Success(device)
            : Result<DeviceDescriptor>.Failure(DeviceErrors.InvalidDescriptor(errors));
    }
}
