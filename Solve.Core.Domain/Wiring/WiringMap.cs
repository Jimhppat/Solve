using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Domain.Wiring;

/// <summary>
/// 一条有效接线及其落点：<see cref="Source"/> 是接线实际所在的位置（可能是查询位置的上级）
/// </summary>
public readonly record struct EffectiveWire(Wire Wire, TopologyNode Source);

/// <summary>
/// 产品位的一个接点及满足它的有效接线；<see cref="Wire"/> 为 null 表示还没接
/// </summary>
public readonly record struct PortCoverage(RequiredPort Required, EffectiveWire? Wire)
{
    public bool IsConnected => Wire is not null;
}

/// <summary>
/// 整台设备的接线快照（数字孪生里"线怎么接"）：按拓扑定位每条接线，并负责接线规则的校验。
/// </summary>
/// <remarks>
/// <para>规则（结构性，与驱动无关）：</para>
/// <list type="bullet">
/// <item>落点必须在当前拓扑中，且不是系统根。</item>
/// <item>通道独占：一路通道只能接到一个位置（一根线只到一个地方）；几个位置共用一路时接在它们的上级。</item>
/// <item>路径唯一：同一条位置路径（祖先 ↔ 后代，含同一位置）上，同一功能 + 同一用途只能有一路。
/// 否则产品会同时从上级和自身得到两路供电 / 负载，或采集点含糊不清。</item>
/// </list>
/// <para>通道是否在驱动中声明、功能是否与声明一致，需要查驱动目录，由应用服务在调用前校验。</para>
/// <para>不可变：通信、业务、UI 线程可无锁并发读取；变更即生成新实例整体替换。</para>
/// </remarks>
public sealed class WiringMap
{
    private static readonly IReadOnlyList<Wire> NoWires = [];

    private readonly Dictionary<TopologyNodeId, Wire[]> _byNode;
    private readonly Dictionary<ChannelRef, Wire> _byChannel;
    private readonly Dictionary<WireId, Wire> _byId;

    private WiringMap(EquipmentTopology? topology, List<Wire> attached, List<Wire> detached)
    {
        Topology = topology;
        Detached = detached;

        // 每个位置的接线按功能、用途、通道排序：界面列表与日志顺序稳定
        attached.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0)
                return byKind;
            var byPurpose = string.CompareOrdinal(a.PurposeKey, b.PurposeKey);
            if (byPurpose != 0)
                return byPurpose;
            var byGroup = string.CompareOrdinal(a.Channel.Group, b.Channel.Group);
            return byGroup != 0 ? byGroup : a.Channel.Index.CompareTo(b.Channel.Index);
        });

        All = attached;
        _byId = attached.ToDictionary(w => w.Id);
        _byChannel = attached.ToDictionary(w => w.Channel);
        _byNode = attached.GroupBy(w => w.Node).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public static WiringMap Empty { get; } = new(null, [], []);

    /// <summary>
    /// 快照所依据的拓扑；尚未建档或读取失败时为 null（此时全部接线都在 <see cref="Detached"/> 中）
    /// </summary>
    public EquipmentTopology? Topology { get; }

    /// <summary>
    /// 落在当前拓扑中的接线
    /// </summary>
    public IReadOnlyList<Wire> All { get; }

    /// <summary>
    /// 无法落位的接线：位置已不在拓扑中、落在系统根，或与先读到的接线争用同一路通道。不参与任何查询，供日志与诊断。
    /// </summary>
    public IReadOnlyList<Wire> Detached { get; }

    public int Count => All.Count;

    /// <summary>
    /// 按拓扑定位一组接线（如从存储读回）。不校验路径唯一：读回的数据按原样显示，规则只拦新的修改。
    /// </summary>
    public static WiringMap Create(EquipmentTopology? topology, IEnumerable<Wire> wires)
    {
        ArgumentNullException.ThrowIfNull(wires);

        var attached = new List<Wire>();
        var detached = new List<Wire>();
        var channels = new HashSet<ChannelRef>();
        foreach (var wire in wires)
        {
            ArgumentNullException.ThrowIfNull(wire);
            var placed = topology is not null
                && topology.TryGetNode(wire.Node, out var node)
                && node.Level != TopologyLevel.System
                && channels.Add(wire.Channel);
            (placed ? attached : detached).Add(wire);
        }

        return new WiringMap(topology, attached, detached);
    }

    public bool TryGet(WireId id, [NotNullWhen(true)] out Wire? wire) => _byId.TryGetValue(id, out wire);

    public bool TryGetByChannel(ChannelRef channel, [NotNullWhen(true)] out Wire? wire) => _byChannel.TryGetValue(channel, out wire);

    /// <summary>
    /// 直接接在该位置上的接线
    /// </summary>
    public IReadOnlyList<Wire> GetOwn(TopologyNodeId node) => _byNode.TryGetValue(node, out var wires) ? wires : NoWires;

    /// <summary>
    /// 该位置的有效接线：从柜体起逐级向下的上级接线，最后是它自身的接线。位置不在拓扑中时为空。
    /// </summary>
    public IReadOnlyList<EffectiveWire> GetEffective(TopologyNodeId nodeId)
    {
        if (Topology is not { } topology || !topology.TryGetNode(nodeId, out var target) || _byNode.Count == 0)
            return [];

        var result = new List<EffectiveWire>();
        for (var level = TopologyLevel.Cabinet; level <= target.Level; level++)
        {
            if (!topology.TryGetNode(target.Address.AncestorAt(level), out var source))
                continue;

            foreach (var wire in GetOwn(source.Id))
                result.Add(new EffectiveWire(wire, source));
        }

        return result;
    }

    /// <summary>
    /// 产品位的接点核对：它要求的每个接点（自身与上级声明的）由哪一路有效接线满足，未满足的为 null。
    /// 只核对产品位（见 <see cref="PortGroup"/>）；不是产品位、不在拓扑中或没有接点要求时为空列表。
    /// </summary>
    public IReadOnlyList<PortCoverage> GetPortCoverage(TopologyNodeId nodeId)
    {
        if (Topology is not { } topology || !topology.TryGetNode(nodeId, out var node)
            || node.Level != TopologyLevel.Product || node.RequiredPorts.Count == 0)
        {
            return [];
        }

        var effective = GetEffective(nodeId);
        var result = new PortCoverage[node.RequiredPorts.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var required = node.RequiredPorts[i];

            // 路径唯一规则保证同一（功能，用途）在一条路径上最多一路，取第一路即可
            EffectiveWire? match = null;
            foreach (var wire in effective)
            {
                if (required.Port.Matches(wire.Wire.Kind, wire.Wire.PurposeKey))
                {
                    match = wire;
                    break;
                }
            }

            result[i] = new PortCoverage(required, match);
        }

        return result;
    }

    /// <summary>
    /// 接在该位置下级（不含自身）上的接线数
    /// </summary>
    public int CountBelow(TopologyNodeId nodeId)
    {
        if (Topology is not { } topology || !topology.TryGetNode(nodeId, out var target))
            return 0;

        var count = 0;
        foreach (var (id, wires) in _byNode)
        {
            if (topology.TryGetNode(id, out var node) && target.Address.IsAncestorOf(node.Address))
                count += wires.Length;
        }

        return count;
    }

    /// <summary>
    /// 一个模块上已接线的通道
    /// </summary>
    public IReadOnlyList<Wire> GetByDevice(DeviceId device) => All.Where(w => w.Channel.Device == device).ToArray();

    /// <summary>
    /// 换成 <paramref name="candidate"/> 拓扑后会失去落点（随位置一起删除）的接线数，供删除位置前的确认提示
    /// </summary>
    public int CountRemovedBy(EquipmentTopology candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return All.Count(w => !candidate.TryGetNode(w.Node, out _));
    }

    /// <summary>
    /// 新增一批接线：逐条校验落点、通道独占与路径唯一（含批内彼此之间），全部通过才返回新快照。
    /// </summary>
    /// <param name="describe">把通道写成人读的文字（如"电源-01 输出"），用于冲突说明；不提供时用组标识与通道号</param>
    public Result<WiringMap> WithAdded(IReadOnlyList<Wire> added, Func<ChannelRef, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(added);
        if (Topology is not { } topology)
            return Result<WiringMap>.Failure(WiringErrors.NoTopology());

        var problems = Check(added, describe);
        if (problems.FirstOrDefault(p => p?.Code is WiringErrors.NodeNotFoundCode or WiringErrors.SystemNodeCode) is { } placement)
            return Result<WiringMap>.Failure(placement);

        var fieldErrors = added.SelectMany(w => w.Validate()).DistinctBy(e => e.Code).ToList();
        if (fieldErrors.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.InvalidWire(fieldErrors));

        // 逐行的冲突说明合成一条：先报通道占用，再报路径冲突
        var channelConflicts = problems.Where(p => p?.Code == WiringErrors.ChannelInUseCode).Select(p => p!.Message).ToList();
        if (channelConflicts.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.ChannelInUse(channelConflicts));
        var pathConflicts = problems.Where(p => p?.Code == WiringErrors.PathConflictCode).Select(p => p!.Message).ToList();
        if (pathConflicts.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.PathConflict(pathConflicts));

        return Result<WiringMap>.Success(new WiringMap(topology, [.. All, .. added], [.. Detached]));
    }

    /// <summary>
    /// 逐条校验一批待新增的接线（不改变快照），返回与 <paramref name="added"/> 一一对应的问题，没有问题为 null。
    /// 批内按顺序判定：先出现的一条占用通道 / 路径，后面与它冲突的那条报错。用于映射草稿逐行标出问题。
    /// </summary>
    /// <remarks>
    /// 各行问题的 Code：Wiring.NodeNotFound、Wiring.SystemNode、Wiring.InvalidWire（字段），
    /// Wiring.ChannelInUse、Wiring.PathConflict（Message 是这一行的冲突说明，不是汇总文字）。
    /// </remarks>
    public IReadOnlyList<Error?> Check(IReadOnlyList<Wire> added, Func<ChannelRef, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(added);
        describe ??= static channel => $"通道 {channel.Group} {channel.Index}";

        var problems = new Error?[added.Count];
        if (Topology is not { } topology)
        {
            Array.Fill(problems, WiringErrors.NoTopology());
            return problems;
        }

        var byChannel = new Dictionary<ChannelRef, (Wire Wire, TopologyNode Node)>();
        foreach (var wire in All)
        {
            if (topology.TryGetNode(wire.Node, out var node))
                byChannel[wire.Channel] = (wire, node);
        }

        var byKey = BuildKeyIndex(topology, All);
        for (var i = 0; i < added.Count; i++)
        {
            var wire = added[i] ?? throw new ArgumentException("接线不能为 null。", nameof(added));
            if (!topology.TryGetNode(wire.Node, out var node))
            {
                problems[i] = WiringErrors.NodeNotFound();
                continue;
            }

            if (node.Level == TopologyLevel.System)
            {
                problems[i] = WiringErrors.SystemNode();
                continue;
            }

            if (wire.Validate() is { Count: > 0 } errors)
            {
                problems[i] = WiringErrors.InvalidWire(errors);
                continue;
            }

            if (byChannel.TryGetValue(wire.Channel, out var holder))
            {
                problems[i] = new Error(WiringErrors.ChannelInUseCode, $"{describe(wire.Channel)} 已接到 {holder.Node.Address}");
                continue;
            }

            if (FindPathConflict(byKey, wire, node) is { } other)
            {
                problems[i] = new Error(WiringErrors.PathConflictCode, $"{node.Address} 与 {other.Node.Address} 都有「{wire.KindText}」");
                continue;
            }

            byChannel[wire.Channel] = (wire, node);
            AddToKeyIndex(byKey, wire, node);
        }

        return problems;
    }

    /// <summary>
    /// 修改接线的用途与线号（落点与通道不变）。改用途可能与同一路径上的其他接线冲突，同样校验。
    /// </summary>
    public Result<WiringMap> WithUpdated(WireId id, string? purpose, string? label)
    {
        if (Topology is not { } topology)
            return Result<WiringMap>.Failure(WiringErrors.NoTopology());
        if (!_byId.TryGetValue(id, out var existing) || !topology.TryGetNode(existing.Node, out var node))
            return Result<WiringMap>.Failure(WiringErrors.WireNotFound());

        var updated = existing with { Purpose = Wire.NormalizeText(purpose), Label = Wire.NormalizeText(label) };
        var errors = updated.Validate();
        if (errors.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.InvalidWire(errors));

        var others = All.Where(w => w.Id != id).ToList();
        if (FindPathConflict(BuildKeyIndex(topology, others), updated, node) is { } other)
            return Result<WiringMap>.Failure(WiringErrors.PathConflict([$"{node.Address} 与 {other.Node.Address} 都有「{updated.KindText}」"]));

        others.Add(updated);
        return Result<WiringMap>.Success(new WiringMap(topology, others, [.. Detached]));
    }

    /// <summary>
    /// 拆除一批接线；不存在的 Id 忽略
    /// </summary>
    public WiringMap WithRemoved(IReadOnlyCollection<WireId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var removed = ids.ToHashSet();
        return new WiringMap(Topology, All.Where(w => !removed.Contains(w.Id)).ToList(), [.. Detached]);
    }

    // ==================== 路径唯一 ====================

    private static PathIndex BuildKeyIndex(EquipmentTopology topology, IEnumerable<Wire> wires)
    {
        var index = new PathIndex();
        foreach (var wire in wires)
        {
            if (topology.TryGetNode(wire.Node, out var node))
                index.Add(wire, node);
        }

        return index;
    }

    private static void AddToKeyIndex(PathIndex index, Wire wire, TopologyNode node) => index.Add(wire, node);

    private static (Wire Wire, TopologyNode Node)? FindPathConflict(PathIndex index, Wire wire, TopologyNode node)
        => index.FindConflict(wire, node);

    /// <summary>
    /// 按（功能，用途）分组的路径索引。查一个位置是否与已有接线在同一路径上只需 O(层数)：
    /// 记下每条接线的落点，以及落点的每个上级"下面有接线"。批量接线上万路时不会退化成两两比较。
    /// </summary>
    private sealed class PathIndex
    {
        private readonly Dictionary<(ChannelKind, string), Dictionary<TopologyAddress, (Wire, TopologyNode)>> _wiredAt = [];
        private readonly Dictionary<(ChannelKind, string), Dictionary<TopologyAddress, (Wire, TopologyNode)>> _wiredBelow = [];

        public void Add(Wire wire, TopologyNode node)
        {
            var key = (wire.Kind, wire.PurposeKey);
            Get(_wiredAt, key).TryAdd(node.Address, (wire, node));

            var below = Get(_wiredBelow, key);
            for (var level = TopologyLevel.Cabinet; level < node.Level; level++)
                below.TryAdd(node.Address.AncestorAt(level), (wire, node));
        }

        /// <summary>
        /// 同一功能、同一用途的接线中，落点与 <paramref name="node"/> 在同一条路径上（自身或上级有，或下级有）的一条
        /// </summary>
        public (Wire Wire, TopologyNode Node)? FindConflict(Wire wire, TopologyNode node)
        {
            var key = (wire.Kind, wire.PurposeKey);
            if (_wiredAt.TryGetValue(key, out var wiredAt))
            {
                for (var level = TopologyLevel.Cabinet; level <= node.Level; level++)
                {
                    if (wiredAt.TryGetValue(node.Address.AncestorAt(level), out var found))
                        return found;
                }
            }

            return _wiredBelow.TryGetValue(key, out var below) && below.TryGetValue(node.Address, out var under)
                ? under
                : null;
        }

        private static Dictionary<TopologyAddress, (Wire, TopologyNode)> Get(
            Dictionary<(ChannelKind, string), Dictionary<TopologyAddress, (Wire, TopologyNode)>> map,
            (ChannelKind, string) key)
        {
            if (!map.TryGetValue(key, out var inner))
                map[key] = inner = [];
            return inner;
        }
    }
}
