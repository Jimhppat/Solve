using System.Text.Json;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;

namespace Solve.Adapters.Persistence.Topology;

/// <summary>
/// 领域拓扑 ⇄ 数据库行的显式转换。
/// </summary>
/// <remarks>
/// 读取时把库内数据视为不可信输入（可能被手工改库或外部工具写坏）：逐行解析 → 按 parent_id 重建树 →
/// 领域校验 → 核对冗余存储的层级、位置编码与启用状态。错误一并收集，任一处有错即整体拒绝。
/// </remarks>
internal static class TopologyRowMapper
{
    private const string TreeRowId = "(tree)";

    /// <summary>
    /// 先序展开（上级总在下级之前）。时间戳留空，由仓储按新旧快照对比后填写。
    /// </summary>
    public static List<TopologyNodeRow> ToRows(EquipmentTopology topology)
    {
        var rows = new List<TopologyNodeRow>(topology.NodeCount);
        var stack = new Stack<(TopologyNode Node, string? ParentId)>();
        stack.Push((topology.Root, null));

        while (stack.Count > 0)
        {
            var (node, parentId) = stack.Pop();
            var id = node.Id.ToString();
            rows.Add(new TopologyNodeRow
            {
                Id = id,
                ParentId = parentId,
                Level = (long)node.Level,
                Ordinal = node.Ordinal,
                Address = node.Address.ToString(),
                Name = node.Name,
                Enabled = node.IsEnabled ? 1 : 0,
                Ports = SerializePorts(node.Ports),
            });

            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push((node.Children[i], id));
        }

        return rows;
    }

    /// <summary>
    /// 行 → 拓扑。失败时返回全部问题（最多 <see cref="EquipmentTopology.MaxReportedErrors"/> 条）。
    /// </summary>
    public static Result<EquipmentTopology> ToTopology(IReadOnlyList<TopologyNodeRow> rows, out IReadOnlyList<Error> problems)
    {
        var collector = new ProblemCollector();
        problems = collector.Errors;

        // 1. 逐行解析
        var parsed = new Dictionary<TopologyNodeId, ParsedRow>(rows.Count);
        var childrenOf = new Dictionary<TopologyNodeId, List<ParsedRow>>();
        var roots = new List<ParsedRow>(1);
        foreach (var row in rows)
        {
            if (!TryParse(row, collector, out var node))
                continue;

            // 主键是文本，大小写不同的同一 UUID 能绕过主键约束
            if (!parsed.TryAdd(node.Id, node))
            {
                collector.Add(row.Id, $"与行 {parsed[node.Id].Row.Id} 是同一个 Id");
                continue;
            }

            if (node.ParentId is { } parentId)
            {
                if (!childrenOf.TryGetValue(parentId, out var siblings))
                    childrenOf[parentId] = siblings = [];
                siblings.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        if (roots.Count != 1)
            collector.Add(TreeRowId, $"系统根节点（parent_id 为空）应恰好 1 个，实际 {roots.Count} 个");

        if (collector.Total > 0)
            return Fail(collector);

        // 2. 从系统根重建树。深度在此处截断：坏数据可能是一条极长的链，不能交给递归无限展开
        var root = roots[0];
        var reached = 0;
        var cabinets = BuildChildren(root, depth: 1);
        if (reached + 1 != parsed.Count)
            collector.Add(TreeRowId, $"{parsed.Count - reached - 1} 行无法从系统根到达（上级缺失、成环或超过 {TopologyLevels.Count} 层）");

        // 3. 领域校验（序号范围、同级重复、名称长度、Id 唯一等）
        var created = EquipmentTopology.Create(new TopologyDefinition(root.Id, root.Row.Name ?? "", cabinets) { Ports = root.Ports }, out var invalid);
        foreach (var error in invalid)
            collector.Add(error);

        if (collector.Total > 0)
            return Fail(collector);

        // 4. 冗余字段核对：库里的层级 / 位置编码 / 启用状态必须与按树推导的结果一致
        var topology = created.Value;
        foreach (var node in parsed.Values)
        {
            var actual = topology.GetNode(node.Id).Value;
            if (node.Row.Level != (long)actual.Level)
                collector.Add(node.Row.Id, $"level 存为 {node.Row.Level}，按树推导应为 {(int)actual.Level}");

            if (node.Ordinal != actual.Ordinal)
                collector.Add(node.Row.Id, $"ordinal 存为 {node.Ordinal}，系统根应为 0");

            if (!string.Equals(node.Row.Address, actual.Address.ToString(), StringComparison.Ordinal))
                collector.Add(node.Row.Id, $"address 存为 '{node.Row.Address}'，按树推导应为 '{actual.Address}'");

            if (node.Enabled != actual.IsEnabled)
                collector.Add(node.Row.Id, $"enabled 存为 {node.Row.Enabled}，系统根不可停用");
        }

        return collector.Total == 0 ? Result<EquipmentTopology>.Success(topology) : Fail(collector);

        TopologyNodeDefinition[] BuildChildren(ParsedRow parent, int depth)
        {
            if (!childrenOf.TryGetValue(parent.Id, out var children))
                return [];

            if (depth >= TopologyLevels.Count)
            {
                collector.Add(parent.Row.Id, $"已是第 {TopologyLevels.Count} 层（产品位），却还有 {children.Count} 个下级");
                return [];
            }

            var definitions = new TopologyNodeDefinition[children.Count];
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                reached++;
                definitions[i] = new TopologyNodeDefinition(child.Id, child.Ordinal, BuildChildren(child, depth + 1))
                {
                    Name = child.Row.Name,
                    Enabled = child.Enabled,
                    Ports = child.Ports,
                };
            }

            return definitions;
        }
    }

    private static bool TryParse(TopologyNodeRow row, ProblemCollector collector, out ParsedRow parsed)
    {
        parsed = null!;
        var valid = true;

        if (!TopologyNodeId.TryParse(row.Id, out var id))
        {
            collector.Add(row.Id, "id 不是有效的 UUID");
            valid = false;
        }

        TopologyNodeId? parentId = null;
        if (row.ParentId is not null)
        {
            if (TopologyNodeId.TryParse(row.ParentId, out var parsedParent))
            {
                parentId = parsedParent;
            }
            else
            {
                collector.Add(row.Id, $"parent_id '{row.ParentId}' 不是有效的 UUID");
                valid = false;
            }
        }

        if (row.Ordinal is < int.MinValue or > int.MaxValue)
        {
            collector.Add(row.Id, $"ordinal {row.Ordinal} 超出整数范围");
            valid = false;
        }

        if (row.Enabled is not (0 or 1))
        {
            collector.Add(row.Id, $"enabled 只能为 0 或 1，实际 {row.Enabled}");
            valid = false;
        }

        if (!TryParsePorts(row, collector, out var ports))
            valid = false;

        if (valid)
            parsed = new ParsedRow(row, id, parentId, (int)row.Ordinal, row.Enabled == 1, ports);

        return valid;
    }

    // ==================== 接点组 ====================

    private static readonly JsonSerializerOptions PortJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class PortGroupDto
    {
        public int? Kind { get; set; }

        public string? Name { get; set; }

        public int? Count { get; set; }
    }

    private static string SerializePorts(IReadOnlyList<PortGroup> ports)
        => ports.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(ports.Select(p => new PortGroupDto { Kind = (int)p.Kind, Name = p.Name, Count = p.Count }).ToList(), PortJson);

    /// <summary>
    /// 只接受"对象数组，每项都有 kind、name、count"。取值范围（功能已定义、数量、名称长度、重复）由领域层统一校验
    /// </summary>
    private static bool TryParsePorts(TopologyNodeRow row, ProblemCollector collector, out IReadOnlyList<PortGroup> ports)
    {
        ports = [];
        List<PortGroupDto?>? groups;
        try
        {
            groups = JsonSerializer.Deserialize<List<PortGroupDto?>>(row.Ports, PortJson);
        }
        catch (JsonException ex)
        {
            collector.Add(row.Id, $"ports '{row.Ports}' 不是有效的接点配置：{ex.Message}");
            return false;
        }

        if (groups is null || groups.Any(g => g is not { Kind: not null, Name: not null, Count: not null }))
        {
            collector.Add(row.Id, $"ports '{row.Ports}' 不是有效的接点配置：每项都必须有 kind、name、count");
            return false;
        }

        ports = groups.Select(g => new PortGroup((ChannelKind)g!.Kind!.Value, g.Name!, g.Count!.Value)).ToArray();
        return true;
    }

    private static Result<EquipmentTopology> Fail(ProblemCollector collector)
        => Result<EquipmentTopology>.Failure(TopologyErrors.StorageCorrupt(collector.Errors, collector.Total));

    private sealed record ParsedRow(TopologyNodeRow Row, TopologyNodeId Id, TopologyNodeId? ParentId, int Ordinal, bool Enabled,
        IReadOnlyList<PortGroup> Ports);

    private sealed class ProblemCollector
    {
        private readonly List<Error> _errors = [];

        public IReadOnlyList<Error> Errors => _errors;

        public int Total { get; private set; }

        public void Add(string rowId, string detail) => Add(TopologyErrors.StorageCorruptRow(rowId, detail));

        public void Add(Error error)
        {
            Total++;
            if (_errors.Count < EquipmentTopology.MaxReportedErrors)
                _errors.Add(error);
        }
    }
}
