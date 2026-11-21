using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Domain.Wiring;

/// <summary>
/// 接线的系统身份（机器用）。创建时生成一次、持久化后终身不变；界面不显示本 Id。default 为空值，校验时拒绝。
/// </summary>
public readonly record struct WireId(Guid Value)
{
    public static readonly WireId Empty = default;

    /// <summary>
    /// 生成 UUIDv7 标识（按创建时间有序，作为数据库主键时索引局部性好）
    /// </summary>
    public static WireId New() => new(Guid.CreateVersion7());

    /// <summary>
    /// 外部输入（数据库）使用；空 GUID 视为解析失败
    /// </summary>
    public static bool TryParse(string? value, out WireId id)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            id = new WireId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// 一路通道的定位：哪个模块、哪个通道组、第几路。驱动保证（组标识，通道号）一经发布不再改变含义。
/// </summary>
/// <param name="Group">驱动声明的通道组标识（<see cref="ChannelGroup.Key"/>）</param>
/// <param name="Index">通道号，从 1 开始</param>
public readonly record struct ChannelRef(DeviceId Device, string Group, int Index)
{
    public override string ToString() => $"{Device}/{Group}#{Index}";
}

/// <summary>
/// 一条接线：把模块的一路通道接到设备拓扑的一个位置上（数字孪生里的"一根线"）。配置，不可变。
/// </summary>
/// <remarks>
/// <para>向下生效：接在哪个位置，就对它和它的全部下级生效。产品位的有效接线 = 自身 + 全部上级的接线
/// （如层位上的电源 + 槽位上的负载 + 产品位上的采集）。</para>
/// <para>通道功能 <see cref="Kind"/> 在接线时按驱动声明记下：驱动包未加载时规则校验仍然有据可依；
/// 驱动升级后若声明不符，运行时把这条接线标为无效，不自动改写。</para>
/// <para>用途 <see cref="Purpose"/> 区分同一位置上同一功能的多路接线（如双路供电的 "VBAT" 与 "VCC"）。
/// 同一条位置路径（祖先 → 后代）上，同一功能、同一用途只能有一路，否则有两路电源并到同一个产品上的风险。</para>
/// </remarks>
/// <param name="Node">接线落点，柜体到产品位任一层（系统根不接线）</param>
public sealed record Wire(WireId Id, TopologyNodeId Node, ChannelRef Channel, ChannelKind Kind)
{
    public const int MaxPurposeLength = 32;
    public const int MaxLabelLength = 32;

    /// <summary>
    /// 用途（如 "VBAT"），未填为 null。比较时忽略大小写与首尾空白。
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// 线号（现场线缆标签，如 "W-C01-012"），供维护追溯，未填为 null
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// 路径唯一性比较用的用途：去空白、不分大小写；未填视为空串（同一功能只允许一路不填用途的接线）
    /// </summary>
    public string PurposeKey => Purpose?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>
    /// 界面显示的用途说明，如"供电 VBAT"、"采集"
    /// </summary>
    public string KindText => Purpose is { Length: > 0 } purpose ? $"{Kind.DisplayName()} {purpose}" : Kind.DisplayName();

    /// <summary>
    /// 校验字段并一次性返回所有错误。空列表表示有效。结构性规则（落点层级、通道独占、路径唯一）由 <see cref="WiringMap"/> 校验。
    /// </summary>
    public IReadOnlyList<Error> Validate()
    {
        var errors = new List<Error>();
        if (Id.IsEmpty)
            errors.Add(WiringErrors.InvalidField("Id", "接线 Id 不能为空。"));
        if (Node.IsEmpty)
            errors.Add(WiringErrors.InvalidField("Node", "接线必须落在一个位置上。"));
        if (Channel.Device.IsEmpty)
            errors.Add(WiringErrors.InvalidField("Device", "接线必须选择模块。"));
        if (string.IsNullOrWhiteSpace(Channel.Group))
            errors.Add(WiringErrors.InvalidField("Group", "接线必须选择通道。"));
        if (Channel.Index < 1)
            errors.Add(WiringErrors.InvalidField("Index", $"通道号必须从 1 开始，当前为 {Channel.Index}。"));
        if (!Kind.IsDefined())
            errors.Add(WiringErrors.InvalidField("Kind", $"通道功能未定义：{(int)Kind}。"));
        if (Purpose is { Length: > MaxPurposeLength })
            errors.Add(WiringErrors.InvalidField("Purpose", $"用途不能超过 {MaxPurposeLength} 个字符。"));
        if (Label is { Length: > MaxLabelLength })
            errors.Add(WiringErrors.InvalidField("Label", $"线号不能超过 {MaxLabelLength} 个字符。"));
        return errors;
    }

    /// <summary>
    /// 去除首尾空白，空白视为未填
    /// </summary>
    public static string? NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// 日志格式：落点、通道、功能在前，Id 在后
    /// </summary>
    public override string ToString() => $"接线 {KindText} {Channel} → 节点 {Node} [{Id}]";
}
