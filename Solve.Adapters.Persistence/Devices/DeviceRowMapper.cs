using System.Text.Json;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

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
            DriverId = device.Driver.ToString(),
            Address = device.Address,
            Name = device.Name,

            // 空白备注统一存 NULL，读回后与"未填"一致
            Remark = string.IsNullOrWhiteSpace(device.Remark) ? null : device.Remark,

            // ParameterValues 按标识排序，序列化结果稳定，便于直接查库比对
            Parameters = JsonSerializer.Serialize(device.Parameters.ToDictionary(p => p.Key, p => p.Value)),
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

        if (!DriverId.TryParse(row.DriverId, out var driver))
            errors.Add(DeviceErrors.StorageCorruptRow("driver_id", $"'{row.DriverId}' 不是有效的驱动标识"));

        var parameters = ParseParameters(row.Parameters, errors);

        var device = new DeviceDescriptor(id, busId, driver, row.Name, address)
        {
            Remark = row.Remark,
            Parameters = parameters,
        };

        // 解析无误后再做领域校验：库文件可能被手工修改成越界值
        if (errors.Count == 0)
            errors.AddRange(device.Validate());

        problems = errors;
        return errors.Count == 0
            ? Result<DeviceDescriptor>.Success(device)
            : Result<DeviceDescriptor>.Failure(DeviceErrors.InvalidDescriptor(errors));
    }

    private static ParameterValues ParseParameters(string json, List<Error> errors)
    {
        // 只接受"字符串 → 字符串"的对象；数字、嵌套对象、null 值等手工改出来的形状一律视为损坏
        Dictionary<string, string?>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        }
        catch (JsonException ex)
        {
            errors.Add(DeviceErrors.StorageCorruptRow("parameters", $"'{json}' 不是有效的参数对象：{ex.Message}"));
            return ParameterValues.Empty;
        }

        if (values is null || values.Values.Any(v => v is null))
        {
            errors.Add(DeviceErrors.StorageCorruptRow("parameters", $"'{json}' 不是有效的参数对象"));
            return ParameterValues.Empty;
        }

        return new ParameterValues(values!);
    }
}
