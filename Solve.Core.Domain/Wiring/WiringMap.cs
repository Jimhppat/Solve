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
/// <item>同一路通道在同一个位置只接一次。一路通道可以接到多个位置（共用），同一路径上也可以有多路相同功能、相同用途的接线：
/// 映射不做防呆拦截。按（功能，用途）下发命令时如果匹配到多路，由命令服务拒绝（无法确定目标）。</item>
/// </list>
/// <para>通道是否在驱动中声明、功能是否与声明一致，需要查驱动目录，由应用服务在调用前校验。</para>
/// <para>不可变：通信、业务、UI 线程可无锁并发读取；变更即生成新实例整体替换。</para>
/// </remarks>
public sealed class WiringMap
{
    private static readonly IReadOnlyList<Wire> NoWires = [];

    private readonly Dictionary<TopologyNodeId, Wire[]> _byNode;
    private readonly Dictionary<ChannelRef, Wire[]> _byChannel;
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
        _byChannel = attached.GroupBy(w => w.Channel).ToDictionary(g => g.Key, g => g.ToArray());
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
    /// 无法落位的接线：位置已不在拓扑中、落在系统根，或与先读到的接线在同一位置重复接同一路通道。不参与任何查询，供日志与诊断。
    /// </summary>
    public IReadOnlyList<Wire> Detached { get; }

    public int Count => All.Count;

    /// <summary>
    /// 按拓扑定位一组接线（如从存储读回）
    /// </summary>
    public static WiringMap Create(EquipmentTopology? topology, IEnumerable<Wire> wires)
    {
        ArgumentNullException.ThrowIfNull(wires);

        var attached = new List<Wire>();
        var detached = new List<Wire>();
        var placedChannels = new HashSet<(TopologyNodeId, ChannelRef)>();
        foreach (var wire in wires)
        {
            ArgumentNullException.ThrowIfNull(wire);
            var placed = topology is not null
                && topology.TryGetNode(wire.Node, out var node)
                && node.Level != TopologyLevel.System
                && placedChannels.Add((wire.Node, wire.Channel));
            (placed ? attached : detached).Add(wire);
        }

        return new WiringMap(topology, attached, detached);
    }

    public bool TryGet(WireId id, [NotNullWhen(true)] out Wire? wire) => _byId.TryGetValue(id, out wire);

    /// <summary>
    /// 接在这路通道上的全部接线（一路通道可以接到多个位置）；没接为空
    /// </summary>
    public IReadOnlyList<Wire> GetByChannel(ChannelRef channel) => _byChannel.TryGetValue(channel, out var wires) ? wires : NoWires;

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

        // 两轮配对，每路接线只满足一个接点：先按（功能，用途）配；剩下的接点再只按用途配还没用过的接线
        // （映射不按功能拦截，接点名对上即视为接上，功能不同时界面上照样显示接线的实际功能）
        var effective = GetEffective(nodeId);
        var result = new PortCoverage[node.RequiredPorts.Count];
        var used = new bool[effective.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new PortCoverage(node.RequiredPorts[i], Take(i, sameKind: true));
        for (var i = 0; i < result.Length; i++)
        {
            if (!result[i].IsConnected)
                result[i] = result[i] with { Wire = Take(i, sameKind: false) };
        }

        return result;

        EffectiveWire? Take(int port, bool sameKind)
        {
            var required = node.RequiredPorts[port].Port;
            for (var w = 0; w < effective.Count; w++)
            {
                var wire = effective[w].Wire;
                if (!used[w] && wire.PurposeKey == required.PurposeKey && (!sameKind || wire.Kind == required.Kind))
                {
                    used[w] = true;
                    return effective[w];
                }
            }

            return null;
        }
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
    /// 新增一批接线：逐条校验落点、字段与"同一位置不重复接同一路通道"（含批内彼此之间），全部通过才返回新快照。
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

        // 逐行的重复说明合成一条
        var duplicates = problems.Where(p => p?.Code == WiringErrors.ChannelInUseCode).Select(p => p!.Message).ToList();
        if (duplicates.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.ChannelInUse(duplicates));

        return Result<WiringMap>.Success(new WiringMap(topology, [.. All, .. added], [.. Detached]));
    }

    /// <summary>
    /// 逐条校验一批待新增的接线（不改变快照），返回与 <paramref name="added"/> 一一对应的问题，没有问题为 null。
    /// 批内按顺序判定：先出现的一条先接上，后面在同一位置重复接同一路通道的那条报错。用于映射草稿逐行标出问题。
    /// </summary>
    /// <remarks>
    /// 各行问题的 Code：Wiring.NodeNotFound、Wiring.SystemNode、Wiring.InvalidWire（字段），
    /// Wiring.ChannelInUse（同一位置重复接同一路通道；Message 是这一行的说明，不是汇总文字）。
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

        var placed = All.Select(w => (w.Node, w.Channel)).ToHashSet();
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

            if (!placed.Add((wire.Node, wire.Channel)))
                problems[i] = new Error(WiringErrors.ChannelInUseCode, $"{describe(wire.Channel)} 已接在 {node.Address}");
        }

        return problems;
    }

    /// <summary>
    /// 修改接线的用途与线号（落点与通道不变）
    /// </summary>
    public Result<WiringMap> WithUpdated(WireId id, string? purpose, string? label)
    {
        if (Topology is not { } topology)
            return Result<WiringMap>.Failure(WiringErrors.NoTopology());
        if (!_byId.TryGetValue(id, out var existing) || !topology.TryGetNode(existing.Node, out _))
            return Result<WiringMap>.Failure(WiringErrors.WireNotFound());

        var updated = existing with { Purpose = Wire.NormalizeText(purpose), Label = Wire.NormalizeText(label) };
        var errors = updated.Validate();
        if (errors.Count > 0)
            return Result<WiringMap>.Failure(WiringErrors.InvalidWire(errors));

        var others = All.Where(w => w.Id != id).ToList();
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
}
