using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.Core.Application.Twin;

/// <summary>
/// 一个产品位的当前状态快照
/// </summary>
/// <param name="UpdatedAt">最近一次收到上报的时间；从未收到为 null</param>
/// <param name="IsEnabled">产品位自身及全部上级都已启用（停用的不计入统计）</param>
public readonly record struct ProductSnapshot(
    TopologyNodeId Id,
    ProductState State,
    string? SerialNumber,
    DateTimeOffset? UpdatedAt,
    bool IsEnabled);

/// <summary>
/// 数字孪生状态库：保存每个产品位的最新状态，并逐级汇总到柜体、层位等容器，供主控台显示。
/// </summary>
/// <remarks>
/// <para>跟随拓扑：每次访问都核对 <see cref="ITopologyService.Current"/>，拓扑被替换（扩缩容、改名、停用）时自动重建索引，
/// 按节点 Id 保留仍然存在的产品位的状态；被删除的位置随之丢弃。</para>
/// <para>合并而不排队：同一产品位的多次上报只保留最新值，上报再频繁内存也不增长；汇总计数随上报增量维护，每次 O(层数)。</para>
/// <para>变化标记：状态改变的产品位及其全部祖先被标记，界面用 <see cref="DrainChanges"/> 按需取走，只刷新看得见的节点。</para>
/// <para>初始状态为空闲（<see cref="ProductState.Idle"/>）。暂不做超时判断（状态机尚未实现）：没有新上报时保持最后一次的状态，
/// <see cref="ProductSnapshot.UpdatedAt"/> 记录最近一次上报时间，供界面显示与将来的超时判断使用。</para>
/// <para>线程安全：数据源线程写、界面线程读，内部一把锁，每次持锁时间很短（不做磁盘 I/O、不回调外部代码；
/// 拓扑切换时的日志由异步日志管线写盘）。</para>
/// </remarks>
public sealed class TwinStateStore
{
    private readonly ITopologyService _topology;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Index? _index;

    public TwinStateStore(
        ITopologyService topology,
        ILogger<TwinStateStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(logger);

        _topology = topology;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 当前跟随的拓扑；尚未建档或读取失败时为 null
    /// </summary>
    public EquipmentTopology? Topology
    {
        get
        {
            lock (_gate)
            {
                return Sync()?.Topology;
            }
        }
    }

    /// <summary>
    /// 当前拓扑的全部产品位 Id（不可变快照，按地址顺序）。供数据源遍历，不会随后续变化而改变。
    /// </summary>
    public IReadOnlyList<TopologyNodeId> ProductIds
    {
        get
        {
            lock (_gate)
            {
                return Sync()?.ProductIds ?? [];
            }
        }
    }

    /// <summary>
    /// 上报一个产品位的状态（任意线程）。Id 不是当前拓扑中的产品位时返回 false 并忽略：
    /// 拓扑刚被修改、数据源还在用旧 Id 时会出现，不应让数据源失败。
    /// </summary>
    /// <param name="serialNumber">产品 SN；没有产品或未知时为 null</param>
    public bool Report(TopologyNodeId productId, ProductState state, string? serialNumber = null)
    {
        if (!state.IsDefined())
            throw new ArgumentOutOfRangeException(nameof(state), state, "未定义的产品状态，数据源映射有误。");

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (Sync() is not { } index || !index.Positions.TryGetValue(productId, out var position) || !index.IsProduct[position])
                return false;

            index.UpdatedAt[position] = now;
            index.SetProduct(position, state, serialNumber);
            return true;
        }
    }

    /// <summary>
    /// 取走自上次调用以来状态有变化的节点（产品位及其全部祖先），并清空标记
    /// </summary>
    public IReadOnlyList<TopologyNodeId> DrainChanges()
    {
        lock (_gate)
        {
            return Sync()?.DrainChanges() ?? [];
        }
    }

    public bool TryGetProduct(TopologyNodeId productId, out ProductSnapshot snapshot)
    {
        lock (_gate)
        {
            if (Sync() is not { } index || !index.Positions.TryGetValue(productId, out var position) || !index.IsProduct[position])
            {
                snapshot = default;
                return false;
            }

            snapshot = index.Snapshot(position);
            return true;
        }
    }

    /// <summary>
    /// 节点（含其下全部产品位）的状态统计；产品位返回只含它自己的统计。节点不存在时为 <see cref="TwinStatusSummary.None"/>。
    /// </summary>
    public TwinStatusSummary GetSummary(TopologyNodeId nodeId)
    {
        lock (_gate)
        {
            return Sync() is { } index && index.Positions.TryGetValue(nodeId, out var position)
                ? index.Summary(position)
                : TwinStatusSummary.None;
        }
    }

    // 调用方持锁。拓扑是不可变快照，按引用比较即可判断是否被替换
    private Index? Sync()
    {
        var current = _topology.Current;
        if (ReferenceEquals(current, _index?.Topology))
            return _index;

        var previous = _index;
        _index = current is null ? null : Index.Build(current, previous);
        _logger.LogInformation("孪生状态库切换到拓扑 {Topology}，产品位 {Products} 个（保留原有状态 {Kept} 个）",
            current?.ToString() ?? "（无）", _index?.ProductIds.Length ?? 0, _index?.KeptCount ?? 0);
        return _index;
    }

    /// <summary>
    /// 某一份拓扑的稠密索引：节点按"层级升序、层内地址升序"编号，父节点编号总小于子节点。
    /// </summary>
    private sealed class Index
    {
        private const int StateCount = ProductStates.Count;

        private readonly int[] _parent;
        private readonly bool[] _enabled;
        private readonly ProductState[] _state;
        private readonly string?[] _serial;
        private readonly int[] _counts;   // 节点 i 的状态 s 计数位于 [i * StateCount + s]
        private readonly int[] _disabled;
        private readonly bool[] _dirty;
        private readonly List<int> _dirtyList = [];
        private readonly TopologyNodeId[] _ids;

        private Index(EquipmentTopology topology, TopologyNode[] nodes)
        {
            Topology = topology;
            _ids = Array.ConvertAll(nodes, node => node.Id);
            Positions = _ids.Select((id, position) => (id, position)).ToFrozenDictionary(pair => pair.id, pair => pair.position);

            var count = nodes.Length;
            _parent = new int[count];
            _enabled = new bool[count];
            IsProduct = new bool[count];
            _state = new ProductState[count];
            _serial = new string?[count];
            UpdatedAt = new DateTimeOffset?[count];
            _counts = new int[count * StateCount];
            _disabled = new int[count];
            _dirty = new bool[count];

            for (var i = 0; i < count; i++)
            {
                var node = nodes[i];
                _enabled[i] = node.IsEffectivelyEnabled;
                IsProduct[i] = node.Level == TopologyLevel.Product;
                _parent[i] = node.Address.TryGetParent(out var parentAddress) && topology.TryGetNode(parentAddress, out var parent)
                    ? Positions[parent.Id]
                    : -1;
            }

            ProductIds = _ids.Where((_, i) => IsProduct[i]).ToArray();
        }

        public EquipmentTopology Topology { get; }

        public FrozenDictionary<TopologyNodeId, int> Positions { get; }

        public bool[] IsProduct { get; }

        public DateTimeOffset?[] UpdatedAt { get; }

        public TopologyNodeId[] ProductIds { get; }

        public int KeptCount { get; private set; }

        public static Index Build(EquipmentTopology topology, Index? previous)
        {
            var nodes = TopologyLevels.All.SelectMany(topology.GetNodes).ToArray();
            var index = new Index(topology, nodes);

            for (var i = 0; i < nodes.Length; i++)
            {
                if (!index.IsProduct[i])
                    continue;

                // 按 Id 保留原有状态：重新编号、扩容后同一产品位的显示不受影响
                if (previous is not null && previous.Positions.TryGetValue(index._ids[i], out var old) && previous.IsProduct[old])
                {
                    index._state[i] = previous._state[old];
                    index._serial[i] = previous._serial[old];
                    index.UpdatedAt[i] = previous.UpdatedAt[old];
                    index.KeptCount++;
                }

                index.AddToAncestors(i, index._state[i], +1);
            }

            return index;
        }

        public void SetProduct(int position, ProductState state, string? serial)
        {
            var old = _state[position];
            var changed = old != state || !string.Equals(_serial[position], serial, StringComparison.Ordinal);
            if (!changed)
                return;

            if (old != state)
            {
                AddToAncestors(position, old, -1);
                AddToAncestors(position, state, +1);
            }

            _state[position] = state;
            _serial[position] = serial;
            MarkDirtyWithAncestors(position);
        }

        public IReadOnlyList<TopologyNodeId> DrainChanges()
        {
            if (_dirtyList.Count == 0)
                return [];

            var changed = new TopologyNodeId[_dirtyList.Count];
            for (var i = 0; i < changed.Length; i++)
            {
                var position = _dirtyList[i];
                changed[i] = _ids[position];
                _dirty[position] = false;
            }

            _dirtyList.Clear();
            return changed;
        }

        public ProductSnapshot Snapshot(int position)
            => new(_ids[position], _state[position], _serial[position], UpdatedAt[position], _enabled[position]);

        public TwinStatusSummary Summary(int position)
        {
            Span<int> counts = stackalloc int[StateCount];
            if (IsProduct[position])
            {
                if (_enabled[position])
                    counts[(int)_state[position]] = 1;
                return new TwinStatusSummary(counts, _enabled[position] ? 0 : 1);
            }

            _counts.AsSpan(position * StateCount, StateCount).CopyTo(counts);
            return new TwinStatusSummary(counts, _disabled[position]);
        }

        // 产品位的贡献计入它的每一级祖先（不计入它自己）；停用的产品位只计入"停用"
        private void AddToAncestors(int product, ProductState state, int delta)
        {
            for (var ancestor = _parent[product]; ancestor >= 0; ancestor = _parent[ancestor])
            {
                if (_enabled[product])
                    _counts[ancestor * StateCount + (int)state] += delta;
                else
                    _disabled[ancestor] += delta;
            }
        }

        private void MarkDirtyWithAncestors(int position)
        {
            // 标记总是整条祖先链一起打：遇到已标记的节点，它的祖先必然也已标记
            for (var node = position; node >= 0 && !_dirty[node]; node = _parent[node])
            {
                _dirty[node] = true;
                _dirtyList.Add(node);
            }
        }
    }
}
