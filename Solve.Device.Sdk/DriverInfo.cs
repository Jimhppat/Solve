namespace Solve.Device.Sdk;

/// <summary>
/// 设备分类，决定驱动出现在模块配置窗口的哪个分类下。数值写入契约，已有成员不得修改，新增只能追加。
/// </summary>
public enum DeviceCategory
{
    /// <summary>
    /// 通用
    /// </summary>
    General = 1,

    /// <summary>
    /// 电源 / 电子负载
    /// </summary>
    PowerAndLoad = 2,
}

/// <summary>
/// 点位的读写权限
/// </summary>
public enum PointAccess
{
    Read = 1,
    ReadWrite = 2,
}

/// <summary>
/// 一个点位（一个可读的物理量或状态）。驱动在 <see cref="ExchangeStep.Completed"/> 中按 <see cref="Key"/> 上报数值。
/// </summary>
/// <param name="Key">驱动内唯一的稳定标识，如 "voltage"；一经发布不得改名</param>
/// <param name="Name">界面显示名，如"输出电压"</param>
/// <param name="Unit">物理单位，如 "V"；无单位填空串。上报的数值已换算到该单位（已乘倍率、加偏移）</param>
/// <param name="Access">读写权限</param>
public sealed record PointInfo(string Key, string Name, string Unit, PointAccess Access)
{
    /// <summary>
    /// 所属通道组（<see cref="ChannelGroupInfo.Key"/>）：每路通道各有一个该点位，上报时用 <see cref="PointSample.Channel"/> 区分。
    /// 为 null 表示模块级点位（整台设备只有一个，如单输出电源的输出电压、设备温度）。
    /// </summary>
    public string? Group { get; init; }
}

/// <summary>
/// 命令的安全等级：内核据此决定执行前的检查（安全联锁、仿真模式、二次确认），驱动无法绕过
/// </summary>
public enum CommandSafety
{
    /// <summary>
    /// 写参数（不直接改变输出，如设定值）
    /// </summary>
    Parameter = 1,

    /// <summary>
    /// 输出动作（开关输出、启停）
    /// </summary>
    Output = 2,

    /// <summary>
    /// 高危（高压、大电流、不可逆操作）：每次执行都需要操作员二次确认
    /// </summary>
    Hazardous = 3,
}

/// <summary>
/// 一条命令
/// </summary>
/// <param name="Key">驱动内唯一的稳定标识，如 "output.on"；一经发布不得改名</param>
/// <param name="Name">界面显示名，如"打开输出"</param>
/// <param name="Safety">安全等级，决定内核执行前的检查</param>
public sealed record CommandInfo(string Key, string Name, CommandSafety Safety)
{
    /// <summary>
    /// 作用的通道组：命令针对某一路通道（如"打开第 3 路继电器"），执行时用 <see cref="DeviceCommand.Channel"/> 指定通道号。
    /// 为 null 表示模块级命令。
    /// </summary>
    public string? Group { get; init; }
}

/// <summary>
/// 驱动说明：设备库据此在驱动表中列出驱动，并校验模块地址。
/// </summary>
/// <param name="Id">全局唯一的驱动标识，1–64 个字符，只含小写字母、数字、'.'、'-'、'_'，建议"厂家.型号"，如 "acme.psu-3000"。
/// 一经发布不得改名：已挂载的模块只按它找回驱动。</param>
/// <param name="Name">驱动表中显示的名称，如"Acme 程控电源"，最长 64 个字符</param>
/// <param name="Category">设备分类</param>
/// <param name="Description">一句话说明，显示为驱动表的悬停提示</param>
/// <param name="MinAddress">该设备在总线上允许的最小地址（1–255）</param>
/// <param name="MaxAddress">该设备在总线上允许的最大地址（1–255）</param>
public sealed record DriverInfo(
    string Id,
    string Name,
    DeviceCategory Category,
    string Description,
    int MinAddress,
    int MaxAddress)
{
    public const int MaxNameLength = 64;
    public const int MinAllowedAddress = 1;
    public const int MaxAllowedAddress = 255;

    public IReadOnlyList<PointInfo> Points { get; init; } = [];

    public IReadOnlyList<CommandInfo> Commands { get; init; } = [];

    /// <summary>
    /// 模块参数（同一型号在不同现场的不同配置）
    /// </summary>
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];

    /// <summary>
    /// 通道（可以单独接线的端子），按组声明。没有声明通道的驱动不能接到设备拓扑上。
    /// </summary>
    public IReadOnlyList<ChannelGroupInfo> Channels { get; init; } = [];

    /// <summary>
    /// 检查驱动说明是否有效（设备库加载驱动包时调用，驱动的测试也应调用）。返回全部问题，空列表表示有效。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!KeyFormat.IsValid(Id))
            problems.Add($"驱动标识「{Id}」格式无效：只能含小写字母、数字、'.'、'-'、'_'，最长 {KeyFormat.MaxLength} 个字符。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
            problems.Add($"驱动 {Id} 的名称不能为空，且不能超过 {MaxNameLength} 个字符。");
        if (!Enum.IsDefined(Category))
            problems.Add($"驱动 {Id} 的分类未定义：{(int)Category}。");
        if (MinAddress < MinAllowedAddress || MaxAddress > MaxAllowedAddress || MinAddress > MaxAddress)
            problems.Add($"驱动 {Id} 的地址范围 [{MinAddress}, {MaxAddress}] 必须在 [{MinAllowedAddress}, {MaxAllowedAddress}] 之内且下限不大于上限。");

        // 记录的 init 列表可被设成 null，这里不能信任可空注解
        CheckKeys("点位", Points?.Select(p => (p?.Key, p is not null && !string.IsNullOrWhiteSpace(p.Name) && Enum.IsDefined(p.Access))), problems);
        CheckKeys("命令", Commands?.Select(c => (c?.Key, c is not null && !string.IsNullOrWhiteSpace(c.Name) && Enum.IsDefined(c.Safety))), problems);
        CheckKeys("参数", Parameters?.Select(p => (p?.Key, p is not null)), problems);

        foreach (var parameter in Parameters ?? [])
        {
            if (parameter is not null)
                problems.AddRange(parameter.ValidateDeclaration());
        }

        ValidateChannels(problems);
        return problems;
    }

    private void ValidateChannels(List<string> problems)
    {
        // 记录的 init 列表可被设成 null，这里不能信任可空注解
        if (Channels is null)
        {
            problems.Add($"驱动 {Id} 的通道列表不能为 null。");
            return;
        }

        CheckKeys("通道组", Channels.Select(g => (g?.Key, g is not null
            && !string.IsNullOrWhiteSpace(g.Name) && g.Name.Length <= ChannelGroupInfo.MaxNameLength
            && Enum.IsDefined(g.Function))), problems);

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Channels)
        {
            if (group is null)
                continue;
            if (group.Key is not null)
                groups.Add(group.Key);
            if (group.Count is < 1 or > ChannelGroupInfo.MaxCount)
                problems.Add($"驱动 {Id} 的通道组 {group.Key} 数量必须在 [1, {ChannelGroupInfo.MaxCount}] 之间，当前为 {group.Count}。");
        }

        // 点位、命令引用的通道组必须已声明：否则上报的通道号无从校验
        foreach (var point in Points ?? [])
        {
            if (point?.Group is { } group && !groups.Contains(group))
                problems.Add($"驱动 {Id} 的点位 {point.Key} 引用了未声明的通道组 {group}。");
        }

        foreach (var command in Commands ?? [])
        {
            if (command?.Group is { } group && !groups.Contains(group))
                problems.Add($"驱动 {Id} 的命令 {command.Key} 引用了未声明的通道组 {group}。");
        }
    }

    private void CheckKeys(string kind, IEnumerable<(string? Key, bool Complete)>? items, List<string> problems)
    {
        if (items is null)
        {
            problems.Add($"驱动 {Id} 的{kind}列表不能为 null。");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, complete) in items)
        {
            if (!KeyFormat.IsValid(key))
                problems.Add($"驱动 {Id} 的{kind}标识「{key}」格式无效。");
            else if (!seen.Add(key!))
                problems.Add($"驱动 {Id} 的{kind}标识 {key} 重复。");
            else if (!complete)
                problems.Add($"驱动 {Id} 的{kind} {key} 名称为空或枚举值未定义。");
        }
    }
}
