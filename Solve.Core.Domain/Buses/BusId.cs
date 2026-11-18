namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线全局唯一标识
/// </summary>
/// <remarks>
/// default(BusId) 为空值，配置加载与注册时必须用 <see cref="IsEmpty"/> 拒绝。
/// JSON 序列化由持久化适配器负责（见 Solve.Adapters.Persistence），领域层不绑定序列化格式。
/// </remarks>
public readonly record struct BusId(Guid Value)
{
    public static readonly BusId Empty = default;

    /// <summary>
    /// 基于毫秒级时间生成 UUIDv7 总线标识（按创建时间有序，便于日志与存储排序）
    /// </summary>
    public static BusId New() => new(Guid.CreateVersion7());

    /// <summary>
    /// 从字符串解析，格式非法时抛出 <see cref="FormatException"/>，仅用于可信输入
    /// </summary>
    public static BusId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// 从字符串解析，外部配置等不可信输入请使用此方法；空 GUID 视为解析失败
    /// </summary>
    public static bool TryParse(string? value, out BusId id)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            id = new BusId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>
    /// 是否为空值
    /// </summary>
    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString();
}
