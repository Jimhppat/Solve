using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

public sealed class TopologyLayoutTests
{
    private static readonly TopologyLayout Standard = new(
        Cabinets: 2, LayersPerCabinet: 3, SlotsPerLayer: 4, FixturesPerSlot: 2, ProductsPerFixture: 8);

    private static IReadOnlyList<string> Codes(TopologyLayout layout)
        => layout.Validate().Select(e => e.Code).ToList();

    [Fact]
    public void Counts_AreComputedPerLevel()
    {
        // 1 + 2 + 6 + 24 + 48 + 384
        Assert.Equal(465, Standard.TotalNodeCount);
        Assert.Equal(384, Standard.ProductCount);
        Assert.Empty(Standard.Validate());
    }

    [Fact]
    public void ToDefinition_BuildsFullTree()
    {
        var definition = Standard.ToDefinition("老化房 1#");
        Assert.True(definition.IsSuccess);

        var topology = EquipmentTopology.Create(definition.Value).Value;

        Assert.Equal("老化房 1#", topology.Name);
        Assert.Equal(465, topology.NodeCount);
        Assert.Equal(2, topology.CountAt(TopologyLevel.Cabinet));
        Assert.Equal(6, topology.CountAt(TopologyLevel.Layer));
        Assert.Equal(24, topology.CountAt(TopologyLevel.Slot));
        Assert.Equal(48, topology.CountAt(TopologyLevel.Fixture));
        Assert.Equal(384, topology.CountAt(TopologyLevel.Product));

        // 序号从 1 连续编号
        Assert.True(topology.TryGetNode(TopologyAddress.Of(2, 3, 4, 2, 8), out _));
        Assert.False(topology.TryGetNode(TopologyAddress.Of(2, 3, 4, 2, 9), out _));
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1, nameof(TopologyLayout.Cabinets))]
    [InlineData(1, -1, 1, 1, 1, nameof(TopologyLayout.LayersPerCabinet))]
    [InlineData(1, 1, 1000, 1, 1, nameof(TopologyLayout.SlotsPerLayer))]
    [InlineData(1, 1, 1, 0, 1, nameof(TopologyLayout.FixturesPerSlot))]
    [InlineData(1, 1, 1, 1, 0, nameof(TopologyLayout.ProductsPerFixture))]
    public void OutOfRangeCount_IsRejected(int c, int l, int s, int f, int p, string field)
        => Assert.Equal([TopologyErrors.LayoutCodePrefix + field], Codes(new TopologyLayout(c, l, s, f, p)));

    [Fact]
    public void HugeLayout_DoesNotOverflow_AndIsRejectedByNodeLimit()
    {
        var max = TopologyAddress.MaxOrdinal;
        var layout = new TopologyLayout(max, max, max, max, max);

        Assert.True(layout.TotalNodeCount > EquipmentTopology.MaxNodeCount);
        Assert.Equal([TopologyErrors.LayoutCodePrefix + "NodeCount"], Codes(layout));

        var definition = layout.ToDefinition("X");
        Assert.True(definition.IsFailure);
        Assert.Equal(TopologyErrors.InvalidLayoutCode, definition.Error.Code);
    }

    [Fact]
    public void MultipleErrors_AreAllReported()
        => Assert.Equal(5, Codes(new TopologyLayout(0, 0, 0, 0, 0)).Count);

    // ---------- 反推布局 ----------

    [Fact]
    public void TryInfer_UniformTopology_ReturnsItsLayout()
    {
        var topology = EquipmentTopology.Create(Standard.ToDefinition("S").Value).Value;

        Assert.True(TopologyLayout.TryInfer(topology, out var layout));
        Assert.Equal(Standard, layout);
    }

    [Fact]
    public void TryInfer_DifferentCabinets_ReturnsMaxCounts()
    {
        // 1 号柜 2 层、2 号柜 1 层
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            new TopologyNodeDefinition(TopologyNodeId.New(), 1, [Chain(1, 4), Chain(2, 4)]),
            Chain(2, 5),
        ])).Value;

        Assert.False(TopologyLayout.TryInfer(topology, out var layout));
        Assert.Equal(new TopologyLayout(2, 2, 1, 1, 1), layout);
    }

    [Fact]
    public void TryInfer_GapInNumbering_IsNotUniform()
    {
        // 只有 1 号与 3 号柜：数量一致但编号不连续，按统一布局保存会变成 1、2 号
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Chain(1, 5), Chain(3, 5)])).Value;

        Assert.False(TopologyLayout.TryInfer(topology, out var layout));
        Assert.Equal(new TopologyLayout(2, 1, 1, 1, 1), layout);
    }

    [Fact]
    public void TryInfer_MissingLevels_IsNotUniform()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S", [Chain(1, 2)])).Value;

        Assert.False(TopologyLayout.TryInfer(topology, out var layout));
        Assert.Equal(new TopologyLayout(1, 1, 0, 0, 0), layout);
    }

    // ---------- 节点身份 ----------

    private static IEnumerable<TopologyNode> AllNodes(EquipmentTopology topology)
        => TopologyLevels.All.SelectMany(topology.GetNodes);

    /// <summary>
    /// 每层只有 1 个下级的一条链，直到产品位
    /// </summary>
    private static TopologyNodeDefinition Chain(int ordinal, int depth)
        => new(TopologyNodeId.New(), ordinal, depth == 1 ? [] : [Chain(1, depth - 1)]);

    [Fact]
    public void FirstTimeDefinition_GivesEveryNodeAUniqueId()
    {
        var topology = EquipmentTopology.Create(Standard.ToDefinition("S").Value).Value;

        var ids = AllNodes(topology).Select(n => n.Id).ToList();
        Assert.Equal(465, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(TopologyNodeId.Empty, ids);
    }

    [Fact]
    public void FirstTimeDefinition_IsNotReproducible_IdsMustBePersisted()
    {
        // 首次建档每次都生成新 Id：Id 只能来自持久化，不能靠"按布局再生成一遍"复原
        var first = EquipmentTopology.Create(Standard.ToDefinition("S").Value).Value;
        var second = EquipmentTopology.Create(Standard.ToDefinition("S").Value).Value;

        Assert.NotEqual(first.Root.Id, second.Root.Id);
        Assert.False(second.TryGetNode(first.GetNode(TopologyAddress.Of(1, 1, 1, 1, 1)).Value.Id, out _));
    }

    [Fact]
    public void ReapplyingLayout_KeepsIdsNamesAndMasks_OfKeptPositions()
    {
        // 现有：2 柜 × 1 层 × 1 槽 × 1 治具位 × 1 产品位；1 号柜有名字，2 号柜的层位被维护屏蔽
        var current = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "S",
        [
            Chain(1, 5) with { Name = "A 柜" },
            new TopologyNodeDefinition(TopologyNodeId.New(), 2, [Chain(1, 4) with { Enabled = false }]),
        ])).Value;

        // 扩容为 3 柜、每治具 2 产品位
        var grown = EquipmentTopology.Create(
            new TopologyLayout(3, 1, 1, 1, 2).ToDefinition("S", current).Value).Value;

        Assert.Equal(current.Root.Id, grown.Root.Id);
        foreach (var kept in AllNodes(current))
        {
            var node = grown.GetNode(kept.Id).Value;
            Assert.Equal(kept.Address, node.Address);
            Assert.Equal(kept.Name, node.Name);
            Assert.Equal(kept.IsEnabled, node.IsEnabled);
        }

        Assert.Equal("A 柜", grown.GetNode(TopologyAddress.Of(1)).Value.Name);
        Assert.False(grown.GetNode(TopologyAddress.Of(2, 1, 1, 1, 2)).Value.IsEffectivelyEnabled);

        // 新增位置拿到新 Id
        var added = grown.GetNode(TopologyAddress.Of(3)).Value;
        Assert.False(current.TryGetNode(added.Id, out _));
    }

    [Fact]
    public void ShrinkingLayout_DropsRemovedPositions()
    {
        var current = EquipmentTopology.Create(new TopologyLayout(2, 1, 1, 1, 2).ToDefinition("S").Value).Value;
        var removedId = current.GetNode(TopologyAddress.Of(2)).Value.Id;
        var keptId = current.GetNode(TopologyAddress.Of(1, 1, 1, 1, 1)).Value.Id;

        var shrunk = EquipmentTopology.Create(new TopologyLayout(1, 1, 1, 1, 1).ToDefinition("S", current).Value).Value;

        Assert.False(shrunk.TryGetNode(removedId, out _));
        Assert.Equal(TopologyAddress.Of(1, 1, 1, 1, 1), shrunk.GetNode(keptId).Value.Address);
        Assert.Equal(6, shrunk.NodeCount);
    }
}
