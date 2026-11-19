using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;
using Solve.Core.Tests.Twin.TestSupport;
using Solve.UI.ViewModels.Twin;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主控台显示区（纯展示面板）：从系统根按显示数量上限展开、点击只选中不进入下一级、
/// 选中详情、按变化刷新、拓扑修改后重建
/// </summary>
public sealed class TwinBoardViewModelTests
{
    private readonly ManualTimeProvider _time = new(Twins.T0);
    private readonly FixedTopologyService _topology = new();

    private (TwinBoardViewModel Board, TwinStateStore Store) Attached(EquipmentTopology? topology, int budget = TwinBoardViewModel.DefaultElementBudget)
    {
        _topology.Current = topology;
        var store = Twins.Store(_topology, _time);
        var board = new TwinBoardViewModel(budget);
        board.Attach(store);
        return (board, store);
    }

    private static IEnumerable<TwinNodeViewModel> Flatten(TwinNodeViewModel node)
        => node.Children.SelectMany(Flatten).Prepend(node);

    [Fact]
    public void Designer_ShowsWhyThereIsNothing()
    {
        var board = new TwinBoardViewModel();

        Assert.False(board.IsAttached);
        Assert.True(board.ShowEmpty);
        Assert.Contains("配置库", board.EmptyText);
        Assert.Null(board.Root);
    }

    [Fact]
    public void NotConfigured_AsksForATopology()
    {
        var (board, _) = Attached(null);

        Assert.True(board.ShowEmpty);
        Assert.Contains("尚未建档", board.EmptyText);
    }

    [Fact]
    public void SmallTopology_IsDrawnDownToProducts()
    {
        var topology = Twins.Layout(1, 1, 2, 2, 4); // 1 + 1 + 1 + 2 + 4 + 16 = 25 个节点

        var (board, _) = Attached(topology);

        var system = Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.Equal(25, board.VisibleCount);
        var nodes = Flatten(system).ToList();
        Assert.Equal(16, nodes.OfType<ProductNodeViewModel>().Count());
        Assert.All(nodes.Where(n => n is not ProductNodeViewModel), n => Assert.True(n.IsExpanded));

        // 等分网格的行列数：柜体 1 个一行；每治具 4 个产品位，一行放满 4 个
        Assert.Equal(1, system.ChildCount);
        Assert.All(nodes.OfType<FixtureNodeViewModel>(), fixture => Assert.Equal(4, fixture.ProductColumns));
    }

    [Fact]
    public void FixtureWithFewProducts_UsesAllColumnsForThem()
    {
        var (board, _) = Attached(Twins.Layout(1, 1, 1, 1, 2));

        var fixture = Flatten(board.Root!).OfType<FixtureNodeViewModel>().Single();

        // 只有 2 个产品位时一行 2 列，格子铺满整行，不留两个空位
        Assert.Equal(2, fixture.ProductColumns);
    }

    [Fact]
    public void LargeTopology_StopsAtBudget_DeepestLevelInSummaryMode()
    {
        // 逐层：柜 2、层 4、槽 8、治具位 16、产品位 64；上限 20 时画到槽位（1 + 2 + 4 + 8 = 15）
        var (board, _) = Attached(Twins.Layout(2, 2, 2, 2, 4), budget: 20);

        Assert.Equal(15, board.VisibleCount);
        var nodes = Flatten(board.Root!).ToList();
        var slots = nodes.OfType<SlotNodeViewModel>().ToList();
        Assert.Equal(8, slots.Count);
        Assert.All(slots, slot => Assert.True(slot.IsSummary));
        Assert.All(nodes.OfType<LayerNodeViewModel>(), layer => Assert.True(layer.IsExpanded));
        Assert.Empty(nodes.OfType<FixtureNodeViewModel>());
    }

    [Fact]
    public void TinyBudget_StillShowsTheCabinets()
    {
        var (board, _) = Attached(Twins.Layout(3, 2, 2, 2, 4), budget: 1);

        var cabinets = Flatten(board.Root!).OfType<CabinetNodeViewModel>().ToList();
        Assert.Equal(3, cabinets.Count);
        Assert.All(cabinets, cabinet => Assert.True(cabinet.IsSummary));
    }

    // ==================== 点击：只选中，不进入下一级 ====================

    [Fact]
    public void SelectingContainer_ShowsDetails_WithoutChangingTheView()
    {
        var (board, _) = Attached(Twins.Layout(2, 2, 2, 2, 4), budget: 20);
        var root = board.Root;
        var layer = Flatten(board.Root!).OfType<LayerNodeViewModel>().First(l => l.Node.Address == TopologyAddress.Of(2, 1));

        layer.Select();

        // 画面不变：根还是系统，可见节点不变，层位仍按原来的上限画到槽位摘要
        Assert.Same(root, board.Root);
        Assert.Equal(15, board.VisibleCount);
        Assert.True(layer.IsSelected);
        Assert.Equal("层位 C02-L01", board.SelectedTitle);
        Assert.Equal("无数据 16", board.SelectedSummaryText);
        Assert.False(board.IsProductSelected);
    }

    [Fact]
    public void SelectingProduct_ShowsItsDetails()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 4);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().Last();
        store.Report(product.Id, ProductState.Running, "SN-9");
        board.Refresh();

        product.Select();

        Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.True(product.IsSelected);
        Assert.True(board.IsProductSelected);
        Assert.Equal("产品位 C01-L01-S01-F01-P04", board.SelectedTitle);
        Assert.Equal("测试中", board.SelectedStateText);
        Assert.Equal("SN-9", board.SelectedSerialText);
        Assert.Equal(product.Id.ToString(), board.SelectedIdText);
        Assert.NotEqual("从未收到", board.SelectedUpdatedText);
    }

    [Fact]
    public void SelectingAnotherNode_MovesTheHighlight()
    {
        var (board, _) = Attached(Twins.Layout(2, 1, 1, 1, 1));
        var cabinets = Flatten(board.Root!).OfType<CabinetNodeViewModel>().ToList();

        cabinets[0].Select();
        cabinets[1].Select();

        Assert.False(cabinets[0].IsSelected);
        Assert.True(cabinets[1].IsSelected);
        Assert.Equal("柜体 C02", board.SelectedTitle);
    }

    // ==================== 刷新 ====================

    [Fact]
    public void Refresh_UpdatesChangedProducts_AndTheirContainers()
    {
        var topology = Twins.Layout(1, 1, 2, 1, 4);
        var (board, store) = Attached(topology);
        var nodes = Flatten(board.Root!).ToList();
        var product = nodes.OfType<ProductNodeViewModel>().First();

        store.Report(product.Id, ProductState.Fault);
        board.Refresh();

        Assert.Equal(ProductState.Fault, product.State);
        var slot = nodes.OfType<SlotNodeViewModel>().First();
        Assert.Equal(ProductState.Fault, slot.Worst);
        Assert.Contains("故障 1", slot.AlarmText);
        Assert.Equal(ProductState.Fault, board.Root!.Worst);

        // 另一个槽位没有变化
        Assert.Equal(ProductState.Unknown, nodes.OfType<SlotNodeViewModel>().Last().Worst);
    }

    [Fact]
    public void Refresh_UpdatesTheSelectedDetails()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 1);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().Single();
        product.Select();
        Assert.Equal("无数据", board.SelectedStateText);

        store.Report(product.Id, ProductState.Passed, "SN-1");
        board.Refresh();

        Assert.Equal("合格", board.SelectedStateText);
        Assert.Equal("SN-1", board.SelectedSerialText);
    }

    [Fact]
    public void Refresh_ExpiresSilentProducts()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().First();
        store.Report(product.Id, ProductState.Passed);
        board.Refresh();
        Assert.Equal(ProductState.Passed, product.State);

        _time.Advance(TimeSpan.FromSeconds(11));
        for (var i = 0; i < 4; i++) // 过期检查每 4 次刷新做一次
            board.Refresh();

        Assert.Equal(ProductState.Unknown, product.State);
    }

    // ==================== 拓扑修改 ====================

    [Fact]
    public void TopologyChange_RebuildsAndKeepsSelectionById()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var cabinet = Flatten(board.Root!).OfType<CabinetNodeViewModel>().Last();
        cabinet.Select();

        // 改名：拓扑被替换，节点 Id 不变
        _topology.Current = topology.WithNodeSettings(cabinet.Id, "B 柜", enabled: true).Value;
        board.Refresh();

        var rebuilt = Flatten(board.Root!).OfType<CabinetNodeViewModel>().Last();
        Assert.NotSame(cabinet, rebuilt);
        Assert.Equal(cabinet.Id, rebuilt.Id);
        Assert.Equal("B 柜", rebuilt.Name);
        Assert.True(rebuilt.IsSelected);
        Assert.Equal("B 柜", board.SelectedNameText);
        Assert.Same(_topology.Current, store.Topology);
    }

    [Fact]
    public void SelectedNodeRemoved_ClearsSelection()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        Flatten(board.Root!).OfType<CabinetNodeViewModel>().Last().Select();

        _topology.Current = Twins.Layout(1, 1, 1, 1, 2, current: topology);
        board.Refresh();

        Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.False(board.HasSelection);
        Assert.Null(board.SelectedTitle);
    }

    [Fact]
    public void DisabledSubtree_IsDimmedOnce_AndExcludedFromColors()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var masked = topology.WithNodeSettings(Twins.Id(topology, 2), null, enabled: false).Value;
        var (board, _) = Attached(masked);

        var cabinets = Flatten(board.Root!).OfType<CabinetNodeViewModel>().ToList();
        Assert.True(cabinets[1].IsDisabled);
        Assert.Equal(TwinNodeViewModel.DisabledOpacity, cabinets[1].ContentOpacity);
        Assert.Null(cabinets[1].Worst);
        Assert.False(cabinets[0].IsDisabled);
        Assert.Equal(ProductState.Unknown, cabinets[0].Worst);

        // 透明度逐层相乘：停用柜体下的节点不再各自变淡，否则 0.4⁵ 几乎看不见
        Assert.All(Flatten(cabinets[1]).Skip(1), node =>
        {
            Assert.True(node.IsDisabled);
            Assert.Equal(1.0, node.ContentOpacity);
        });
    }
}
