namespace Solve.Core.Domain.Devices;

/// <summary>
/// 总线挂载设备的系统身份（机器用）。创建时生成一次、持久化后终身不变；界面只显示名称与地址，不显示本 Id。
/// </summary>
/// <remarks>
/// default(DeviceId) 为空值，校验时拒绝。
/// </remarks>
public readonly record struct DeviceId(Guid Value)
{
    public static readonly DeviceId Empty = default;

    /// <summary>
    /// 生成 UUIDv7 设备标识（按创建时间有序，作为数据库主键时索引局部性好）
    /// </summary>
    public static DeviceId New() => new(Guid.CreateVersion7());

    /// <summary>
    /// 从字符串解析，外部输入（数据库、配置）请使用此方法；空 GUID 视为解析失败
    /// </summary>
    public static bool TryParse(string? value, out DeviceId id)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            id = new DeviceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");
}
