using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Topology.TestSupport;

namespace Solve.Core.Tests.Wiring;

/// <summary>
/// 批量接线：按位置编码顺序依次对应连续通道，不够时续接下一个模块
/// </summary>
public sealed class WiringBatchTests
{
    // 1 柜 × 1 层 × 2 槽 × 2 治具 × 3 产品 = 12 个产品位
    private readonly EquipmentTopology _topology = FakeTopologyRepository.Layout(1, 1, 2, 2, 3);
    private static readonly ChannelGroup Input8 = new("input", "输入", ChannelKind.Measure, 8);

    private TopologyNode Node(string address) => _topology.GetNode(TopologyAddress.Parse(address)).Value;

    [Fact]
    public void Expand_PairsPositionsInAddressOrder_AndContinuesOnNextModule()
    {
        var first = DeviceId.New();
        var second = DeviceId.New();
        var request = new WiringBatchRequest(Node("C01").Id, TopologyLevel.Product,
            [new ChannelSource(first, Input8), new ChannelSource(second, Input8)], StartIndex: 3) { Purpose = " VBAT " };

        var wires = WiringBatch.Expand(_topology, request).Value;

        Assert.Equal(12, wires.Count);
        Assert.Equal(_topology.GetDescendants(Node("C01").Address, TopologyLevel.Product).Select(n => n.Id), wires.Select(w => w.Node));
        Assert.Equal(new ChannelRef(first, "input", 3), wires[0].Channel);
        Assert.Equal(new ChannelRef(first, "input", 8), wires[5].Channel);
        Assert.Equal(new ChannelRef(second, "input", 1), wires[6].Channel);
        Assert.Equal(new ChannelRef(second, "input", 6), wires[11].Channel);
        Assert.All(wires, w => Assert.Equal(("VBAT", ChannelKind.Measure), (w.Purpose, w.Kind)));

        // 展开结果本身满足规则，可以直接加入空接线
        Assert.True(WiringMap.Create(_topology, []).WithAdded(wires).IsSuccess);
    }

    [Fact]
    public void Expand_NotEnoughChannels_ReportsNeededAndAvailable()
    {
        var request = new WiringBatchRequest(Node("C01").Id, TopologyLevel.Product, [new ChannelSource(DeviceId.New(), Input8)], StartIndex: 1);

        var result = WiringBatch.Expand(_topology, request);

        Assert.Equal(WiringErrors.NotEnoughChannelsCode, result.Error.Code);
        Assert.Contains("12", result.Error.Message);
        Assert.Contains("8", result.Error.Message);
    }

    [Fact]
    public void Expand_SameLevelAsRoot_WiresOnlyTheRoot()
    {
        var request = new WiringBatchRequest(Node("C01-L01-S02").Id, TopologyLevel.Slot, [new ChannelSource(DeviceId.New(), Input8)], StartIndex: 2);

        var wire = Assert.Single(WiringBatch.Expand(_topology, request).Value);

        Assert.Equal(Node("C01-L01-S02").Id, wire.Node);
        Assert.Equal(2, wire.Channel.Index);
    }

    [Theory]
    [InlineData(TopologyLevel.System)]
    [InlineData(TopologyLevel.Cabinet)]
    public void Expand_LevelAboveRootOrSystem_IsRejected(TopologyLevel level)
    {
        var request = new WiringBatchRequest(Node("C01-L01").Id, level, [new ChannelSource(DeviceId.New(), Input8)], StartIndex: 1);

        Assert.True(WiringBatch.Expand(_topology, request).IsFailure);
    }

    [Fact]
    public void Expand_MixedKinds_IsRejected()
    {
        var load = new ChannelGroup("load", "负载", ChannelKind.Load, 8);
        var request = new WiringBatchRequest(Node("C01").Id, TopologyLevel.Product,
            [new ChannelSource(DeviceId.New(), Input8), new ChannelSource(DeviceId.New(), load)], StartIndex: 1);

        Assert.Equal(WiringErrors.KindMismatchCode, WiringBatch.Expand(_topology, request).Error.Code);
    }

    [Fact]
    public void Expand_StartIndexOutOfGroup_IsRejected()
    {
        var request = new WiringBatchRequest(Node("C01").Id, TopologyLevel.Product, [new ChannelSource(DeviceId.New(), Input8)], StartIndex: 9);

        Assert.True(WiringBatch.Expand(_topology, request).IsFailure);
    }

    /// <summary>
    /// 规则索引按层数查找：上万路批量接线不会退化成两两比较
    /// </summary>
    [Fact]
    public void LargeBatch_IsCheckedQuickly()
    {
        var topology = FakeTopologyRepository.Layout(4, 10, 10, 5, 10); // 每柜 5 000 个产品位，共 20 000 个
        var group = new ChannelGroup("input", "输入", ChannelKind.Measure, 256);
        var sources = Enumerable.Range(0, 80).Select(_ => new ChannelSource(DeviceId.New(), group)).ToList();
        var request = new WiringBatchRequest(topology.Root.Children[0].Id, TopologyLevel.Product, sources, 1);

        var wires = WiringBatch.Expand(topology, request).Value;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var map = WiringMap.Create(topology, []).WithAdded(wires).Value;
        var again = map.WithAdded(WiringBatch.Expand(topology, request with
        {
            Root = topology.Root.Children[1].Id,
            Sources = Enumerable.Range(0, 80).Select(_ => new ChannelSource(DeviceId.New(), group)).ToList(),
        }).Value);
        started.Stop();

        Assert.True(again.IsSuccess);
        Assert.Equal(5_000, map.Count);
        Assert.Equal(10_000, again.Value.Count);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {started.Elapsed}");
    }
}
