using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 整台设备的六层拓扑（数字孪生的静态骨架）：系统 → 柜体 → 层位 → 槽位 → 治具位 → 产品位。
/// </summary>
/// <remarks>
/// <para>只描述"设备有哪些位置、谁属于谁、哪些被停用"，不包含通信、运行状态或测试流程，与总线等模块无依赖。</para>
/// <para>创建后完全不可变，通信、业务、存储、UI 线程可无锁并发读取；结构变更即重新 <see cref="Create(TopologyDefinition)"/>
/// 一个新实例整体替换。</para>
/// <para>查询开销：按地址定位 O(层数 × log 扇出)；按层或按子树取节点为二分定位 + 连续切片，不遍历整棵树。</para>
/// </remarks>
public sealed class EquipmentTopology
{
    public const int MaxNameLength = 64;

    /// <summary>
    /// 节点总数上限（含系统根）。按每节点约百余字节估算，上限时占用约 20–30 MB；
    /// 用于拦截错误配置（如各层都填 999）把进程内存耗尽。
    /// </summary>
    public const int MaxNodeCount = 200_000;

    /// <summary>
    /// 校验失败时最多列出的错误数。同一处笔误在大树中可能重复上万次，全部列出会淹没日志与界面。
    /// </summary>
    public const int MaxReportedErrors = 50;

    // 下标为 (int)level - 1；每层按地址升序（即先序遍历顺序）
    private readonly TopologyNode[][] _nodesByLevel;
    private readonly ReadOnlyCollection<TopologyNode>[] _readOnlyByLevel;

    private EquipmentTopology(TopologyNode root, TopologyNode[][] nodesByLevel)
    {
        Root = root;
        _nodesByLevel = nodesByLevel;
        _readOnlyByLevel = Array.ConvertAll(nodesByLevel, Array.AsReadOnly);
        NodeCount = nodesByLevel.Sum(nodes => nodes.Length);
    }

    /// <summary>
    /// 系统名称
    /// </summary>
    public string Name => Root.Name!;

    public TopologyNode Root { get; }

    /// <summary>
    /// 节点总数（含系统根）
    /// </summary>
    public int NodeCount { get; }

    /// <summary>
    /// 校验并创建拓扑。所有错误一次性收集（便于 UI 同时标出多处），失败时整体拒绝，不会得到"部分有效"的拓扑。
    /// </summary>
    /// <param name="definition">不可信的结构定义</param>
    /// <param name="problems">全部错误（最多 <see cref="MaxReportedErrors"/> 条），成功时为空</param>
    public static Result<EquipmentTopology> Create(TopologyDefinition definition, out IReadOnlyList<Error> problems)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new Builder();
        var root = builder.BuildRoot(definition);
        problems = builder.Errors;

        if (builder.TotalErrorCount > 0)
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidDefinition(builder.Errors, builder.TotalErrorCount));

        return Result<EquipmentTopology>.Success(new EquipmentTopology(root, IndexByLevel(root)));
    }

    /// <inheritdoc cref="Create(TopologyDefinition, out IReadOnlyList{Error})"/>
    public static Result<EquipmentTopology> Create(TopologyDefinition definition) => Create(definition, out _);

    /// <summary>
    /// 指定层级的全部节点，按地址升序
    /// </summary>
    public IReadOnlyList<TopologyNode> GetNodes(TopologyLevel level) => _readOnlyByLevel[LevelIndex(level)];

    /// <summary>
    /// 指定层级的节点数（含停用节点）
    /// </summary>
    public int CountAt(TopologyLevel level) => _nodesByLevel[LevelIndex(level)].Length;

    public bool TryGetNode(TopologyAddress address, [NotNullWhen(true)] out TopologyNode? node)
        => Locate(address, out node, out _);

    public Result<TopologyNode> GetNode(TopologyAddress address)
        => TryGetNode(address, out var node)
            ? Result<TopologyNode>.Success(node)
            : Result<TopologyNode>.Failure(TopologyErrors.NodeNotFound(address));

    /// <summary>
    /// 子树内位于 <paramref name="level"/> 层的全部节点（含停用节点），按地址升序，
    /// 如"2 号柜的全部产品位"。<paramref name="level"/> 与 <paramref name="ancestor"/> 同层时只返回它自己。
    /// 祖先不存在时返回空列表。
    /// </summary>
    public IReadOnlyList<TopologyNode> GetDescendants(TopologyAddress ancestor, TopologyLevel level)
    {
        var nodes = _nodesByLevel[LevelIndex(level)];
        if (ancestor.IsEmpty)
            return ReadOnlyCollection<TopologyNode>.Empty;

        if (level < ancestor.Level)
            throw new ArgumentOutOfRangeException(nameof(level), level, $"层级不能高于祖先所在的 {ancestor.Level}。");

        // 地址序 = 先序遍历序，子树在每一层上都是连续的一段：
        // 起点是第一个 ≥ 祖先地址的节点，终点是第一个"排在祖先之后且不属于该子树"的节点
        var start = PartitionPoint(nodes, 0, node => node.Address.CompareTo(ancestor) >= 0);
        var end = PartitionPoint(nodes, start, node => !ancestor.Contains(node.Address));

        return end == start
            ? ReadOnlyCollection<TopologyNode>.Empty
            : new ReadOnlyCollection<TopologyNode>(new ArraySegment<TopologyNode>(nodes, start, end - start));
    }

    /// <summary>
    /// 联锁前置检查：位置存在，且它自身及全部祖先都已启用。
    /// </summary>
    /// <remarks>
    /// 只回答"结构上是否允许"，不代表设备在线、无故障。向位置下发命令前还必须检查通信链路与运行状态。
    /// 拒绝时给出自上而下第一个被停用的节点，便于操作员定位是哪一级屏蔽的。
    /// </remarks>
    public Result CheckEnabled(TopologyAddress address)
    {
        if (!Locate(address, out _, out var firstDisabled))
            return Result.Failure(TopologyErrors.NodeNotFound(address));

        return firstDisabled is null
            ? Result.Success()
            : Result.Failure(TopologyErrors.NodeDisabled(address, firstDisabled.Address));
    }

    public override string ToString() => $"{Name}（{NodeCount} 个节点）";

    private bool Locate(
        TopologyAddress address,
        [NotNullWhen(true)] out TopologyNode? node,
        out TopologyNode? firstDisabled)
    {
        node = null;
        firstDisabled = null;
        if (address.IsEmpty)
            return false;

        var current = Root;
        for (var level = TopologyLevel.Cabinet; level <= address.Level; level++)
        {
            if (!current.TryGetChild(address.GetOrdinal(level), out var child))
            {
                firstDisabled = null;
                return false;
            }

            current = child;
            if (firstDisabled is null && !current.IsEnabled)
                firstDisabled = current;
        }

        node = current;
        return true;
    }

    private static int LevelIndex(TopologyLevel level)
        => level.IsDefined()
            ? (int)level - 1
            : throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的拓扑层级。");

    /// <summary>
    /// 在 [from, nodes.Length) 上找第一个满足谓词的下标。要求谓词在该区间内单调（先 false 后 true）。
    /// </summary>
    private static int PartitionPoint(TopologyNode[] nodes, int from, Func<TopologyNode, bool> predicate)
    {
        var low = from;
        var high = nodes.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (predicate(nodes[mid]))
                high = mid;
            else
                low = mid + 1;
        }

        return low;
    }

    private static TopologyNode[][] IndexByLevel(TopologyNode root)
    {
        var lists = new List<TopologyNode>[TopologyLevels.Count];
        for (var i = 0; i < lists.Length; i++)
            lists[i] = [];

        // 先序遍历 + 子节点已按序号排序 => 每层列表天然按地址升序
        var stack = new Stack<TopologyNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            lists[(int)node.Level - 1].Add(node);
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }

        return Array.ConvertAll(lists, list => list.ToArray());
    }

    /// <summary>
    /// 一次遍历完成校验与构建。树深固定为 6，递归深度有界。
    /// </summary>
    private sealed class Builder
    {
        private readonly List<Error> _errors = [];
        private int _nodeCount = 1; // 系统根
        private bool _aborted;

        public IReadOnlyList<Error> Errors => _errors;

        public int TotalErrorCount { get; private set; }

        public TopologyNode BuildRoot(TopologyDefinition definition)
        {
            string? name = null;
            if (string.IsNullOrWhiteSpace(definition.Name))
                AddError(TopologyErrors.NameField, "系统名称不能为空。");
            else
                name = CheckName(TopologyAddress.System, definition.Name);

            // 系统根不可停用：停用整台设备不属于拓扑配置，应由上层的急停 / 安全停机处理
            var cabinets = BuildChildren(TopologyAddress.System, definition.Cabinets, parentEffectivelyEnabled: true);
            return new TopologyNode(TopologyAddress.System, name, isEnabled: true, isEffectivelyEnabled: true, cabinets);
        }

        private TopologyNode[] BuildChildren(
            TopologyAddress parent,
            IReadOnlyList<TopologyNodeDefinition>? definitions,
            bool parentEffectivelyEnabled)
        {
            // 反序列化可能把列表置为 null，不能信任可空注解
            if (definitions is null)
            {
                AddError(TopologyErrors.ChildrenField, $"{parent} 的下级列表为空引用，没有下级时应为空列表。");
                return [];
            }

            if (!parent.Level.TryGetChildLevel(out var childLevel))
            {
                if (definitions.Count > 0)
                    AddError(TopologyErrors.DepthField, $"{parent} 是产品位（最底层），不能再有下级，当前定义了 {definitions.Count} 个。");
                return [];
            }

            var ordinals = new HashSet<int>();
            var built = new List<TopologyNode>(definitions.Count);
            for (var i = 0; i < definitions.Count && !_aborted; i++)
            {
                var definition = definitions[i];
                if (definition is null)
                {
                    AddError(TopologyErrors.ChildrenField, $"{parent} 的第 {i + 1} 个{childLevel.DisplayName()}定义为空引用。");
                    continue;
                }

                if (!parent.TryGetChild(definition.Ordinal, out var address))
                {
                    AddError(TopologyErrors.OrdinalField,
                        $"{parent} 下的{childLevel.DisplayName()}序号必须在 [1, {TopologyAddress.MaxOrdinal}] 之间，当前为 {definition.Ordinal}。");
                    continue;
                }

                if (!ordinals.Add(definition.Ordinal))
                {
                    AddError(TopologyErrors.DuplicateOrdinalField, $"{childLevel.DisplayName()} {address} 重复定义。");
                    continue;
                }

                if (++_nodeCount > MaxNodeCount)
                {
                    AddError(TopologyErrors.NodeLimitField, $"节点总数超过上限 {MaxNodeCount}，已停止校验。");
                    _aborted = true;
                    break;
                }

                var name = definition.Name is null ? null : CheckName(address, definition.Name);
                var effectivelyEnabled = parentEffectivelyEnabled && definition.Enabled;
                var children = BuildChildren(address, definition.Children, effectivelyEnabled);
                built.Add(new TopologyNode(address, name, definition.Enabled, effectivelyEnabled, children));
            }

            built.Sort(static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
            return built.ToArray();
        }

        /// <summary>
        /// 去除首尾空白；空白名视为未配置（返回 null，界面回退显示地址）
        /// </summary>
        private string? CheckName(TopologyAddress address, string name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength)
            {
                AddError(TopologyErrors.NameField, $"{address} 的名称不能超过 {MaxNameLength} 个字符。");
                return null;
            }

            return trimmed.Length == 0 ? null : trimmed;
        }

        private void AddError(string field, string message)
        {
            TotalErrorCount++;
            if (_errors.Count < MaxReportedErrors)
                _errors.Add(TopologyErrors.InvalidField(field, message));
        }
    }
}
