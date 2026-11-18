using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

public sealed class EquipmentTopologyTests
{
    private static TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children)
        => new(ordinal, children);

    private static TopologyNodeDefinition Products(int fixtureOrdinal, int count)
        => new(fixtureOrdinal, Enumerable.Range(1, count).Select(TopologyNodeDefinition.Leaf).ToArray());

    /// <summary>
    /// 统一布局：2 柜 × 2 层 × 2 槽 × 2 治具位 × 2 产品位
    /// </summary>
    private static EquipmentTopology Uniform()
        => EquipmentTopology.Create(new TopologyLayout(2, 2, 2, 2, 2).ToDefinition("SYS-A").Value).Value;

    private static IReadOnlyList<string> Codes(TopologyDefinition definition)
    {
        var result = EquipmentTopology.Create(definition, out var problems);
        Assert.True(result.IsFailure);
        Assert.Equal(TopologyErrors.InvalidDefinitionCode, result.Error.Code);
        return problems.Select(e => e.Code).ToList();
    }

    private static string Field(string field) => TopologyErrors.DefinitionCodePrefix + field;

    // ---------- 结构与查询 ----------

    [Fact]
    public void HeterogeneousTree_WithGaps_IsAccepted()
    {
        // 1 号柜：2 层；3 号柜（2 号缺位）：1 层，槽位 1、5，治具产品数不同
        var definition = new TopologyDefinition("异构设备",
        [
            Node(1, Node(1, Node(1, Products(1, 4))), Node(2, Node(1, Products(1, 4)))),
            Node(3, Node(1, Node(1, Products(1, 8), Products(2, 2)), Node(5, Products(1, 1)))),
        ]);

        var result = EquipmentTopology.Create(definition, out var problems);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Empty(problems);
        var topology = result.Value;
        Assert.Equal(2, topology.CountAt(TopologyLevel.Cabinet));
        Assert.Equal(4 + 4 + 8 + 2 + 1, topology.CountAt(TopologyLevel.Product));
        Assert.False(topology.TryGetNode(TopologyAddress.Of(2), out _));
        Assert.True(topology.TryGetNode(TopologyAddress.Of(3, 1, 5, 1, 1), out var product));
        Assert.Equal(TopologyLevel.Product, product.Level);
    }

    [Fact]
    public void Children_AreSortedByOrdinal_RegardlessOfDefinitionOrder()
    {
        var topology = EquipmentTopology.Create(new TopologyDefinition("S", [Node(3), Node(1), Node(2)])).Value;

        Assert.Equal([1, 2, 3], topology.Root.Children.Select(c => c.Ordinal));
        Assert.Equal([1, 2, 3], topology.GetNodes(TopologyLevel.Cabinet).Select(c => c.Ordinal));
    }

    [Fact]
    public void GetNodes_PerLevel_IsInAddressOrder()
    {
        var topology = Uniform();

        foreach (var level in TopologyLevels.All)
        {
            var addresses = topology.GetNodes(level).Select(n => n.Address).ToList();
            Assert.All(addresses, a => Assert.Equal(level, a.Level));
            Assert.Equal(addresses.Order(), addresses);
        }

        Assert.Equal(1 + 2 + 4 + 8 + 16 + 32, topology.NodeCount);
        Assert.Same(topology.Root, Assert.Single(topology.GetNodes(TopologyLevel.System)));
    }

    [Fact]
    public void TryGetNode_EmptyOrMissingAddress_ReturnsFalse()
    {
        var topology = Uniform();

        Assert.False(topology.TryGetNode(default, out _));
        Assert.False(topology.TryGetNode(TopologyAddress.Of(3), out _));
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.GetNode(TopologyAddress.Of(1, 1, 1, 1, 3)).Error.Code);
        Assert.True(topology.TryGetNode(TopologyAddress.System, out var root));
        Assert.Same(topology.Root, root);
    }

    [Fact]
    public void GetDescendants_ReturnsExactlyTheSubtreeSlice()
    {
        var topology = Uniform();
        var cabinet2 = TopologyAddress.Of(2);

        var products = topology.GetDescendants(cabinet2, TopologyLevel.Product);

        Assert.Equal(16, products.Count);
        Assert.All(products, p => Assert.True(cabinet2.Contains(p.Address)));
        Assert.Equal(
            topology.GetNodes(TopologyLevel.Product).Where(p => cabinet2.Contains(p.Address)).Select(p => p.Address),
            products.Select(p => p.Address));
    }

    [Fact]
    public void GetDescendants_EdgeCases()
    {
        var topology = Uniform();
        var slot = TopologyAddress.Of(1, 2, 1);

        Assert.Equal(32, topology.GetDescendants(TopologyAddress.System, TopologyLevel.Product).Count);
        Assert.Equal(slot, Assert.Single(topology.GetDescendants(slot, TopologyLevel.Slot)).Address);
        Assert.Empty(topology.GetDescendants(TopologyAddress.Of(9), TopologyLevel.Product));
        Assert.Empty(topology.GetDescendants(default, TopologyLevel.Product));
        Assert.Throws<ArgumentOutOfRangeException>(() => topology.GetDescendants(slot, TopologyLevel.Cabinet));
        Assert.Throws<ArgumentOutOfRangeException>(() => topology.GetDescendants(slot, (TopologyLevel)7));
    }

    [Fact]
    public void Names_AreTrimmed_BlankFallsBackToAddress()
    {
        var definition = new TopologyDefinition("  老化房  ",
        [
            Node(1) with { Name = "  A 柜 " },
            Node(2) with { Name = "   " },
        ]);

        var topology = EquipmentTopology.Create(definition).Value;

        Assert.Equal("老化房", topology.Name);
        Assert.Equal("A 柜", topology.GetNode(TopologyAddress.Of(1)).Value.DisplayName);
        var unnamed = topology.GetNode(TopologyAddress.Of(2)).Value;
        Assert.Null(unnamed.Name);
        Assert.Equal("C02", unnamed.DisplayName);
    }

    // ---------- 停用与联锁 ----------

    [Fact]
    public void DisabledNode_DisablesWholeSubtree()
    {
        var definition = new TopologyDefinition("S",
        [
            Node(1, Node(1, Node(1, Products(1, 2))) with { Enabled = false }, Node(2, Node(1, Products(1, 2)))),
        ]);
        var topology = EquipmentTopology.Create(definition).Value;

        var disabledLayer = topology.GetNode(TopologyAddress.Of(1, 1)).Value;
        var productUnderIt = topology.GetNode(TopologyAddress.Of(1, 1, 1, 1, 2)).Value;
        var productElsewhere = topology.GetNode(TopologyAddress.Of(1, 2, 1, 1, 2)).Value;

        Assert.False(disabledLayer.IsEnabled);
        Assert.True(productUnderIt.IsEnabled);
        Assert.False(productUnderIt.IsEffectivelyEnabled);
        Assert.True(productElsewhere.IsEffectivelyEnabled);
        Assert.True(topology.GetNode(TopologyAddress.Of(1)).Value.IsEffectivelyEnabled);
    }

    [Fact]
    public void CheckEnabled_ReportsTopmostDisabledAncestor()
    {
        var definition = new TopologyDefinition("S",
        [
            Node(1, Node(1, Node(1, Products(1, 2) with { Enabled = false })) with { Enabled = false }),
            Node(2, Node(1, Node(1, Products(1, 2)))),
        ]);
        var topology = EquipmentTopology.Create(definition).Value;
        var target = TopologyAddress.Of(1, 1, 1, 1, 1);

        var check = topology.CheckEnabled(target);

        Assert.True(check.IsFailure);
        Assert.Equal(TopologyErrors.NodeDisabledCode, check.Error.Code);
        Assert.Contains("层位 C01-L01", check.Error.Message);

        Assert.True(topology.CheckEnabled(TopologyAddress.Of(2, 1, 1, 1, 2)).IsSuccess);
        Assert.True(topology.CheckEnabled(TopologyAddress.System).IsSuccess);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.CheckEnabled(TopologyAddress.Of(2, 1, 1, 1, 3)).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.CheckEnabled(default).Error.Code);
    }

    // ---------- 校验 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSystemName_IsRejected(string? name)
        => Assert.Equal([Field(TopologyErrors.NameField)], Codes(new TopologyDefinition(name!, [Node(1)])));

    [Fact]
    public void TooLongName_IsRejected()
    {
        var definition = new TopologyDefinition("S",
            [Node(1) with { Name = new string('x', EquipmentTopology.MaxNameLength + 1) }]);

        Assert.Equal([Field(TopologyErrors.NameField)], Codes(definition));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(TopologyAddress.MaxOrdinal + 1)]
    public void OutOfRangeOrdinal_IsRejected(int ordinal)
        => Assert.Equal([Field(TopologyErrors.OrdinalField)], Codes(new TopologyDefinition("S", [Node(1, Node(ordinal))])));

    [Fact]
    public void DuplicateOrdinal_IsRejected()
        => Assert.Equal(
            [Field(TopologyErrors.DuplicateOrdinalField)],
            Codes(new TopologyDefinition("S", [Node(1, Node(2), Node(2))])));

    [Fact]
    public void ProductWithChildren_IsRejected()
    {
        var definition = new TopologyDefinition("S", [Node(1, Node(1, Node(1, Node(1, Node(1, Node(1))))))]);

        Assert.Equal([Field(TopologyErrors.DepthField)], Codes(definition));
    }

    [Fact]
    public void NullListsAndEntries_AreRejected_NotThrown()
    {
        var definition = new TopologyDefinition("S",
        [
            new TopologyNodeDefinition(1, null!),
            null!,
        ]);

        Assert.Equal([Field(TopologyErrors.ChildrenField), Field(TopologyErrors.ChildrenField)], Codes(definition));
        Assert.Equal([Field(TopologyErrors.ChildrenField)], Codes(new TopologyDefinition("S", null!)));
    }

    [Fact]
    public void AllErrors_AreCollectedInOnePass()
    {
        var definition = new TopologyDefinition("", [Node(0), Node(1, Node(1), Node(1))]);

        Assert.Equal(
            [Field(TopologyErrors.NameField), Field(TopologyErrors.OrdinalField), Field(TopologyErrors.DuplicateOrdinalField)],
            Codes(definition));
    }

    [Fact]
    public void ReportedErrors_AreCapped_ButTotalIsInMessage()
    {
        // 100 个柜体，每个下面都有一个非法层位序号
        var cabinets = Enumerable.Range(1, 100).Select(i => Node(i, Node(0))).ToArray();

        var result = EquipmentTopology.Create(new TopologyDefinition("S", cabinets), out var problems);

        Assert.Equal(EquipmentTopology.MaxReportedErrors, problems.Count);
        Assert.Contains("共 100 处错误", result.Error.Message);
    }

    [Fact]
    public void NodeLimit_StopsTraversal()
    {
        // 共享子定义：定义本身很小，但展开后 1 + 999 × 999 远超上限
        var layers = Enumerable.Range(1, TopologyAddress.MaxOrdinal).Select(TopologyNodeDefinition.Leaf).ToArray();
        var cabinets = Enumerable.Range(1, TopologyAddress.MaxOrdinal).Select(i => new TopologyNodeDefinition(i, layers)).ToArray();

        var codes = Codes(new TopologyDefinition("S", cabinets));

        Assert.Equal([Field(TopologyErrors.NodeLimitField)], codes);
    }

    [Fact]
    public void FailedValidation_NeverReturnsPartialTopology()
    {
        var result = EquipmentTopology.Create(new TopologyDefinition("S", [Node(1), Node(1)]));

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Error_IsStableResultType()
    {
        Error error = TopologyErrors.NodeNotFound(TopologyAddress.Of(1, 2));

        Assert.Equal("Topology.NodeNotFound", error.Code);
        Assert.Contains("C01-L02", error.Message);
    }
}
