using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Topology.TestSupport;

namespace Solve.Core.Tests.Topology;

/// <summary>
/// 位置接点（需求侧）：声明、校验、向下生效到产品位、由有效接线按（功能，用途）满足，以及结构编辑时随位置保留
/// </summary>
public sealed class PortGroupTests
{
    // 1 柜 × 1 层 × 2 槽 × 1 治具 × 2 产品
    private readonly EquipmentTopology _topology = FakeTopologyRepository.Layout(1, 1, 2, 1, 2);
    private readonly DeviceId _psu = DeviceId.New();
    private readonly DeviceId _daq = DeviceId.New();

    private static readonly PortGroup Vbat = new(ChannelKind.PowerSupply, "VBAT", 1);
    private static readonly PortGroup Voltages = new(ChannelKind.Measure, "V", 3);

    private static TopologyNode Node(EquipmentTopology topology, string address) => topology.GetNode(TopologyAddress.Parse(address)).Value;

    private EquipmentTopology WithPorts(EquipmentTopology topology, string address, params PortGroup[] ports)
    {
        var updated = topology.WithNodePorts(Node(topology, address).Id, ports);
        Assert.True(updated.IsSuccess, updated.IsFailure ? updated.Error.Message : null);
        return updated.Value;
    }

    private Wire Power(EquipmentTopology topology, string address, string? purpose)
        => new(WireId.New(), Node(topology, address).Id, new ChannelRef(_psu, "output", 1), ChannelKind.PowerSupply) { Purpose = purpose };

    private Wire Measure(EquipmentTopology topology, string address, int index, string? purpose)
        => new(WireId.New(), Node(topology, address).Id, new ChannelRef(_daq, "input", index), ChannelKind.Measure) { Purpose = purpose };

    // ---------- 展开与校验 ----------

    [Fact]
    public void Expand_SinglePort_UsesName_MultiplePorts_AppendIndex()
    {
        Assert.Equal([new Port(ChannelKind.PowerSupply, "VBAT")], Vbat.Expand());
        Assert.Equal(["V1", "V2", "V3"], Voltages.Expand().Select(p => p.Purpose));
        Assert.Equal("采集 V × 3", Voltages.ToString());
    }

    [Fact]
    public void Ports_AreTrimmed_AndKeptOnNode()
    {
        var topology = WithPorts(_topology, "C01-L01", Vbat with { Name = "  VBAT " });

        Assert.Equal([Vbat], Node(topology, "C01-L01").Ports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PortGroup.MaxCount + 1)]
    public void CountOutOfRange_IsRejected(int count)
        => Assert.Equal(TopologyErrors.InvalidDefinitionCode,
            _topology.WithNodePorts(_topology.Root.Id, [new PortGroup(ChannelKind.Measure, "V", count)]).Error.Code);

    [Fact]
    public void MultiplePorts_WithoutName_AreRejected()
    {
        var result = _topology.WithNodePorts(_topology.Root.Id, [new PortGroup(ChannelKind.Measure, " ", 2)]);

        Assert.Contains("必须填写名称", result.Error.Message);
    }

    [Fact]
    public void SinglePort_WithoutName_IsAllowed_AndMatchesWireWithoutPurpose()
    {
        var topology = WithPorts(_topology, "C01-L01-S01-F01-P01", new PortGroup(ChannelKind.Measure, "", 1));
        var map = WiringMap.Create(topology, [Measure(topology, "C01-L01-S01-F01-P01", 1, null)]);

        Assert.True(Assert.Single(map.GetPortCoverage(Node(topology, "C01-L01-S01-F01-P01").Id)).IsConnected);
    }

    [Fact]
    public void ExpandedPortCollision_IsRejected()
    {
        // V × 11 展开出 V1，与单独声明的 V1 重复（不分大小写）
        var result = _topology.WithNodePorts(_topology.Root.Id,
            [new PortGroup(ChannelKind.Measure, "V", 11), new PortGroup(ChannelKind.Measure, "v1", 1)]);

        Assert.Contains("重复", result.Error.Message);
    }

    [Fact]
    public void SameName_DifferentKind_IsAllowed()
        => Assert.True(_topology.WithNodePorts(_topology.Root.Id,
            [new PortGroup(ChannelKind.Measure, "A", 1), new PortGroup(ChannelKind.Switch, "A", 1)]).IsSuccess);

    [Fact]
    public void UndefinedKind_AndTooManyGroups_AreRejected()
    {
        Assert.Contains("功能未定义", _topology.WithNodePorts(_topology.Root.Id, [new PortGroup((ChannelKind)99, "X", 1)]).Error.Message);

        var many = Enumerable.Range(1, PortGroup.MaxGroupsPerNode + 1).Select(i => new PortGroup(ChannelKind.Measure, $"G{i}-", 1)).ToArray();
        Assert.Contains("不能超过", _topology.WithNodePorts(_topology.Root.Id, many).Error.Message);
    }

    // ---------- 向下生效 ----------

    [Fact]
    public void RequiredPorts_AreInheritedByEveryProductBelow_WithSource()
    {
        var topology = WithPorts(_topology, "C01-L01-S01", Voltages);
        topology = WithPorts(topology, "C01-L01-S01-F01-P02", Vbat);

        var first = Node(topology, "C01-L01-S01-F01-P01");
        var second = Node(topology, "C01-L01-S01-F01-P02");
        var otherSlot = Node(topology, "C01-L01-S02-F01-P01");

        Assert.Equal(["V1", "V2", "V3"], first.RequiredPorts.Select(p => p.Port.Purpose));
        Assert.All(first.RequiredPorts, p => Assert.Equal(Node(topology, "C01-L01-S01").Id, p.Source));
        Assert.Equal(["V1", "V2", "V3", "VBAT"], second.RequiredPorts.Select(p => p.Port.Purpose));
        Assert.Empty(otherSlot.RequiredPorts);
    }

    [Fact]
    public void SamePort_DeclaredTwiceOnPath_IsRequiredOnce_FromTopmost()
    {
        var topology = WithPorts(_topology, "C01", Vbat);
        topology = WithPorts(topology, "C01-L01-S01-F01-P01", Vbat with { Name = "vbat" });

        var required = Assert.Single(Node(topology, "C01-L01-S01-F01-P01").RequiredPorts);
        Assert.Equal(Node(topology, "C01").Id, required.Source);
    }

    [Fact]
    public void RootPorts_ApplyToWholeMachine()
    {
        var topology = WithPorts(_topology, "SYS", Vbat);

        Assert.All(topology.GetNodes(TopologyLevel.Product), p => Assert.Single(p.RequiredPorts));
    }

    // ---------- 接点核对 ----------

    [Fact]
    public void Coverage_MatchesKindAndPurpose_IncludingInheritedWires()
    {
        var topology = WithPorts(_topology, "C01-L01-S01", Vbat, Voltages);
        var product = Node(topology, "C01-L01-S01-F01-P01").Id;

        // 层位上的电源（用途 vbat，不分大小写）满足下面全部产品位的 VBAT；V2 接在产品位自身；V3 用途写错
        var map = WiringMap.Create(topology,
        [
            Power(topology, "C01-L01", "vbat"),
            Measure(topology, "C01-L01-S01-F01-P01", 2, "V2"),
            Measure(topology, "C01-L01-S01-F01-P01", 3, "V30"),
        ]);

        var coverage = map.GetPortCoverage(product);

        Assert.Equal(
            [("供电 VBAT", true), ("采集 V1", false), ("采集 V2", true), ("采集 V3", false)],
            coverage.Select(c => (c.Required.Port.DisplayText, c.IsConnected)));
        Assert.Equal(Node(topology, "C01-L01").Id, coverage[0].Wire!.Value.Source.Id);
    }

    [Fact]
    public void Coverage_IsOnlyCheckedOnProducts()
    {
        var topology = WithPorts(_topology, "C01-L01-S01", Voltages);
        var map = WiringMap.Create(topology, []);

        Assert.Empty(map.GetPortCoverage(Node(topology, "C01-L01-S01").Id));
        Assert.Equal(3, map.GetPortCoverage(Node(topology, "C01-L01-S01-F01-P01").Id).Count);
    }

    // ---------- 结构编辑保留接点 ----------

    [Fact]
    public void StructureEdits_KeepPorts()
    {
        var topology = WithPorts(_topology, "C01-L01-S01-F01-P02", Vbat);
        topology = WithPorts(topology, "C01-L01", Voltages);

        // 下级数量调大：新产品位照抄最后一个（P02）的接点；复制节点：副本带接点；改名：接点不变；导出再建：等价
        var grown = topology.WithChildCount(Node(topology, "C01-L01-S01-F01").Id, 3).Value;
        Assert.Equal([Vbat], Node(grown, "C01-L01-S01-F01-P03").Ports);

        var copied = topology.WithNodeCopied(Node(topology, "C01-L01-S01-F01-P02").Id).Value;
        Assert.Equal([Vbat], Node(copied, "C01-L01-S01-F01-P03").Ports);

        var renamed = topology.WithNodeSettings(Node(topology, "C01-L01").Id, "高温层", true).Value;
        Assert.Equal([Voltages], Node(renamed, "C01-L01").Ports);

        var rebuilt = EquipmentTopology.Create(topology.ToDefinition()).Value;
        Assert.Equal([Voltages], Node(rebuilt, "C01-L01").Ports);
        Assert.Equal(4, Node(rebuilt, "C01-L01-S01-F01-P02").RequiredPorts.Count);
    }

    [Fact]
    public void LayoutRebuild_KeepsPortsOfExistingPositions()
    {
        var topology = WithPorts(_topology, "SYS", Vbat);
        topology = WithPorts(topology, "C01-L01-S01", Voltages);

        var definition = new TopologyLayout(1, 1, 3, 1, 2).ToDefinition("老化房", topology).Value;
        var rebuilt = EquipmentTopology.Create(definition).Value;

        Assert.Equal([Vbat], rebuilt.Root.Ports);
        Assert.Equal([Voltages], Node(rebuilt, "C01-L01-S01").Ports);
        Assert.Empty(Node(rebuilt, "C01-L01-S03").Ports);
    }
}
