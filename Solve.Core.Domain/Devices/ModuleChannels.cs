using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Domain.Devices;

/// <summary>
/// 模块实例对一组通道的设置（不可变、按内容比较相等）。
/// </summary>
/// <remarks>
/// <para>有驱动的模块：<see cref="Key"/> 必须是驱动声明的组，只能选择启用哪几路、给通道起别名；名称、功能、数量由驱动决定，这里必须留空。</para>
/// <para>通用模块（驱动允许手工通道）：一组通道完全由这里定义，名称、功能、数量必填。</para>
/// </remarks>
public sealed class ChannelGroupSetting : IEquatable<ChannelGroupSetting>
{
    public const int MaxAliasLength = 32;

    private readonly int[]? _enabled;
    private readonly SortedDictionary<int, string> _aliases;

    /// <param name="enabled">启用的通道号；null 表示全部启用。重复与顺序无关</param>
    /// <param name="aliases">通道别名；空白别名忽略</param>
    public ChannelGroupSetting(
        string key,
        string? name = null,
        ChannelKind? kind = null,
        int? count = null,
        IEnumerable<int>? enabled = null,
        IEnumerable<KeyValuePair<int, string>>? aliases = null)
    {
        Key = key ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Kind = kind;
        Count = count;
        _enabled = enabled?.Distinct().Order().ToArray();
        _aliases = [];
        foreach (var (index, alias) in aliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(alias))
                _aliases[index] = alias.Trim();
        }
    }

    public string Key { get; }

    /// <summary>
    /// 组名（只用于通用模块）
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// 功能（只用于通用模块）
    /// </summary>
    public ChannelKind? Kind { get; }

    /// <summary>
    /// 通道数（只用于通用模块）
    /// </summary>
    public int? Count { get; }

    /// <summary>
    /// 启用的通道号（升序、无重复）；null 表示全部启用
    /// </summary>
    public IReadOnlyList<int>? Enabled => _enabled;

    public IReadOnlyDictionary<int, string> Aliases => _aliases;

    public bool Equals(ChannelGroupSetting? other)
        => other is not null
           && string.Equals(Key, other.Key, StringComparison.Ordinal)
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && Kind == other.Kind
           && Count == other.Count
           && (_enabled is null ? other._enabled is null : other._enabled is not null && _enabled.SequenceEqual(other._enabled))
           && _aliases.Count == other._aliases.Count
           && _aliases.All(pair => other._aliases.TryGetValue(pair.Key, out var alias) && string.Equals(alias, pair.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as ChannelGroupSetting);

    public override int GetHashCode() => HashCode.Combine(Key, Name, Kind, Count, _enabled?.Length, _aliases.Count);
}

/// <summary>
/// 模块实例的通道配置（不可变、按内容比较相等）：启用哪几路、通道别名，通用模块还定义有哪些通道。
/// 没有设置的组按驱动声明全部启用：已有模块升级后接线不受影响。
/// </summary>
public sealed class ModuleChannelConfig : IEquatable<ModuleChannelConfig>
{
    /// <summary>
    /// 一个模块的通道组数上限（通用模块手工定义时）
    /// </summary>
    public const int MaxGroups = 16;

    public ModuleChannelConfig(IEnumerable<ChannelGroupSetting> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Groups = groups.ToArray();
    }

    public static ModuleChannelConfig Empty { get; } = new([]);

    /// <summary>
    /// 各组的设置，按界面上的顺序
    /// </summary>
    public IReadOnlyList<ChannelGroupSetting> Groups { get; }

    public bool IsEmpty => Groups.Count == 0;

    public ChannelGroupSetting? Find(string key) => Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// 结构校验（与驱动无关），错误追加到 <paramref name="errors"/>。是否与驱动的通道能力相符由 <see cref="ModuleChannels.Resolve"/> 校验。
    /// </summary>
    public void CollectErrors(List<Error> errors)
    {
        if (Groups.Count > MaxGroups)
            errors.Add(DeviceErrors.InvalidField("Channels", $"通道组不能超过 {MaxGroups} 个。"));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            if (group is null)
            {
                errors.Add(DeviceErrors.InvalidField("Channels", "通道组设置不能为 null。"));
                continue;
            }

            // 组标识与驱动标识同一格式（SDK 的 KeyFormat）
            if (!DriverId.TryParse(group.Key, out _))
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组标识「{group.Key}」格式无效。"));
            else if (!keys.Add(group.Key))
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组 {group.Key} 重复。"));

            if (group.Enabled is { } enabled && enabled.Any(i => i is < 1 or > ChannelGroup.MaxCount))
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组 {group.Key} 的启用通道号必须在 [1, {ChannelGroup.MaxCount}] 之间。"));

            foreach (var (index, alias) in group.Aliases)
            {
                if (index is < 1 or > ChannelGroup.MaxCount)
                    errors.Add(DeviceErrors.InvalidField("Channels", $"通道组 {group.Key} 的别名通道号 {index} 越界。"));
                else if (alias.Length > ChannelGroupSetting.MaxAliasLength)
                    errors.Add(DeviceErrors.InvalidField("Channels", $"通道别名不能超过 {ChannelGroupSetting.MaxAliasLength} 个字符：「{alias}」。"));
            }
        }
    }

    public bool Equals(ModuleChannelConfig? other) => other is not null && Groups.SequenceEqual(other.Groups);

    public override bool Equals(object? obj) => Equals(obj as ModuleChannelConfig);

    public override int GetHashCode() => Groups.Count;
}

/// <summary>
/// 按驱动的通道能力解析模块实例的通道配置，得到这台模块实际可用的通道组（纯计算）。
/// </summary>
public static class ModuleChannels
{
    /// <summary>
    /// 解析并校验。失败时返回全部问题合成的一条错误（面向操作员）。
    /// </summary>
    public static Result<IReadOnlyList<ChannelGroup>> Resolve(DriverDescriptor driver, ModuleChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<Error>();
        config.CollectErrors(errors);
        if (errors.Count > 0)
            return Failure(errors);

        var groups = driver.AllowsManualChannels ? ResolveManual(driver, config, errors) : ResolveDeclared(driver, config, errors);
        return errors.Count > 0 ? Failure(errors) : Result<IReadOnlyList<ChannelGroup>>.Success(groups);
    }

    /// <summary>
    /// 有驱动的模块：以驱动声明为准，只应用启用与别名
    /// </summary>
    private static List<ChannelGroup> ResolveDeclared(DriverDescriptor driver, ModuleChannelConfig config, List<Error> errors)
    {
        foreach (var setting in config.Groups)
        {
            if (driver.FindChannelGroup(setting.Key) is null)
                errors.Add(DeviceErrors.InvalidField("Channels", $"{driver.Name}没有通道组 {setting.Key}。"));
            else if (setting.Name is not null || setting.Kind is not null || setting.Count is not null)
                errors.Add(DeviceErrors.InvalidField("Channels", $"{driver.Name}的通道名称、功能与数量由驱动决定，不能在模块上修改。"));
        }

        var groups = new List<ChannelGroup>(driver.Channels.Count);
        foreach (var declared in driver.Channels)
            groups.Add(Apply(declared, config.Find(declared.Key), errors));
        return groups;
    }

    /// <summary>
    /// 通用模块：通道组完全由模块配置定义
    /// </summary>
    private static List<ChannelGroup> ResolveManual(DriverDescriptor driver, ModuleChannelConfig config, List<Error> errors)
    {
        var groups = new List<ChannelGroup>(config.Groups.Count);
        foreach (var setting in config.Groups)
        {
            if (setting.Name is not { } name || setting.Kind is not { } kind || setting.Count is not { } count)
            {
                errors.Add(DeviceErrors.InvalidField("Channels", $"{driver.Name}的每组通道都要填写名称、功能与数量。"));
                continue;
            }

            if (name.Length > DriverDescriptor.MaxChannelNameLength)
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组名称不能超过 {DriverDescriptor.MaxChannelNameLength} 个字符。"));
            if (!kind.IsDefined())
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组「{name}」的功能未定义。"));
            if (count is < 1 or > ChannelGroup.MaxCount)
            {
                errors.Add(DeviceErrors.InvalidField("Channels", $"通道组「{name}」的数量必须在 [1, {ChannelGroup.MaxCount}] 之间，当前为 {count}。"));
                continue;
            }

            groups.Add(Apply(new ChannelGroup(setting.Key, name, kind, count), setting, errors));
        }

        return groups;
    }

    private static ChannelGroup Apply(ChannelGroup group, ChannelGroupSetting? setting, List<Error> errors)
    {
        if (setting is null)
            return group;

        if (setting.Enabled is { } enabled && enabled.Any(i => !group.Contains(i)))
            errors.Add(DeviceErrors.InvalidField("Channels", $"「{group.Name}」只有 {group.Count} 路，启用的通道号超出范围。"));
        if (setting.Aliases.Keys.Any(i => !group.Contains(i)))
            errors.Add(DeviceErrors.InvalidField("Channels", $"「{group.Name}」只有 {group.Count} 路，别名的通道号超出范围。"));

        return group with
        {
            EnabledIndices = setting.Enabled?.ToHashSet(),
            Aliases = new Dictionary<int, string>(setting.Aliases),
        };
    }

    private static Result<IReadOnlyList<ChannelGroup>> Failure(List<Error> errors)
        => Result<IReadOnlyList<ChannelGroup>>.Failure(DeviceErrors.InvalidDescriptor(errors.DistinctBy(e => e.Message).ToList()));
}
