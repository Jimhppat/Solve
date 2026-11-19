using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Domain.Devices;

/// <summary>
/// 模块参数值（参数标识 → 文本值），不可变、按内容比较相等。
/// </summary>
/// <remarks>
/// 只保存操作员填写过的参数；未填写的在绑定驱动时取驱动声明的默认值，驱动升级调整默认值后自动生效。
/// 这里只校验结构（标识格式、个数、长度）；取值是否合法要看驱动的声明，由应用服务校验。
/// </remarks>
public sealed class ParameterValues : IReadOnlyDictionary<string, string>, IEquatable<ParameterValues>
{
    public const int MaxCount = 32;
    public const int MaxValueLength = 256;

    private readonly SortedDictionary<string, string> _values;

    public ParameterValues(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            ArgumentNullException.ThrowIfNull(key);
            _values[key] = value ?? throw new ArgumentNullException(nameof(values), $"参数 {key} 的值为 null。");
        }
    }

    public static ParameterValues Empty { get; } = new([]);

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public string this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// 结构校验，错误追加到 <paramref name="errors"/>
    /// </summary>
    public void CollectErrors(List<Error> errors)
    {
        if (_values.Count > MaxCount)
            errors.Add(DeviceErrors.InvalidField("Parameters", $"模块参数不能超过 {MaxCount} 个。"));

        foreach (var (key, value) in _values)
        {
            // 参数标识与驱动标识同一格式（SDK 的 KeyFormat）
            if (!DriverId.TryParse(key, out _))
                errors.Add(DeviceErrors.InvalidField("Parameters", $"参数标识「{key}」格式无效。"));
            else if (value.Length > MaxValueLength)
                errors.Add(DeviceErrors.InvalidField("Parameters", $"参数 {key} 的值不能超过 {MaxValueLength} 个字符。"));
        }
    }

    public bool Equals(ParameterValues? other)
        => other is not null && _values.Count == other._values.Count
           && _values.All(pair => other._values.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as ParameterValues);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => _values.Count == 0 ? "{}" : "{" + string.Join(", ", _values.Select(p => $"{p.Key}={p.Value}")) + "}";
}
