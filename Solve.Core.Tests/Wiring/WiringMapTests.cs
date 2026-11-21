using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Topology.TestSupport;

namespace Solve.Core.Tests.Wiring;

/// <summary>
/// 接线规则：落点、通道独占、同一路径上同功能同用途只能一路，以及有效接线（向下继承）
/// </summary>
public sealed class WiringMapTests
{
    // 2 柜 × 2 层 × 2 槽 × 1 治具 × 2 产品
    private readonly EquipmentTopology _topology = FakeTopologyRepository.Layout(2, 2, 2, 1, 2);
    private readonly DeviceId _psu = DeviceId.New();
    private readonly DeviceId _daq = DeviceId.New();

    private TopologyNode Node(string address) => _topology.GetNode(TopologyAddress.Parse(address)).Value;

    private Wire Power(string address, int index = 1, string? purpose = null)
        => new(WireId.New(), Node(address).Id, new ChannelRef(_psu, "output", index), ChannelKind.PowerSupply) { Purpose = purpose };

    private Wire Measure(string address, int index)
        => new(WireId.New(), Node(address).Id, new ChannelRef(_daq, "input", index), ChannelKind.Measure);

    private WiringMap Map(params Wire[] wires) => WiringMap.Create(_topology, wires);

    [Fact]
    public void Add_ToProduct_Succeeds()
    {
        var added = Map().WithAdded([Measure("C01-L01-S01-F01-P01", 1)]);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        Assert.Equal(1, added.Value.Count);
    }

    [Fact]
    public void Add_ToSystemRoot_IsRejected()
    {
        var wire = new Wire(WireId.New(), _topology.Root.Id, new ChannelRef(_psu, "output", 1), ChannelKind.PowerSupply);

        Assert.Equal(WiringErrors.SystemNodeCode, Map().WithAdded([wire]).Error.Code);
    }

    [Fact]
    public void Add_ToUnknownNode_IsRejected()
    {
        var wire = new Wire(WireId.New(), TopologyNodeId.New(), new ChannelRef(_psu, "output", 1), ChannelKind.PowerSupply);

        Assert.Equal(WiringErrors.NodeNotFoundCode, Map().WithAdded([wire]).Error.Code);
    }

    [Fact]
    public void Add_WithoutTopology_IsRejected()
        => Assert.Equal(WiringErrors.NoTopologyCode, WiringMap.Empty.WithAdded([Measure("C01-L01-S01-F01-P01", 1)]).Error.Code);

    [Fact]
    public void SameChannel_ToTwoPositions_IsRejected()
    {
        var map = Map(Measure("C01-L01-S01-F01-P01", 3));

        var result = map.WithAdded([Measure("C01-L01-S01-F01-P02", 3)]);

        Assert.Equal(WiringErrors.ChannelInUseCode, result.Error.Code);
        Assert.Contains("C01-L01-S01-F01-P01", result.Error.Message);
    }

    [Fact]
    public void SameChannel_TwiceInOneBatch_IsRejected()
        => Assert.Equal(WiringErrors.ChannelInUseCode,
            Map().WithAdded([Measure("C01-L01-S01-F01-P01", 1), Measure("C01-L01-S01-F01-P02", 1)]).Error.Code);

    /// <summary>
    /// 层位有电源，再给其下的产品位接一路不填用途的电源：两路电源会并到同一个产品上
    /// </summary>
    [Fact]
    public void Power_OnLayerAndOnProductBelow_IsRejected()
    {
        var map = Map(Power("C01-L01"));

        var result = map.WithAdded([Power("C01-L01-S01-F01-P01", index: 2)]);

        Assert.Equal(WiringErrors.PathConflictCode, result.Error.Code);
    }

    /// <summary>
    /// 反过来：产品位已有电源，再给它的上级接同用途电源，同样拒绝（从下级查上级、从上级查下级都要拦）
    /// </summary>
    [Fact]
    public void Power_OnProductThenOnCabinetAbove_IsRejected()
    {
        var map = Map(Power("C01-L02-S02-F01-P02"));

        Assert.Equal(WiringErrors.PathConflictCode, map.WithAdded([Power("C01", index: 2)]).Error.Code);
    }

    [Fact]
    public void DualRail_WithDifferentPurposes_IsAllowed()
    {
        var map = Map(Power("C01-L01", purpose: "VBAT"));

        var result = map.WithAdded([Power("C01-L01-S01-F01-P01", index: 2, purpose: "VCC")]);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Fact]
    public void Purpose_IsComparedIgnoringCaseAndSpaces()
    {
        var map = Map(Power("C01-L01", purpose: "VBAT"));

        Assert.Equal(WiringErrors.PathConflictCode, map.WithAdded([Power("C01-L01-S01", index: 2, purpose: " vbat ")]).Error.Code);
    }

    [Fact]
    public void SameKind_OnSiblingsOrOtherCabinet_IsAllowed()
    {
        var map = Map(Power("C01-L01"));

        var result = map.WithAdded([Power("C01-L02", index: 2), Power("C02", index: 3)]);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Fact]
    public void DifferentKinds_OnSamePath_AreAllowed()
    {
        var map = Map(Power("C01-L01"));

        Assert.True(map.WithAdded([Measure("C01-L01-S01-F01-P01", 1)]).IsSuccess);
    }

    [Fact]
    public void Effective_ListsAncestorsFirst_ThenOwn()
    {
        var layer = Power("C01-L01");
        var product = Measure("C01-L01-S01-F01-P01", 1);
        var other = Measure("C01-L01-S01-F01-P02", 2);
        var map = Map(product, layer, other);

        var effective = map.GetEffective(Node("C01-L01-S01-F01-P01").Id);

        Assert.Equal([layer.Id, product.Id], effective.Select(e => e.Wire.Id));
        Assert.Equal(TopologyLevel.Layer, effective[0].Source.Level);
        Assert.Equal(2, map.CountBelow(Node("C01-L01").Id));
    }

    [Fact]
    public void Create_PutsWiresOnMissingNodesOrDuplicateChannels_InDetached()
    {
        var orphan = new Wire(WireId.New(), TopologyNodeId.New(), new ChannelRef(_psu, "output", 1), ChannelKind.PowerSupply);
        var first = Measure("C01-L01-S01-F01-P01", 1);
        var duplicate = Measure("C01-L01-S01-F01-P02", 1);

        var map = Map(orphan, first, duplicate);

        Assert.Equal([first.Id], map.All.Select(w => w.Id));
        Assert.Equal(2, map.Detached.Count);
    }

    [Fact]
    public void CountRemovedBy_CountsWiresOnDeletedPositions()
    {
        var map = Map(Power("C02"), Measure("C02-L01-S01-F01-P01", 1), Measure("C01-L01-S01-F01-P01", 2));
        var candidate = _topology.WithNodeRemoved(Node("C02").Id).Value;

        Assert.Equal(2, map.CountRemovedBy(candidate));
    }

    [Fact]
    public void Update_ToConflictingPurpose_IsRejected_AndOtherwiseApplied()
    {
        var layer = Power("C01-L01", purpose: "VBAT");
        var product = Power("C01-L01-S01-F01-P01", index: 2, purpose: "VCC");
        var map = Map(layer, product);

        Assert.Equal(WiringErrors.PathConflictCode, map.WithUpdated(product.Id, "vbat", null).Error.Code);

        var updated = map.WithUpdated(product.Id, "AUX", "W-012");
        Assert.True(updated.IsSuccess);
        Assert.True(updated.Value.TryGet(product.Id, out var wire));
        Assert.Equal(("AUX", "W-012"), (wire.Purpose, wire.Label));
    }

    [Fact]
    public void Remove_DropsOnlyGivenWires()
    {
        var a = Measure("C01-L01-S01-F01-P01", 1);
        var b = Measure("C01-L01-S01-F01-P02", 2);

        var map = Map(a, b).WithRemoved([a.Id]);

        Assert.Equal([b.Id], map.All.Select(w => w.Id));
        Assert.False(map.TryGetByChannel(a.Channel, out _));
    }

    /// <summary>
    /// 逐行校验：先出现的一行占用通道 / 路径，后面与它冲突的那一行报错，其余行没有问题
    /// </summary>
    [Fact]
    public void Check_ReportsProblemsPerRow_InOrder()
    {
        var map = Map(Power("C01-L01"));
        var ok = Measure("C01-L01-S01-F01-P01", 1);
        var duplicateChannel = Measure("C01-L01-S01-F01-P02", 1);
        var pathConflict = Power("C01-L01-S02", index: 2);
        var onSystem = new Wire(WireId.New(), _topology.Root.Id, new ChannelRef(_daq, "input", 9), ChannelKind.Measure);

        var problems = map.Check([ok, duplicateChannel, pathConflict, onSystem]);

        Assert.Null(problems[0]);
        Assert.Equal(WiringErrors.ChannelInUseCode, problems[1]!.Code);
        Assert.Contains("C01-L01-S01-F01-P01", problems[1]!.Message);
        Assert.Equal(WiringErrors.PathConflictCode, problems[2]!.Code);
        Assert.Equal(WiringErrors.SystemNodeCode, problems[3]!.Code);
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void InvalidFields_AreRejected()
    {
        var wire = Measure("C01-L01-S01-F01-P01", 1) with { Purpose = new string('x', Wire.MaxPurposeLength + 1) };

        Assert.Equal(WiringErrors.InvalidWireCode, Map().WithAdded([wire]).Error.Code);
    }
}
