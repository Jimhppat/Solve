using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 整台设备的六层拓扑（数字孪生的静态骨架）：系统 → 柜体 → 层位 → 槽位 → 治具位 → 产品位。
/// </summary>
/// <remarks>
/// <para>只描述"设备有哪些位置、谁属于谁、哪些被停用"，不包含通信、运行状态或测试流程，与总线等模块无依赖。</para>
/// <para>双身份：每个节点同时有系统身份 <see cref="TopologyNodeId"/>（机器用，终身不变）与
/// 位置编码 <see cref="TopologyAddress"/>（人用，全树唯一，可随重新编号改变），两者都能 O(1)～O(层数) 定位节点。</para>
/// <para>创建后完全不可变，通信、业务、存储、UI 线程可无锁并发读取；结构变更即 <see cref="ToDefinition"/> 导出、
/// 修改后重新 <see cref="Create(TopologyDefinition)"/> 一个新实例整体替换。</para>
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

    // 只读冻结字典：构建一次，之后无锁并发查询
    private readonly FrozenDictionary<TopologyNodeId, TopologyNode> _nodesById;

    private EquipmentTopology(TopologyNode root, TopologyNode[][] nodesByLevel)
    {
        Root = root;
        _nodesByLevel = nodesByLevel;
        _readOnlyByLevel = Array.ConvertAll(nodesByLevel, Array.AsReadOnly);
        _nodesById = nodesByLevel.SelectMany(nodes => nodes).ToFrozenDictionary(node => node.Id);
        NodeCount = _nodesById.Count;
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
    /// 导出为结构定义（保留全部 Id、序号、名称与启用状态），用于修改后重建或持久化。
    /// 导出结果再 <see cref="Create(TopologyDefinition)"/> 得到等价的拓扑。
    /// </summary>
    public TopologyDefinition ToDefinition() => Export(node => (node.Name, node.IsEnabled));

    /// <summary>
    /// 修改单个节点的显示名与启用状态，得到一份新拓扑（本实例不变）。全部 Id、位置编码与其余节点保持不变。
    /// </summary>
    /// <param name="id">要修改的节点</param>
    /// <param name="name">新显示名；空白表示不命名（界面显示位置编码）。系统根的名称即系统名称，不能为空</param>
    /// <param name="enabled">是否启用；停用会让整棵子树都视为停用。系统根不可停用</param>
    public Result<EquipmentTopology> WithNodeSettings(TopologyNodeId id, string? name, bool enabled)
    {
        if (!TryGetNode(id, out var target))
            return Result<EquipmentTopology>.Failure(TopologyErrors.NodeNotFound(id));

        // 停用整台设备不属于拓扑配置，应由急停 / 安全停机处理（与 Create 中系统根恒为启用一致）
        if (target.Level == TopologyLevel.System && !enabled)
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidField(
                TopologyErrors.EnabledField, "系统根不可停用；需要整机停止时请使用紧急停止。"));
        }

        return Create(Export(node => node.Id == id ? (name, enabled) : (node.Name, node.IsEnabled)));
    }

    // ---------- 结构编辑 ----------
    // 每次得到一份新拓扑（本实例不变）。四种编辑都只改写某一个上级的下级列表，其余节点的 Id、序号、名称与启用状态原样导出，
    // 再经 Create 整树校验：序号范围、同级重复、Id 唯一、节点上限的规则只有一份。

    /// <summary>
    /// 在 <paramref name="parentId"/> 下新增 <paramref name="count"/> 个下级，序号从 <see cref="TopologyNode.NextChildOrdinal"/> 起连续编号，
    /// 每个新节点按 <paramref name="template"/> 带上下级。
    /// </summary>
    /// <param name="enabled">
    /// 新节点自身是否启用。新位置在现场调试完成前应保持停用（故障安全方向），由操作员确认后再启用；
    /// 新节点的下级都是启用，统一由新节点屏蔽。
    /// </param>
    public Result<EquipmentTopology> WithChildrenAdded(TopologyNodeId parentId, int count, TopologyTemplate template, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!TryGetNode(parentId, out var parent))
            return Result<EquipmentTopology>.Failure(TopologyErrors.NodeNotFound(parentId));

        if (!parent.Level.TryGetChildLevel(out var childLevel))
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit($"{parent.Address} 是产品位（最底层），不能新增下级。"));

        if (count < 1)
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit($"新增数量至少为 1，当前为 {count}。"));

        var templateCheck = template.Validate(childLevel);
        if (templateCheck.IsFailure)
            return Result<EquipmentTopology>.Failure(templateCheck.Error);

        var first = parent.NextChildOrdinal;
        var available = Math.Max(0, TopologyAddress.MaxOrdinal - first + 1);
        if (count > available)
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit(
                $"{parent.Address} 下的{childLevel.DisplayName()}序号已用到 {first - 1}，最多还能新增 {available} 个。"));
        }

        // 先按数量估算再生成：模板各层都填 999 时会有上万亿个节点，必须在分配内存之前拦住
        var added = count * template.NodeCountPerInstance;
        if (NodeCount + added > MaxNodeCount)
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit(
                $"新增 {added} 个节点后共 {NodeCount + added} 个，超过上限 {MaxNodeCount}。"));
        }

        var nodes = template.Build(parent.Address, first, count, enabled);
        return Rewrite(parentId, children => [.. children, .. nodes]);
    }

    /// <summary>
    /// 删除节点及其整棵子树。被删节点的 Id 作废，引用它们的历史数据只能按原 Id 追溯。系统根不能删除。
    /// </summary>
    public Result<EquipmentTopology> WithNodeRemoved(TopologyNodeId id)
    {
        if (!TryGetNonRoot(id, "删除", out var node, out var parent, out var error))
            return Result<EquipmentTopology>.Failure(error);

        return Rewrite(parent.Id, children => Array.FindAll(children, child => child.Id != node.Id));
    }

    /// <summary>
    /// 修改节点在上级中的序号：它和整棵子树的位置编码随之改变，Id 不变。新序号已被同级占用时拒绝，不做自动对调。
    /// 序号与原来相同时返回本实例。
    /// </summary>
    public Result<EquipmentTopology> WithOrdinal(TopologyNodeId id, int ordinal)
    {
        if (!TryGetNonRoot(id, "重新编号", out var node, out var parent, out var error))
            return Result<EquipmentTopology>.Failure(error);

        if (ordinal is < 1 or > TopologyAddress.MaxOrdinal)
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit(
                $"序号必须在 [1, {TopologyAddress.MaxOrdinal}] 之间，当前为 {ordinal}。"));
        }

        if (ordinal == node.Ordinal)
            return Result<EquipmentTopology>.Success(this);

        if (parent.TryGetChild(ordinal, out var occupant))
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit(
                $"{parent.Address} 下已有{occupant.Level.DisplayName()} {occupant.Address}，序号 {ordinal} 已被占用。"));
        }

        return Rewrite(parent.Id, children => Array.ConvertAll(children,
            child => child.Id == node.Id ? child with { Ordinal = ordinal } : child));
    }

    /// <summary>
    /// 把节点连同整棵子树复制为一个同级节点，放在 <see cref="TopologyNode.NextChildOrdinal"/>。
    /// 副本全部分配新 Id；下级的名称与启用状态照抄，副本自身不带名称（它是另一个物理位置），启用状态由 <paramref name="enabled"/> 决定。
    /// </summary>
    public Result<EquipmentTopology> WithNodeCopied(TopologyNodeId id, bool enabled)
    {
        if (!TryGetNonRoot(id, "复制", out var node, out var parent, out var error))
            return Result<EquipmentTopology>.Failure(error);

        var ordinal = parent.NextChildOrdinal;
        if (ordinal > TopologyAddress.MaxOrdinal)
        {
            return Result<EquipmentTopology>.Failure(TopologyErrors.InvalidEdit(
                $"{parent.Address} 下的{node.Level.DisplayName()}序号已用到 {TopologyAddress.MaxOrdinal}，不能再复制。"));
        }

        // 子树规模不超过现有拓扑，生成后由 Create 检查节点上限
        return Rewrite(parent.Id, children =>
        {
            var source = Array.Find(children, child => child.Id == node.Id)!;
            return [.. children, CloneWithNewIds(source) with { Ordinal = ordinal, Name = null, Enabled = enabled }];
        });

        static TopologyNodeDefinition CloneWithNewIds(TopologyNodeDefinition source)
            => new(TopologyNodeId.New(), source.Ordinal, source.Children.Select(CloneWithNewIds).ToArray())
            {
                Name = source.Name,
                Enabled = source.Enabled,
            };
    }

    /// <summary>
    /// 找到要编辑的非根节点及其上级。系统根没有上级，不能删除、重新编号或复制。
    /// </summary>
    private bool TryGetNonRoot(
        TopologyNodeId id,
        string operation,
        [NotNullWhen(true)] out TopologyNode? node,
        [NotNullWhen(true)] out TopologyNode? parent,
        [NotNullWhen(false)] out Error? error)
    {
        parent = null;
        error = null;
        if (!TryGetNode(id, out node))
        {
            error = TopologyErrors.NodeNotFound(id);
            return false;
        }

        if (!node.Address.TryGetParent(out var parentAddress) || !TryGetNode(parentAddress, out parent))
        {
            error = TopologyErrors.InvalidEdit($"系统根不能{operation}。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 导出整棵树，只把 <paramref name="parentId"/> 的下级列表换成 <paramref name="rewrite"/> 的结果，再整树校验
    /// </summary>
    private Result<EquipmentTopology> Rewrite(
        TopologyNodeId parentId,
        Func<TopologyNodeDefinition[], TopologyNodeDefinition[]> rewrite)
        => Create(Export(node => (node.Name, node.IsEnabled), parentId, rewrite));

    private TopologyDefinition Export(
        Func<TopologyNode, (string? Name, bool Enabled)> settings,
        TopologyNodeId rewriteParent = default,
        Func<TopologyNodeDefinition[], TopologyNodeDefinition[]>? rewriteChildren = null)
    {
        return new TopologyDefinition(Root.Id, settings(Root).Name ?? "", ExportChildren(Root));

        TopologyNodeDefinition[] ExportChildren(TopologyNode parent)
        {
            var children = Array.ConvertAll(parent.Children.ToArray(), node =>
            {
                var (name, enabled) = settings(node);
                return new TopologyNodeDefinition(node.Id, node.Ordinal, ExportChildren(node)) { Name = name, Enabled = enabled };
            });

            // 空 Id 不属于任何节点：不改写时 rewriteParent 为 default，永远匹配不上
            return rewriteChildren is not null && parent.Id == rewriteParent ? rewriteChildren(children) : children;
        }
    }

    // ---------- 按系统身份（机器用）查询 ----------

    public bool TryGetNode(TopologyNodeId id, [NotNullWhen(true)] out TopologyNode? node)
        => _nodesById.TryGetValue(id, out node);

    public Result<TopologyNode> GetNode(TopologyNodeId id)
        => TryGetNode(id, out var node)
            ? Result<TopologyNode>.Success(node)
            : Result<TopologyNode>.Failure(TopologyErrors.NodeNotFound(id));

    /// <inheritdoc cref="CheckEnabled(TopologyAddress)"/>
    public Result CheckEnabled(TopologyNodeId id)
        => TryGetNode(id, out var node)
            ? CheckEnabled(node.Address)
            : Result.Failure(TopologyErrors.NodeNotFound(id));

    // ---------- 按位置编码（人用）查询 ----------

    public bool TryGetNode(TopologyAddress address, [NotNullWhen(true)] out TopologyNode? node)
        => Locate(address, out node, out _);

    public Result<TopologyNode> GetNode(TopologyAddress address)
        => TryGetNode(address, out var node)
            ? Result<TopologyNode>.Success(node)
            : Result<TopologyNode>.Failure(TopologyErrors.NodeNotFound(address));

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

    // ---------- 按层 / 按子树 ----------

    /// <summary>
    /// 指定层级的全部节点，按地址升序
    /// </summary>
    public IReadOnlyList<TopologyNode> GetNodes(TopologyLevel level) => _readOnlyByLevel[LevelIndex(level)];

    /// <summary>
    /// 指定层级的节点数（含停用节点）
    /// </summary>
    public int CountAt(TopologyLevel level) => _nodesByLevel[LevelIndex(level)].Length;

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

        // Id 必须全树唯一（不只是同级唯一）：系统按 Id 找节点，重复即无法区分
        private readonly Dictionary<TopologyNodeId, TopologyAddress> _ids = [];
        private int _nodeCount = 1; // 系统根
        private bool _aborted;

        public IReadOnlyList<Error> Errors => _errors;

        public int TotalErrorCount { get; private set; }

        public TopologyNode BuildRoot(TopologyDefinition definition)
        {
            CheckId(TopologyAddress.System, definition.Id);

            string? name = null;
            if (string.IsNullOrWhiteSpace(definition.Name))
                AddError(TopologyErrors.NameField, "系统名称不能为空。");
            else
                name = CheckName(TopologyAddress.System, definition.Name);

            // 系统根不可停用：停用整台设备不属于拓扑配置，应由上层的急停 / 安全停机处理
            var cabinets = BuildChildren(TopologyAddress.System, definition.Cabinets, parentEffectivelyEnabled: true);
            return new TopologyNode(definition.Id, TopologyAddress.System, name, isEnabled: true, isEffectivelyEnabled: true, cabinets);
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

                // 序号非法或重复时地址不成立，整棵子树无从校验，跳过
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

                // Id 有问题时地址仍成立，继续校验下级，一次报全
                CheckId(address, definition.Id);

                var name = definition.Name is null ? null : CheckName(address, definition.Name);
                var effectivelyEnabled = parentEffectivelyEnabled && definition.Enabled;
                var children = BuildChildren(address, definition.Children, effectivelyEnabled);
                built.Add(new TopologyNode(definition.Id, address, name, definition.Enabled, effectivelyEnabled, children));
            }

            built.Sort(static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
            return built.ToArray();
        }

        private void CheckId(TopologyAddress address, TopologyNodeId id)
        {
            if (id.IsEmpty)
            {
                AddError(TopologyErrors.IdField, $"{address} 的节点 Id 不能为空。");
                return;
            }

            if (!_ids.TryAdd(id, address))
                AddError(TopologyErrors.DuplicateIdField, $"{address} 的节点 Id {id} 与 {_ids[id]} 重复。");
        }

        /// <summary>
        /// 去除首尾空白；空白名视为未配置（返回 null，界面回退显示位置编码）
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
