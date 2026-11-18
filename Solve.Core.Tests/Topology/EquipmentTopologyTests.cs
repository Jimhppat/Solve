using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

public sealed class EquipmentTopologyTests
{
    private static TopologyDefinition Equipment(string name, params TopologyNodeDefinition[] cabinets)
        => new(TopologyNodeId.New(), name, cabinets);

    private static TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children)
        => new(TopologyNodeId.New(), ordinal, children);

    private static TopologyNodeDefinition Products(int fixtureOrdinal, int count)
        => Node(fixtureOrdinal, Enumerable.Range(1, count).Select(i => TopologyNodeDefinition.Leaf(TopologyNodeId.New(), i)).ToArray());

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

    private static IEnumerable<TopologyNode> AllNodes(EquipmentTopology topology)
        => TopologyLevels.All.SelectMany(topology.GetNodes);

    // ---------- 结构与查询 ----------

    [Fact]
    public void HeterogeneousTree_WithGaps_IsAccepted()
    {
        // 1 号柜：2 层；3 号柜（2 号缺位）：1 层，槽位 1、5，治具产品数不同
        var definition = Equipment("异构设备",
            Node(1, Node(1, Node(1, Products(1, 4))), Node(2, Node(1, Products(1, 4)))),
            Node(3, Node(1, Node(1, Products(1, 8), Products(2, 2)), Node(5, Products(1, 1)))));

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
        var topology = EquipmentTopology.Create(Equipment("S", Node(3), Node(1), Node(2))).Value;

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

        Assert.False(topology.TryGetNode(default(TopologyAddress), out _));
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
        var definition = Equipment("  老化房  ",
            Node(1) with { Name = "  A 柜 " },
            Node(2) with { Name = "   " });

        var topology = EquipmentTopology.Create(definition).Value;

        Assert.Equal("老化房", topology.Name);
        Assert.Equal("A 柜", topology.GetNode(TopologyAddress.Of(1)).Value.DisplayName);
        var unnamed = topology.GetNode(TopologyAddress.Of(2)).Value;
        Assert.Null(unnamed.Name);
        Assert.Equal("C02", unnamed.DisplayName);
    }

    // ---------- 双身份：系统 Id 与位置编码 ----------

    [Fact]
    public void EveryNode_IsReachableByIdAndByAddress()
    {
        var topology = Uniform();

        foreach (var node in AllNodes(topology))
        {
            Assert.False(node.Id.IsEmpty);
            Assert.True(topology.TryGetNode(node.Id, out var byId));
            Assert.True(topology.TryGetNode(node.Address, out var byAddress));
            Assert.Same(node, byId);
            Assert.Same(node, byAddress);
        }
    }

    [Fact]
    public void UnknownId_IsNotFound()
    {
        var topology = Uniform();

        Assert.False(topology.TryGetNode(TopologyNodeId.New(), out _));
        Assert.False(topology.TryGetNode(TopologyNodeId.Empty, out _));
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.GetNode(TopologyNodeId.New()).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.CheckEnabled(TopologyNodeId.New()).Error.Code);
    }

    [Fact]
    public void Renumbering_ChangesAddress_ButKeepsId()
    {
        var product = SingleProductEquipment(out var definition);
        var original = EquipmentTopology.Create(definition).Value;
        Assert.Equal(TopologyAddress.Of(1, 1, 1, 1, 1), original.GetNode(product).Value.Address);

        // 现场把 1 号柜改挂为 7 号：位置编码变了，系统身份不变，历史数据仍挂在同一节点上
        var exported = original.ToDefinition();
        var renumbered = exported with { Cabinets = [exported.Cabinets[0] with { Ordinal = 7 }] };
        var topology = EquipmentTopology.Create(renumbered).Value;

        var node = topology.GetNode(product).Value;
        Assert.Equal(TopologyAddress.Of(7, 1, 1, 1, 1), node.Address);
        Assert.False(topology.TryGetNode(TopologyAddress.Of(1, 1, 1, 1, 1), out _));
    }

    private static TopologyNodeId SingleProductEquipment(out TopologyDefinition definition)
    {
        var productId = TopologyNodeId.New();
        definition = Equipment("S", Node(1, Node(1, Node(1, Node(1, TopologyNodeDefinition.Leaf(productId, 1))))));
        return productId;
    }

    [Fact]
    public void ToDefinition_RoundTrips_IdsNamesAndMasks()
    {
        var original = EquipmentTopology.Create(Equipment("老化房",
            Node(1, Node(1, Node(1, Products(1, 2))) with { Enabled = false, Name = "高温层" }),
            Node(2, Node(1, Node(1, Products(1, 3)))) with { Name = "B 柜" })).Value;

        var rebuilt = EquipmentTopology.Create(original.ToDefinition()).Value;

        Assert.Equal(original.Name, rebuilt.Name);
        Assert.Equal(original.NodeCount, rebuilt.NodeCount);
        foreach (var node in AllNodes(original))
        {
            var twin = rebuilt.GetNode(node.Id).Value;
            Assert.Equal(node.Address, twin.Address);
            Assert.Equal(node.Name, twin.Name);
            Assert.Equal(node.IsEnabled, twin.IsEnabled);
            Assert.Equal(node.IsEffectivelyEnabled, twin.IsEffectivelyEnabled);
        }
    }

    [Fact]
    public void Node_ToString_CarriesBothIdentities()
    {
        var topology = EquipmentTopology.Create(Equipment("S", Node(1) with { Name = "A 柜" })).Value;
        var cabinet = topology.GetNode(TopologyAddress.Of(1)).Value;

        Assert.Equal($"柜体 C01（A 柜）[{cabinet.Id}]", cabinet.ToString());
    }

    // ---------- 单节点修改 ----------

    [Fact]
    public void WithNodeSettings_RenamesAndMasks_OnlyThatNode()
    {
        var original = Uniform();
        var layer = original.GetNode(TopologyAddress.Of(1, 2)).Value;

        var edited = original.WithNodeSettings(layer.Id, "  高温层 ", enabled: false).Value;

        var after = edited.GetNode(layer.Id).Value;
        Assert.Equal("高温层", after.Name);
        Assert.False(after.IsEnabled);
        Assert.False(edited.GetNode(TopologyAddress.Of(1, 2, 2, 2, 2)).Value.IsEffectivelyEnabled);
        Assert.True(edited.GetNode(TopologyAddress.Of(1, 1, 1, 1, 1)).Value.IsEffectivelyEnabled);
        foreach (var node in AllNodes(original))
            Assert.Equal(node.Address, edited.GetNode(node.Id).Value.Address);

        // 原拓扑不可变
        Assert.Null(original.GetNode(layer.Id).Value.Name);
        Assert.True(original.GetNode(layer.Id).Value.IsEnabled);
    }

    [Fact]
    public void WithNodeSettings_BlankNameClearsIt()
    {
        var named = EquipmentTopology.Create(Equipment("S", Node(1) with { Name = "A 柜" })).Value;
        var cabinet = named.GetNode(TopologyAddress.Of(1)).Value;

        Assert.Null(named.WithNodeSettings(cabinet.Id, "   ", enabled: true).Value.GetNode(cabinet.Id).Value.Name);
    }

    [Fact]
    public void WithNodeSettings_Root_RenamesSystem_ButCannotBeDisabled()
    {
        var topology = Uniform();

        Assert.Equal("老化房 2#", topology.WithNodeSettings(topology.Root.Id, "老化房 2#", enabled: true).Value.Name);
        Assert.Equal(Field(TopologyErrors.EnabledField),
            topology.WithNodeSettings(topology.Root.Id, "S", enabled: false).Error.Code);
        Assert.Equal(TopologyErrors.InvalidDefinitionCode,
            topology.WithNodeSettings(topology.Root.Id, "  ", enabled: true).Error.Code);
    }

    [Fact]
    public void WithNodeSettings_InvalidInput_Fails()
    {
        var topology = Uniform();
        var cabinet = topology.GetNode(TopologyAddress.Of(1)).Value;

        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.WithNodeSettings(TopologyNodeId.New(), "X", true).Error.Code);
        Assert.Equal(TopologyErrors.InvalidDefinitionCode,
            topology.WithNodeSettings(cabinet.Id, new string('x', EquipmentTopology.MaxNameLength + 1), true).Error.Code);
    }

    // ---------- 停用与联锁 ----------

    [Fact]
    public void DisabledNode_DisablesWholeSubtree()
    {
        var definition = Equipment("S",
            Node(1, Node(1, Node(1, Products(1, 2))) with { Enabled = false }, Node(2, Node(1, Products(1, 2)))));
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
    public void CheckEnabled_ReportsTopmostDisabledAncestor_ByAddressAndById()
    {
        var definition = Equipment("S",
            Node(1, Node(1, Node(1, Products(1, 2) with { Enabled = false })) with { Enabled = false }),
            Node(2, Node(1, Node(1, Products(1, 2)))));
        var topology = EquipmentTopology.Create(definition).Value;
        var target = topology.GetNode(TopologyAddress.Of(1, 1, 1, 1, 1)).Value;

        foreach (var check in new[] { topology.CheckEnabled(target.Address), topology.CheckEnabled(target.Id) })
        {
            Assert.True(check.IsFailure);
            Assert.Equal(TopologyErrors.NodeDisabledCode, check.Error.Code);
            Assert.Contains("层位 C01-L01", check.Error.Message);
        }

        Assert.True(topology.CheckEnabled(TopologyAddress.Of(2, 1, 1, 1, 2)).IsSuccess);
        Assert.True(topology.CheckEnabled(TopologyAddress.System).IsSuccess);
        Assert.True(topology.CheckEnabled(topology.Root.Id).IsSuccess);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.CheckEnabled(TopologyAddress.Of(2, 1, 1, 1, 3)).Error.Code);
        Assert.Equal(TopologyErrors.NodeNotFoundCode, topology.CheckEnabled(default(TopologyAddress)).Error.Code);
    }

    // ---------- 校验 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSystemName_IsRejected(string? name)
        => Assert.Equal([Field(TopologyErrors.NameField)], Codes(Equipment(name!, Node(1))));

    [Fact]
    public void TooLongName_IsRejected()
        => Assert.Equal(
            [Field(TopologyErrors.NameField)],
            Codes(Equipment("S", Node(1) with { Name = new string('x', EquipmentTopology.MaxNameLength + 1) })));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(TopologyAddress.MaxOrdinal + 1)]
    public void OutOfRangeOrdinal_IsRejected(int ordinal)
        => Assert.Equal([Field(TopologyErrors.OrdinalField)], Codes(Equipment("S", Node(1, Node(ordinal)))));

    [Fact]
    public void DuplicateOrdinal_IsRejected()
        => Assert.Equal([Field(TopologyErrors.DuplicateOrdinalField)], Codes(Equipment("S", Node(1, Node(2), Node(2)))));

    [Fact]
    public void EmptyId_IsRejected_ForRootAndChildren()
    {
        var definition = new TopologyDefinition(TopologyNodeId.Empty, "S",
            [Node(1, new TopologyNodeDefinition(TopologyNodeId.Empty, 1, []))]);

        Assert.Equal([Field(TopologyErrors.IdField), Field(TopologyErrors.IdField)], Codes(definition));
    }

    [Fact]
    public void DuplicateId_AcrossBranches_IsRejected()
    {
        // 同级序号不同、位于不同柜体，只要 Id 相同就无法区分
        var shared = TopologyNodeId.New();
        var definition = Equipment("S",
            Node(1, new TopologyNodeDefinition(shared, 1, [])),
            Node(2, new TopologyNodeDefinition(shared, 1, [])));

        var result = EquipmentTopology.Create(definition, out var problems);

        Assert.Equal([Field(TopologyErrors.DuplicateIdField)], problems.Select(p => p.Code));
        Assert.Contains("C01-L01", result.Error.Message);
        Assert.Contains("C02-L01", result.Error.Message);
    }

    [Fact]
    public void ReusedDefinitionSubtree_IsRejected()
    {
        // 同一份子定义挂到两个柜体下 = 两个物理位置共用一套身份，必须拒绝
        var layer = Node(1, Node(1));
        var codes = Codes(Equipment("S", Node(1, layer), Node(2, layer)));

        Assert.Equal([Field(TopologyErrors.DuplicateIdField), Field(TopologyErrors.DuplicateIdField)], codes);
    }

    [Fact]
    public void ProductWithChildren_IsRejected()
        => Assert.Equal(
            [Field(TopologyErrors.DepthField)],
            Codes(Equipment("S", Node(1, Node(1, Node(1, Node(1, Node(1, Node(1)))))))));

    [Fact]
    public void NullListsAndEntries_AreRejected_NotThrown()
    {
        var definition = Equipment("S", new TopologyNodeDefinition(TopologyNodeId.New(), 1, null!), null!);

        Assert.Equal([Field(TopologyErrors.ChildrenField), Field(TopologyErrors.ChildrenField)], Codes(definition));
        Assert.Equal([Field(TopologyErrors.ChildrenField)], Codes(new TopologyDefinition(TopologyNodeId.New(), "S", null!)));
    }

    [Fact]
    public void AllErrors_AreCollectedInOnePass()
    {
        var definition = Equipment("", Node(0), Node(1, Node(1), Node(1)));

        Assert.Equal(
            [Field(TopologyErrors.NameField), Field(TopologyErrors.OrdinalField), Field(TopologyErrors.DuplicateOrdinalField)],
            Codes(definition));
    }

    [Fact]
    public void ReportedErrors_AreCapped_ButTotalIsInMessage()
    {
        // 100 个柜体，每个下面都有一个非法层位序号
        var cabinets = Enumerable.Range(1, 100).Select(i => Node(i, Node(0))).ToArray();

        var result = EquipmentTopology.Create(Equipment("S", cabinets), out var problems);

        Assert.Equal(EquipmentTopology.MaxReportedErrors, problems.Count);
        Assert.Contains("共 100 处错误", result.Error.Message);
    }

    [Fact]
    public void NodeLimit_StopsTraversal()
    {
        // 300 柜 × 700 层 = 210,300 个节点，超过上限
        var cabinets = Enumerable.Range(1, 300)
            .Select(c => Node(c, Enumerable.Range(1, 700).Select(l => TopologyNodeDefinition.Leaf(TopologyNodeId.New(), l)).ToArray()))
            .ToArray();

        Assert.Equal([Field(TopologyErrors.NodeLimitField)], Codes(Equipment("S", cabinets)));
    }

    [Fact]
    public void FailedValidation_NeverReturnsPartialTopology()
    {
        var result = EquipmentTopology.Create(Equipment("S", Node(1), Node(1)));

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
