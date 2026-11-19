using System.Globalization;

namespace Solve.Device.Sdk;

/// <summary>
/// 模块参数的类型
/// </summary>
public enum ParameterKind
{
    /// <summary>
    /// 整数（如通道数）
    /// </summary>
    Integer = 1,

    /// <summary>
    /// 实数（如量程上限）
    /// </summary>
    Number = 2,

    /// <summary>
    /// 是 / 否
    /// </summary>
    Boolean = 3,

    /// <summary>
    /// 从 <see cref="ParameterInfo.Choices"/> 中选一项（如报文封装：RTU / TCP / RTU over TCP）
    /// </summary>
    Choice = 4,

    /// <summary>
    /// 短文本
    /// </summary>
    Text = 5,
}

/// <summary>
/// <see cref="ParameterKind.Choice"/> 参数的一个选项
/// </summary>
/// <param name="Value">保存的值，驱动内唯一，一经发布不得改名</param>
/// <param name="Name">界面显示名</param>
public sealed record ParameterChoice(string Value, string Name);

/// <summary>
/// 驱动声明的一个模块参数：同一型号在不同现场需要不同配置时使用（报文封装、通道数、量程等）。
/// 操作员在模块配置窗口按声明填写，内核校验后通过 <see cref="DeviceContext.Parameters"/> 交给会话。
/// </summary>
/// <remarks>
/// 参数值一律以文本保存（数字用不变区域性格式，布尔为 "true" / "false"）。
/// 新版本驱动可以新增参数（必须给默认值，已有模块按默认值运行），不得删除或改名已发布的参数。
/// </remarks>
/// <param name="Key">驱动内唯一的稳定标识，格式同点位 Key，一经发布不得改名</param>
/// <param name="Name">界面显示名</param>
/// <param name="Kind">类型</param>
/// <param name="DefaultValue">默认值（文本形式），必须本身就是合法值</param>
public sealed record ParameterInfo(string Key, string Name, ParameterKind Kind, string DefaultValue)
{
    /// <summary>
    /// 物理单位，无单位为空串
    /// </summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// 最小值（仅 <see cref="ParameterKind.Integer"/> / <see cref="ParameterKind.Number"/>）
    /// </summary>
    public double? Minimum { get; init; }

    /// <summary>
    /// 最大值（仅 <see cref="ParameterKind.Integer"/> / <see cref="ParameterKind.Number"/>）
    /// </summary>
    public double? Maximum { get; init; }

    /// <summary>
    /// 可选项（仅 <see cref="ParameterKind.Choice"/>，至少一项）
    /// </summary>
    public IReadOnlyList<ParameterChoice> Choices { get; init; } = [];

    /// <summary>
    /// 文本最大长度（仅 <see cref="ParameterKind.Text"/>）
    /// </summary>
    public int MaxLength { get; init; } = 64;

    /// <summary>
    /// 校验并规范化一个参数值（如 " 4 " → "4"、"TRUE" → "true"）。内核保存前调用；驱动的测试也可以直接调用。
    /// </summary>
    /// <returns>合法时为规范化后的文本；不合法时为 null，<paramref name="error"/> 给出原因</returns>
    public string? Normalize(string? raw, out string? error)
    {
        error = null;
        var text = raw?.Trim() ?? string.Empty;
        switch (Kind)
        {
            case ParameterKind.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return Fail($"{Name}必须是整数。", out error);
                return InRange(integer, out error) ? integer.ToString(CultureInfo.InvariantCulture) : null;

            case ParameterKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    return Fail($"{Name}必须是数字。", out error);
                return InRange(number, out error) ? number.ToString("R", CultureInfo.InvariantCulture) : null;

            case ParameterKind.Boolean:
                if (!bool.TryParse(text, out var flag))
                    return Fail($"{Name}必须是 true 或 false。", out error);
                return flag ? "true" : "false";

            case ParameterKind.Choice:
                var choice = Choices.FirstOrDefault(c => string.Equals(c.Value, text, StringComparison.Ordinal));
                return choice?.Value ?? Fail($"{Name}只能取：{string.Join("、", Choices.Select(c => c.Name))}。", out error);

            case ParameterKind.Text:
                if (text.Length > MaxLength)
                    return Fail($"{Name}不能超过 {MaxLength} 个字符。", out error);
                return text;

            default:
                return Fail($"{Name}的类型未定义：{(int)Kind}。", out error);
        }
    }

    /// <summary>
    /// 检查声明本身是否有效（设备库加载驱动包时调用）。返回全部问题，空列表表示有效。
    /// </summary>
    public IReadOnlyList<string> ValidateDeclaration()
    {
        var problems = new List<string>();

        if (!KeyFormat.IsValid(Key))
            problems.Add($"参数标识「{Key}」格式无效。");
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add($"参数 {Key} 的名称不能为空。");
        if (!Enum.IsDefined(Kind))
        {
            problems.Add($"参数 {Key} 的类型未定义：{(int)Kind}。");
            return problems;
        }

        if (Minimum is { } min && Maximum is { } max && min > max)
            problems.Add($"参数 {Key} 的最小值大于最大值。");
        if (Kind == ParameterKind.Choice)
        {
            if (Choices is null || Choices.Count == 0)
                problems.Add($"参数 {Key} 是选项类型，至少要有一个选项。");
            else if (Choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != Choices.Count)
                problems.Add($"参数 {Key} 的选项值重复。");
        }

        if (Kind == ParameterKind.Text && MaxLength is < 1 or > 256)
            problems.Add($"参数 {Key} 的最大长度必须在 [1, 256] 之间。");

        if (problems.Count == 0 && Normalize(DefaultValue, out var error) is null)
            problems.Add($"参数 {Key} 的默认值无效：{error}");

        return problems;
    }

    private bool InRange(double value, out string? error)
    {
        error = null;
        if (Minimum is { } min && value < min)
            error = $"{Name}不能小于 {min.ToString(CultureInfo.InvariantCulture)}{Unit}。";
        else if (Maximum is { } max && value > max)
            error = $"{Name}不能大于 {max.ToString(CultureInfo.InvariantCulture)}{Unit}。";
        return error is null;
    }

    private static string? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }
}

/// <summary>
/// 内核交给会话的参数值：已按驱动声明校验、规范化，未填写的已补上默认值。
/// </summary>
public sealed class DeviceParameters
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public DeviceParameters(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>
    /// 没有任何参数
    /// </summary>
    public static DeviceParameters Empty { get; } = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Values => _values;

    public long GetInteger(string key) => long.Parse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public double GetNumber(string key) => double.Parse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture);

    public bool GetBoolean(string key) => bool.Parse(Get(key));

    /// <summary>
    /// 选项或文本参数的值
    /// </summary>
    public string GetText(string key) => Get(key);

    // 只会读到驱动自己声明过的参数，取不到说明驱动代码写错了 Key（编程错误），直接抛出
    private string Get(string key)
        => _values.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"驱动没有声明参数 {key}。");
}
