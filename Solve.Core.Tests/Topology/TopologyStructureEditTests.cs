using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

/// <summary>
/// 结构编辑：增减下级数量、删除、重新编号、复制。每次得到新拓扑，未涉及的节点 Id、序号、名称与启用状态原样保留。
/// </summary>
public sealed class TopologyStructureEditTests
{
    private static TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children)
        => new(TopologyNodeId.New(), ordinal, children);

    private static EquipmentTopology Layout(int c, int l, int s, int f, int p)
        => EquipmentTopology.Create(new TopologyLayout(c, l, s, f, p).ToDefinition("S").Value).Value;

    private static TopologyNode At(EquipmentTopology topology, params int[] ordinals)
        => topology.GetNode(TopologyAddress.Of(ordinals)).Value;

    private static IEnumerable<TopologyNode> AllNodes(EquipmentTopology topology)
        => TopologyLevels.All.SelectMany(topology.GetNodes);

    /// <summary>
    /// 原拓扑的每个节点在新拓扑中都还在：同一 Id、同一位置编码、同一名称与启用状态
    /// </summary>
    private static void AssertKeptUnchanged(EquipmentTopology before, EquipmentTopology after)
    {
        foreach (var node in AllNodes(before))
        {
            var kept = after.GetNode(node.Id).Value;
            Assert.Equal((node.Address, node.Name, node.IsEnabled), (kept.Address, kept.Name, kept.IsEnabled));
        }
    }

    // ---------- 下级数量 ----------

    [Fact]
    public void ChildCount_Grow_CopiesStructureOfLastChild_UnnamedAndEnabled()
    {
        // 1 号柜：L01 有 2 个槽，L02 有 3 个槽且已命名、停用（其中一个槽也停用）
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            Node(1,
                Node(1, Node(1), Node(2)),
                Node(2, Node(1), Node(2) with { Enabled = false }, Node(3)) with { Name = "高温层", Enabled = false }),
        ])).Value;
        var cabinet = At(topology, 1);

        var grown = topology.WithChildCount(cabinet.Id, 4).Value;

        AssertKeptUnchanged(topology, grown);
        Assert.Equal([1, 2, 3, 4], grown.GetNode(cabinet.Id).Value.Children.Select(c => c.Ordinal));
        foreach (var layer in new[] { At(grown, 1, 3), At(grown, 1, 4) })
        {
            // 照抄最后一个层位的结构；名称与停用属于具体的物理位置，不照抄
            Assert.Equal(3, layer.Children.Count);
            Assert.Null(layer.Name);
            Assert.True(layer.IsEffectivelyEnabled);
            Assert.All(layer.Children, slot => Assert.True(slot.IsEnabled));
            Assert.DoesNotContain(layer.Id, AllNodes(topology).Select(n => n.Id));
        }
    }

    [Fact]
    public void ChildCount_Grow_DoesNotFillGaps()
    {
        // 现场只有 1 号和 3 号柜（2 号拆走了）：新增的柜体排在 4 号，不顶替 2 号
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Node(1), Node(3)])).Value;

        var grown = topology.WithChildCount(topology.Root.Id, 3).Value;

        Assert.Equal([1, 3, 4], grown.Root.Children.Select(c => c.Ordinal));
    }

    [Fact]
    public void ChildCount_Grow_FromEmpty_AddsBareNodes()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1, Node(1, Node(1))))])).Value;
        var fixture = At(topology, 1, 1, 1, 1);

        var grown = topology.WithChildCount(fixture.Id, 2).Value;

        Assert.Equal(2, grown.CountAt(TopologyLevel.Product));
        Assert.Equal(TopologyLevel.Product, At(grown, 1, 1, 1, 1, 2).Level);
    }

    [Fact]
    public void ChildCount_Shrink_RemovesHighestOrdinals_AndKeepsOriginalUnchanged()
    {
        var topology = Layout(1, 4, 1, 1, 2);
        var cabinet = At(topology, 1);

        var shrunk = topology.WithChildCount(cabinet.Id, 2).Value;

        Assert.Equal([1, 2], shrunk.GetNode(cabinet.Id).Value.Children.Select(c => c.Ordinal));
        Assert.Equal(topology.NodeCount - 10, shrunk.NodeCount);
        Assert.Equal(At(topology, 1, 2).Id, At(shrunk, 1, 2).Id);
        Assert.Equal(4, topology.GetNode(cabinet.Id).Value.Children.Count);
    }

    [Fact]
    public void ChildCount_ToZero_LeavesEmptyContainer()
    {
        var topology = Layout(1, 1, 1, 1, 3);
        var fixture = At(topology, 1, 1, 1, 1);

        var emptied = topology.WithChildCount(fixture.Id, 0).Value;

        Assert.Empty(emptied.GetNode(fixture.Id).Value.Children);
        Assert.Equal(0, emptied.CountAt(TopologyLevel.Product));
    }

    [Fact]
    public void ChildCount_Unchanged_ReturnsSameInstance()
    {
        var topology = Layout(1, 2, 1, 1, 1);

        Assert.Same(topology, topology.WithChildCount(At(topology, 1).Id, 2).Value);
    }

    [Fact]
    public void ChildCount_InvalidRequests_AreRejected()
    {
        var topology = Layout(1, 1, 1, 1, 1);
        var cabinet = At(topology, 1);

        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithChildCount(At(topology, 1, 1, 1, 1, 1).Id, 1).Error.Code);
        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithChildCount(cabinet.Id, -1).Error.Code);
        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithChildCount(cabinet.Id, TopologyAddress.MaxOrdinal + 1).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.WithChildCount(TopologyNodeId.New(), 1).Error.Code);
    }

    [Fact]
    public void ChildCount_OrdinalsExhausted_IsRejected()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Node(997)])).Value;

        var result = topology.WithChildCount(topology.Root.Id, 4);

        Assert.Equal(TopologyErrors.InvalidEditCode, result.Error.Code);
        Assert.Contains("柜体数量最多为 3", result.Error.Message);
        Assert.True(topology.WithChildCount(topology.Root.Id, 3).IsSuccess);
    }

    [Fact]
    public void ChildCount_OverNodeLimit_IsRejectedBeforeBuilding()
    {
        // 每个柜体约 3000 个节点，扩到 999 个柜体约 300 万个：必须在生成之前按数量拦住
        var topology = Layout(1, 1, 999, 1, 1);

        var result = topology.WithChildCount(topology.Root.Id, 999);

        Assert.Equal(TopologyErrors.InvalidEditCode, result.Error.Code);
        Assert.Contains("超过上限", result.Error.Message);
    }

    // ---------- 删除 ----------

    [Fact]
    public void Remove_DropsSubtree_SiblingsKeepTheirNumbers()
    {
        var topology = Layout(1, 3, 1, 1, 2);
        var layer = At(topology, 1, 2);

        var removed = topology.WithNodeRemoved(layer.Id).Value;

        Assert.Equal(topology.NodeCount - 5, removed.NodeCount);
        Assert.False(removed.TryGetNode(layer.Id, out _));
        Assert.False(removed.TryGetNode(TopologyAddress.Of(1, 2, 1, 1, 1), out _));
        Assert.Equal([1, 3], At(removed, 1).Children.Select(c => c.Ordinal));
        Assert.Equal(At(topology, 1, 3).Id, At(removed, 1, 3).Id);
    }

    [Fact]
    public void Remove_Root_OrUnknown_IsRejected()
    {
        var topology = Layout(1, 1, 1, 1, 1);

        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithNodeRemoved(topology.Root.Id).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.WithNodeRemoved(TopologyNodeId.New()).Error.Code);
    }

    [Fact]
    public void Remove_LastChild_LeavesEmptyContainer()
    {
        var topology = Layout(1, 1, 1, 1, 1);

        var removed = topology.WithNodeRemoved(At(topology, 1, 1, 1, 1, 1).Id).Value;

        Assert.Empty(At(removed, 1, 1, 1, 1).Children);
        Assert.Equal(0, removed.CountAt(TopologyLevel.Product));
    }

    // ---------- 重新编号 ----------

    [Fact]
    public void Renumber_MovesWholeSubtreeAddress_KeepsIds_AndOrder()
    {
        var topology = Layout(3, 1, 1, 1, 1);
        var first = At(topology, 1);
        var product = At(topology, 1, 1, 1, 1, 1);

        var renumbered = topology.WithOrdinal(first.Id, 5).Value;

        Assert.Equal(TopologyAddress.Of(5), renumbered.GetNode(first.Id).Value.Address);
        Assert.Equal(TopologyAddress.Of(5, 1, 1, 1, 1), renumbered.GetNode(product.Id).Value.Address);
        Assert.False(renumbered.TryGetNode(TopologyAddress.Of(1), out _));

        // 下级按序号重新排序：位置编码查询与按层遍历都随之正确
        Assert.Equal([2, 3, 5], renumbered.Root.Children.Select(c => c.Ordinal));
        Assert.Equal(first.Id, At(renumbered, 5).Id);
        Assert.Equal(TopologyAddress.Of(5, 1, 1, 1, 1), renumbered.GetNodes(TopologyLevel.Product)[^1].Address);
    }

    [Fact]
    public void Renumber_ToOccupiedOrOutOfRange_IsRejected()
    {
        var topology = Layout(2, 1, 1, 1, 1);
        var first = At(topology, 1);

        var occupied = topology.WithOrdinal(first.Id, 2);
        Assert.Equal(TopologyErrors.InvalidEditCode, occupied.Error.Code);
        Assert.Contains("C02", occupied.Error.Message);

        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithOrdinal(first.Id, 0).Error.Code);
        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithOrdinal(first.Id, TopologyAddress.MaxOrdinal + 1).Error.Code);
        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithOrdinal(topology.Root.Id, 1).Error.Code);
    }

    [Fact]
    public void Renumber_SameOrdinal_ReturnsSameInstance()
    {
        var topology = Layout(2, 1, 1, 1, 1);

        Assert.Same(topology, topology.WithOrdinal(At(topology, 2).Id, 2).Value);
    }

    // ---------- 复制 ----------

    [Fact]
    public void Copy_DuplicatesSubtreeWithNewIds_AtNextOrdinal()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            Node(1, Node(1, Node(1, Node(1, Node(1), Node(2)))) with { Name = "高温层" }, Node(2) with { Enabled = false }) with { Name = "A 柜" },
            Node(4),
        ])).Value;
        var source = At(topology, 1);

        var copied = topology.WithNodeCopied(source.Id).Value;

        var copy = At(copied, 5);
        AssertKeptUnchanged(topology, copied);
        Assert.Equal(topology.NodeCount + 7, copied.NodeCount);

        // 副本自身不带名称（另一个物理位置）；启用状态与下级的名称、启用状态照抄
        Assert.Null(copy.Name);
        Assert.True(copy.IsEnabled);
        Assert.Equal("高温层", At(copied, 5, 1).Name);
        Assert.False(At(copied, 5, 2).IsEnabled);
        Assert.Equal(2, At(copied, 5, 1, 1, 1).Children.Count);

        var originalIds = AllNodes(topology).Select(n => n.Id).ToHashSet();
        Assert.DoesNotContain(copied.GetDescendants(copy.Address, TopologyLevel.Product), p => originalIds.Contains(p.Id));
        Assert.DoesNotContain(copy.Id, originalIds);
    }

    [Fact]
    public void Copy_Root_OrExhaustedOrdinals_IsRejected()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1), Node(TopologyAddress.MaxOrdinal)])).Value;

        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithNodeCopied(topology.Root.Id).Error.Code);
        Assert.Equal(TopologyErrors.InvalidEditCode, topology.WithNodeCopied(At(topology, 1).Id).Error.Code);
    }

    [Fact]
    public void Copy_OfDisabledNode_StaysDisabled()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
            [Node(1, Node(1)) with { Enabled = false }])).Value;

        var copied = topology.WithNodeCopied(At(topology, 1).Id).Value;

        Assert.False(At(copied, 2).IsEnabled);
        Assert.False(At(copied, 2, 1).IsEffectivelyEnabled);
    }

    // ---------- 下一个序号 ----------

    [Fact]
    public void NextChildOrdinal_IsMaxPlusOne()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Node(2, Node(7)), Node(3)])).Value;

        Assert.Equal(4, topology.Root.NextChildOrdinal);
        Assert.Equal(8, At(topology, 2).NextChildOrdinal);
        Assert.Equal(1, At(topology, 3).NextChildOrdinal);
    }
}
