namespace Solve.Core.Domain.Drivers;

/// <summary>
/// 驱动的稳定标识（如 "generic.module"），写入模块配置，用来找回该模块使用的驱动。
/// </summary>
/// <remarks>
/// <para>一经发布不得改名：库里的模块只按它找驱动，改名后已有模块会变成"未知驱动"。</para>
/// <para>格式：1–64 个字符，只含小写字母、数字、'.'、'-'、'_'，便于日志检索与人工核对。default 为空值，校验时拒绝。</para>
/// </remarks>
public readonly record struct DriverId
{
    public const int MaxLength = 64;

    private DriverId(string value) => Value = value;

    public string? Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// 可信输入（代码常量）使用，格式非法时抛出 <see cref="ArgumentException"/>
    /// </summary>
    public static DriverId Of(string value)
        => TryParse(value, out var id) ? id : throw new ArgumentException($"驱动标识格式非法：'{value}'。", nameof(value));

    /// <summary>
    /// 外部输入（数据库）使用
    /// </summary>
    public static bool TryParse(string? value, out DriverId id)
    {
        id = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'))
                return false;
        }

        id = new DriverId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
