namespace Solve.Device.Sdk;

/// <summary>
/// 标识的统一格式：驱动标识、点位 / 命令 / 参数的 Key。1–64 个字符，只含小写字母、数字、'.'、'-'、'_'。
/// </summary>
public static class KeyFormat
{
    public const int MaxLength = 64;

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
            return false;

        foreach (var c in key)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'))
                return false;
        }

        return true;
    }
}
