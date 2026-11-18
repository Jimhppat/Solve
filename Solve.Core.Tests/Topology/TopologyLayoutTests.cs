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
}
