using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 六层结构中一个位置的物理地址，如 C01-L02-S03-F01-P04（1 号柜 / 2 层 / 3 槽 / 1 号治具位 / 4 号产品位），系统根写作 SYS。
/// </summary>
/// <remarks>
/// <para>地址是节点的"人用身份"（位置编码）：全树唯一，用于界面显示、扫码、人工录入和日志阅读。
/// 系统内部引用一律使用 <see cref="TopologyNodeId"/>；地址会随重新编号而改变，不要把它当外键保存。</para>
/// <para>各层序号从 1 开始，与现场标牌一致；0 表示"该层及以下未指定"，地址的层级由最后一个非零序号决定。</para>
/// <para>default(TopologyAddress) 为空地址（<see cref="IsEmpty"/>），不指向任何节点，查询一律视为不存在，
/// 防止"忘记赋值"被当成整台设备。</para>
/// <para>排序按（柜, 层, 槽, 治具位, 产品位）字典序，未指定的 0 排在前面，正好是整棵树的先序遍历顺序：
/// 同一祖先的全部后代在有序序列里是连续的一段。</para>
/// </remarks>
public readonly record struct TopologyAddress : IComparable<TopologyAddress>
{
    /// <summary>
    /// 单层序号上限。三位数足够覆盖现场编号，同时限制单层扇出，防止错误配置生成超大树。
    /// </summary>
    public const int MaxOrdinal = 999;

    private const string SystemText = "SYS";
    private const string EmptyText = "(empty)";

    private TopologyAddress(TopologyLevel level, int cabinet, int layer, int slot, int fixture, int product)
    {
        Level = level;
        Cabinet = cabinet;
        Layer = layer;
        Slot = slot;
        Fixture = fixture;
        Product = product;
    }

    /// <summary>
    /// 系统根地址
    /// </summary>
    public static TopologyAddress System { get; } = new(TopologyLevel.System, 0, 0, 0, 0, 0);

    /// <summary>
    /// 地址所在层级；空地址为 0（未定义值）
    /// </summary>
    public TopologyLevel Level { get; }

    /// <summary>
    /// 柜号，未指定为 0
    /// </summary>
    public int Cabinet { get; }

    /// <summary>
    /// 层号，未指定为 0
    /// </summary>
    public int Layer { get; }

    /// <summary>
    /// 槽号，未指定为 0
    /// </summary>
    public int Slot { get; }

    /// <summary>
    /// 治具位号，未指定为 0
    /// </summary>
    public int Fixture { get; }

    /// <summary>
    /// 产品位号，未指定为 0
    /// </summary>
    public int Product { get; }

    public bool IsEmpty => Level == 0;

    /// <summary>
    /// 本层序号。系统根与空地址为 0。
    /// </summary>
    public int Ordinal => IsEmpty ? 0 : GetOrdinal(Level);

    /// <summary>
    /// 按自上而下的序号构造地址（第一个是柜号），不传序号即系统根。
    /// 仅用于可信输入（代码常量、测试），非法时抛出 <see cref="ArgumentException"/>；外部输入请用 <see cref="TryCreate"/>。
    /// </summary>
    public static TopologyAddress Of(params ReadOnlySpan<int> ordinals)
        => TryCreate(ordinals, out var address)
            ? address
            : throw new ArgumentException(
                $"地址序号非法：最多 {TopologyLevels.Count - 1} 级，每级在 [1, {MaxOrdinal}] 之间，实际为 [{string.Join(", ", ordinals.ToArray())}]。",
                nameof(ordinals));

    public static bool TryCreate(ReadOnlySpan<int> ordinals, out TopologyAddress address)
    {
        address = default;
        if (ordinals.Length > TopologyLevels.Count - 1)
            return false;

        foreach (var ordinal in ordinals)
        {
            if (ordinal is < 1 or > MaxOrdinal)
                return false;
        }

        Span<int> padded = stackalloc int[TopologyLevels.Count - 1];
        ordinals.CopyTo(padded);
        address = new TopologyAddress(
            (TopologyLevel)(ordinals.Length + (int)TopologyLevel.System),
            padded[0], padded[1], padded[2], padded[3], padded[4]);
        return true;
    }

    /// <summary>
    /// 指定层级上的序号。该层在地址层级之下（未指定）或为系统层时返回 0。
    /// </summary>
    public int GetOrdinal(TopologyLevel level) => level switch
    {
        TopologyLevel.System => 0,
        TopologyLevel.Cabinet => Cabinet,
        TopologyLevel.Layer => Layer,
        TopologyLevel.Slot => Slot,
        TopologyLevel.Fixture => Fixture,
        TopologyLevel.Product => Product,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的拓扑层级。"),
    };

    /// <summary>
    /// 下一层第 <paramref name="ordinal"/> 个位置的地址。产品位与空地址没有下级。
    /// </summary>
    public bool TryGetChild(int ordinal, out TopologyAddress child)
    {
        child = default;
        if (IsEmpty || Level == TopologyLevel.Product || ordinal is < 1 or > MaxOrdinal)
            return false;

        child = Level switch
        {
            TopologyLevel.System => new TopologyAddress(TopologyLevel.Cabinet, ordinal, 0, 0, 0, 0),
            TopologyLevel.Cabinet => new TopologyAddress(TopologyLevel.Layer, Cabinet, ordinal, 0, 0, 0),
            TopologyLevel.Layer => new TopologyAddress(TopologyLevel.Slot, Cabinet, Layer, ordinal, 0, 0),
            TopologyLevel.Slot => new TopologyAddress(TopologyLevel.Fixture, Cabinet, Layer, Slot, ordinal, 0),
            _ => new TopologyAddress(TopologyLevel.Product, Cabinet, Layer, Slot, Fixture, ordinal),
        };
        return true;
    }

    /// <summary>
    /// 可信输入版本的 <see cref="TryGetChild"/>，非法时抛出。
    /// </summary>
    public TopologyAddress Child(int ordinal)
    {
        if (IsEmpty || Level == TopologyLevel.Product)
            throw new InvalidOperationException($"{this} 没有下级。");

        return TryGetChild(ordinal, out var child)
            ? child
            : throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"序号必须在 [1, {MaxOrdinal}] 之间。");
    }

    /// <summary>
    /// 上一级地址。系统根与空地址没有上级。
    /// </summary>
    public bool TryGetParent(out TopologyAddress parent)
    {
        if (IsEmpty || Level == TopologyLevel.System)
        {
            parent = default;
            return false;
        }

        parent = AncestorAt(Level - 1);
        return true;
    }

    /// <summary>
    /// 截断到指定层级的祖先（含自身层级），如产品位地址取 <see cref="TopologyLevel.Cabinet"/> 得到所在柜体。
    /// </summary>
    public TopologyAddress AncestorAt(TopologyLevel level)
    {
        if (IsEmpty)
            throw new InvalidOperationException("空地址没有祖先。");

        if (!level.IsDefined() || level > Level)
            throw new ArgumentOutOfRangeException(nameof(level), level, $"层级必须在 [System, {Level}] 之间。");

        return new TopologyAddress(
            level,
            level >= TopologyLevel.Cabinet ? Cabinet : 0,
            level >= TopologyLevel.Layer ? Layer : 0,
            level >= TopologyLevel.Slot ? Slot : 0,
            level >= TopologyLevel.Fixture ? Fixture : 0,
            level >= TopologyLevel.Product ? Product : 0);
    }

    /// <summary>
    /// 是否为 <paramref name="other"/> 的严格祖先（不含自身）
    /// </summary>
    public bool IsAncestorOf(TopologyAddress other)
        => !IsEmpty && !other.IsEmpty && Level < other.Level && other.AncestorAt(Level) == this;

    /// <summary>
    /// <paramref name="other"/> 是否在以本地址为根的子树内（含自身）
    /// </summary>
    public bool Contains(TopologyAddress other) => !IsEmpty && (this == other || IsAncestorOf(other));

    public int CompareTo(TopologyAddress other)
    {
        var compare = Cabinet.CompareTo(other.Cabinet);
        if (compare != 0) return compare;
        compare = Layer.CompareTo(other.Layer);
        if (compare != 0) return compare;
        compare = Slot.CompareTo(other.Slot);
        if (compare != 0) return compare;
        compare = Fixture.CompareTo(other.Fixture);
        if (compare != 0) return compare;
        compare = Product.CompareTo(other.Product);
        if (compare != 0) return compare;

        // 序号全同只可能是"空地址 vs 系统根"，空地址排前
        return Level.CompareTo(other.Level);
    }

    /// <summary>
    /// 规范文本：系统根为 SYS，其余为 C01-L02-S03-F01-P04（序号至少两位，不足补零）。
    /// 该格式会写入日志与数据库，一经发布不得修改。
    /// </summary>
    public override string ToString()
    {
        if (IsEmpty)
            return EmptyText;

        if (Level == TopologyLevel.System)
            return SystemText;

        var builder = new StringBuilder(20);
        for (var level = TopologyLevel.Cabinet; level <= Level; level++)
        {
            if (builder.Length > 0)
                builder.Append('-');

            builder.Append(level.AddressPrefix())
                .Append(GetOrdinal(level).ToString("D2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解析规范文本，仅用于可信输入，格式非法时抛出 <see cref="FormatException"/>。
    /// </summary>
    public static TopologyAddress Parse(string text)
        => TryParse(text, out var address)
            ? address
            : throw new FormatException($"无法解析拓扑地址：\"{text}\"。");

    /// <summary>
    /// 解析规范文本。忽略首尾空白与前缀大小写，序号允许不补零（C1-L2 等价于 C01-L02）；
    /// 层级必须自柜体起逐级连续，不允许跳级或乱序。
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out TopologyAddress address)
    {
        address = default;
        if (text is null)
            return false;

        var span = text.AsSpan().Trim();
        if (span.IsEmpty)
            return false;

        if (span.Equals(SystemText, StringComparison.OrdinalIgnoreCase))
        {
            address = System;
            return true;
        }

        Span<int> ordinals = stackalloc int[TopologyLevels.Count - 1];
        var depth = 0;
        foreach (var range in span.Split('-'))
        {
            if (depth == ordinals.Length)
                return false;

            var segment = span[range];
            var expected = (TopologyLevel)(depth + (int)TopologyLevel.Cabinet);
            if (segment.Length < 2 || char.ToUpperInvariant(segment[0]) != expected.AddressPrefix())
                return false;

            // NumberStyles.None：只接受十进制数字，拒绝符号、空白与千分位
            if (!int.TryParse(segment[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                return false;

            ordinals[depth++] = ordinal;
        }

        return TryCreate(ordinals[..depth], out address);
    }
}
