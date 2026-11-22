using System.Globalization;

namespace Solve.Device.Sdk;

/// <summary>
/// 命令的一个数值参数（SDK 1.2 新增），如电子负载"恒流带载"的电流设定值。
/// 内核按声明校验调用方传入的参数、补全默认值后再交给 <see cref="IDeviceSession.BeginCommand"/>，
/// 界面按声明生成输入项；驱动自己仍要按同样的范围再校验一次（纵深防御，也兼容旧内核）。
/// </summary>
/// <remarks>
/// <para>值一律是 <see cref="Unit"/> 下的工程值（已换算，不是协议原始值）。</para>
/// <para>一经发布，Key 不得改名、单位不得改变；可以放宽范围，收窄范围会让已有测试流程的设定值失效，视同改名。
/// 新增参数必须带 <see cref="DefaultValue"/>，否则已有调用方会因缺少参数被拒绝。</para>
/// </remarks>
/// <param name="Key">命令内唯一的稳定标识，格式同点位 Key，如 "current"</param>
/// <param name="Name">界面显示名，如"电流"，最长 <see cref="MaxNameLength"/> 个字符</param>
/// <param name="Unit">物理单位，如 "A"；无单位填空串</param>
/// <param name="Minimum">允许的最小值（含）</param>
/// <param name="Maximum">允许的最大值（含）</param>
public sealed record CommandArgumentInfo(string Key, string Name, string Unit, double Minimum, double Maximum)
{
    public const int MaxNameLength = 32;

    /// <summary>
    /// 默认值：为 null 表示必填；有默认值时调用方可以不传，内核交给驱动前补上
    /// </summary>
    public double? DefaultValue { get; init; }

    /// <summary>
    /// 必填参数（没有默认值）
    /// </summary>
    public bool IsRequired => DefaultValue is null;

    /// <summary>
    /// 检查一个参数值是否合法（有限且在范围内）。内核下发前调用；驱动与测试也可以直接调用。
    /// </summary>
    /// <returns>合法时为 null；否则为面向操作员的原因</returns>
    public string? Check(double value)
    {
        if (!double.IsFinite(value))
            return $"{Name}的值无效。";
        if (value < Minimum || value > Maximum)
            return $"{Name} {Format(value)} {Unit} 超出范围 [{Format(Minimum)}, {Format(Maximum)}] {Unit}。";
        return null;
    }

    /// <summary>
    /// 检查声明本身是否有效（设备库加载驱动包时由 <see cref="DriverInfo.Validate"/> 调用）。返回全部问题，空列表表示有效。
    /// </summary>
    public IReadOnlyList<string> ValidateDeclaration(string commandKey)
    {
        var problems = new List<string>();
        if (!KeyFormat.IsValid(Key))
            problems.Add($"命令 {commandKey} 的参数标识「{Key}」格式无效。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
            problems.Add($"命令 {commandKey} 的参数 {Key} 名称不能为空，且不能超过 {MaxNameLength} 个字符。");
        if (Unit is null)
            problems.Add($"命令 {commandKey} 的参数 {Key} 单位不能为 null（无单位填空串）。");
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Minimum > Maximum)
            problems.Add($"命令 {commandKey} 的参数 {Key} 范围 [{Format(Minimum)}, {Format(Maximum)}] 无效：必须是有限数且下限不大于上限。");
        else if (DefaultValue is { } value && Check(value) is { } error)
            problems.Add($"命令 {commandKey} 的参数 {Key} 默认值无效：{error}");

        return problems;
    }

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
