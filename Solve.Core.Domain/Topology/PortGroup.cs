using Solve.Core.Domain.Drivers;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 位置的一组接点（需求侧）：产品接到设备上要几路、各是什么功能，如"采集 V × 4"展开为接点 V1–V4。
/// 与模块的通道组（供给侧，<see cref="ChannelGroup"/>）相对：通道组说"我能提供几路"，接点组说"这里需要接几路"。
/// </summary>
/// <remarks>
/// <para>向下生效：声明在哪个位置，就对它下面的全部产品位（含它自己，若它就是产品位）生效；产品位的接点 = 自身 + 全部上级声明的接点。
/// 接点只在产品位上核对：产品位是被测产品，柜体、层位等是共用资源的落点，不在那里要求"接完"。</para>
/// <para>一个接点由产品位的一路有效接线（自身或上级的接线）满足：功能相同、用途与接点名相同（忽略大小写与首尾空白）。
/// 这与接线的路径唯一规则用的是同一把键（功能 + 用途），所以一个接点最多只会有一路接线。</para>
/// <para>不可变、按内容比较相等。数值写入配置库（topology_nodes.ports），已发布的接点名不得随意改动：已有接线按用途匹配。</para>
/// </remarks>
/// <param name="Kind">接点功能，与通道功能同一套分类</param>
/// <param name="Name">接点名；数量为 1 时就是接点名（可为空：匹配不填用途的接线），数量大于 1 时是前缀，展开为"前缀 + 序号"</param>
/// <param name="Count">接点数，1–<see cref="MaxCount"/></param>
public sealed record PortGroup(ChannelKind Kind, string Name, int Count)
{
    /// <summary>
    /// 一组接点的数量上限：覆盖常见的多路采集，拦截误填的超大数量
    /// </summary>
    public const int MaxCount = 64;

    /// <summary>
    /// 接点名（前缀）长度上限：加上两位序号后不超过接线用途的上限 32
    /// </summary>
    public const int MaxNameLength = 30;

    /// <summary>
    /// 一个位置自身最多声明的接点组数
    /// </summary>
    public const int MaxGroupsPerNode = 16;

    /// <summary>
    /// 展开为接点：数量为 1 时接点名即 <see cref="Name"/>，否则为 Name1..NameN
    /// </summary>
    public IEnumerable<Port> Expand()
    {
        if (Count == 1)
        {
            yield return new Port(Kind, Name);
            yield break;
        }

        for (var index = 1; index <= Count; index++)
            yield return new Port(Kind, $"{Name}{index}");
    }

    /// <summary>
    /// 界面显示，如"采集 V × 4"、"供电 VBAT"
    /// </summary>
    public override string ToString()
    {
        var name = Name.Length > 0 ? $"{Kind.DisplayName()} {Name}" : Kind.DisplayName();
        return Count == 1 ? name : $"{name} × {Count}";
    }
}

/// <summary>
/// 一个接点：功能 + 用途。产品位上用接线的（功能，用途）与它匹配。
/// </summary>
/// <param name="Purpose">接点名（已去首尾空白），与接线的用途对应；空串表示匹配不填用途的接线</param>
public readonly record struct Port(ChannelKind Kind, string Purpose)
{
    /// <summary>
    /// 匹配用的键：与 Wire.PurposeKey 的规则相同（去空白、不分大小写）
    /// </summary>
    public string PurposeKey => Purpose.ToUpperInvariant();

    /// <summary>
    /// 界面显示，如"采集 V3"、"供电"
    /// </summary>
    public string DisplayText => Purpose.Length > 0 ? $"{Kind.DisplayName()} {Purpose}" : Kind.DisplayName();

    public bool Matches(ChannelKind kind, string purposeKey) => Kind == kind && string.Equals(PurposeKey, purposeKey, StringComparison.Ordinal);

    public override string ToString() => DisplayText;
}

/// <summary>
/// 产品位要求的一个接点，及声明它的位置（自身或某个上级）
/// </summary>
public readonly record struct RequiredPort(Port Port, TopologyNodeId Source);
