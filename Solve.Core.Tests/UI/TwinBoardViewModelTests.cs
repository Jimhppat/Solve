using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;
using Solve.Core.Tests.Twin.TestSupport;
using Solve.UI.ViewModels.Twin;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主控台显示区（纯展示面板）：一次画一个柜体并按显示数量上限展开、柜体切换、点击只选中不进入下一级、
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
    public void LargeCabinet_StopsAtBudget_DeepestLevelInSummaryMode()
    {
        // 只算当前柜体：系统 1 + 柜体胶囊 2 = 3；C01 逐层：层 2、槽 4、治具位 8、产品位 32；
        // 上限 20 时画到治具位（3 + 2 + 4 + 8 = 17）
        var (board, _) = Attached(Twins.Layout(2, 2, 2, 2, 4), budget: 20);

        Assert.Equal(17, board.VisibleCount);
        var nodes = Flatten(board.Root!).ToList();
        var fixtures = nodes.OfType<FixtureNodeViewModel>().ToList();
        Assert.Equal(8, fixtures.Count);
        Assert.All(fixtures, fixture => Assert.True(fixture.IsSummary));
        Assert.All(nodes.OfType<SlotNodeViewModel>(), slot => Assert.True(slot.IsExpanded));
        Assert.Empty(nodes.OfType<ProductNodeViewModel>());
    }

    [Fact]
    public void Budget_CountsOnlyTheShownCabinet()
    {
        // 整套设备 1 + 4 + 4 + 4 + 4 + 16 = 33 个节点超过上限 15；只画一个柜体时 5 + 1 + 1 + 1 + 4 = 12，能画到产品位
        var (board, _) = Attached(Twins.Layout(4, 1, 1, 1, 4), budget: 15);

        Assert.Equal(12, board.VisibleCount);
        Assert.Equal(4, Flatten(board.Root!).OfType<ProductNodeViewModel>().Count());
    }

    [Fact]
    public void TinyBudget_StillShowsTheCabinetsAndLayers()
    {
        var (board, _) = Attached(Twins.Layout(3, 2, 2, 2, 4), budget: 1);

        // 全部柜体都有胶囊；当前柜体至少展开到层位，层位用摘要模式
        var system = Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.Equal(3, system.ChildCount);
        var current = Assert.IsType<CabinetNodeViewModel>(board.CurrentCabinet);
        Assert.Same(system.Children[0], current);
        Assert.Equal(2, current.ChildCount);
        Assert.All(current.Children, layer => Assert.True(layer.IsSummary));
        Assert.All(system.Children.Skip(1), cabinet => Assert.True(cabinet.IsSummary));
    }

    // ==================== 柜体切换 ====================

    [Fact]
    public void MultipleCabinets_ShowTheFirstCabinetOnly()
    {
        var (board, _) = Attached(Twins.Layout(3, 1, 1, 1, 2));

        var system = Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.True(system.HasCabinetChoice);
        Assert.Equal(["C01", "C02", "C03"], system.Children.Select(cabinet => cabinet.ShortCode));
        Assert.Same(system.Children[0], system.CurrentCabinet);
        Assert.Same(system.CurrentCabinet, board.CurrentCabinet);
        Assert.Equal([true, false, false], system.Children.Select(cabinet => cabinet.IsExpanded));

        var products = Flatten(system).OfType<ProductNodeViewModel>().ToList();
        Assert.Equal(2, products.Count);
        Assert.All(products, product => Assert.StartsWith("C01-", product.Node.Address.ToString()));
    }

    [Fact]
    public void SingleCabinet_HidesTheSwitcher()
    {
        var (board, _) = Attached(Twins.Layout(1, 1, 1, 1, 1));

        var system = Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.False(system.HasCabinetChoice);
        Assert.NotNull(board.CurrentCabinet);
    }

    [Fact]
    public void ShowCabinet_SwitchesTheDisplay_AndReleasesTheOldCabinet()
    {
        var topology = Twins.Layout(3, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        var system = Assert.IsType<SystemNodeViewModel>(board.Root);
        var before = board.VisibleCount; // 1 + 3 + 1 + 1 + 1 + 2 = 9

        board.ShowCabinet(Twins.Id(topology, 2));

        // 不重建整个画面：系统与胶囊是同一批实例，只换当前柜体
        Assert.Same(system, board.Root);
        Assert.Same(system.Children[1], board.CurrentCabinet);
        Assert.True(system.Children[0].IsSummary);
        Assert.Empty(system.Children[0].Children);
        Assert.Equal(9, before);
        Assert.Equal(before, board.VisibleCount);
        Assert.All(Flatten(system).OfType<ProductNodeViewModel>(),
            product => Assert.StartsWith("C02-", product.Node.Address.ToString()));
    }

    [Fact]
    public void Switcher_SetsTheCurrentCabinet_AndNullIsIgnored()
    {
        var (board, _) = Attached(Twins.Layout(3, 1, 1, 1, 1));
        var system = Assert.IsType<SystemNodeViewModel>(board.Root);

        // 切换胶囊双向绑定 CurrentCabinet
        system.CurrentCabinet = system.Children[2];
        Assert.Same(system.Children[2], board.CurrentCabinet);

        // 胶囊在重建过程中可能短暂回传 null：不能因此清空画面
        system.CurrentCabinet = null;
        Assert.Same(system.Children[2], board.CurrentCabinet);
    }

    [Fact]
    public void ShowCabinet_IgnoresOtherLevelsAndUnknownIds()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 1);
        var (board, _) = Attached(topology);
        var current = board.CurrentCabinet;

        board.ShowCabinet(Twins.Id(topology, 2, 1));       // C02 的层位
        board.ShowCabinet(Twins.Id(topology, 2, 1, 1, 1, 1)); // C02 的产品位
        board.ShowCabinet(TopologyNodeId.New());

        Assert.Same(current, board.CurrentCabinet);
        Assert.False(board.HasRefreshError);
    }

    [Fact]
    public void HiddenCabinet_KeepsItsSwitcherStatusUpToDate()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var system = Assert.IsType<SystemNodeViewModel>(board.Root);

        // C02 没在显示，它出了两个不良品：胶囊上的圆点与红色计数照样更新
        store.Report(Twins.Id(topology, 2, 1, 1, 1, 1), ProductState.Defective);
        store.Report(Twins.Id(topology, 2, 1, 1, 1, 2), ProductState.Defective);
        board.Refresh();

        var hidden = system.Children[1];
        Assert.Equal(ProductState.Defective, hidden.Worst);
        Assert.Equal(2, hidden.AlarmCount);
        Assert.True(hidden.HasAlarm);
        Assert.Equal(0, system.Children[0].AlarmCount);
        Assert.Equal(ProductState.Defective, system.Worst);
    }

    [Fact]
    public void Selection_SurvivesSwitching_AndIsHighlightedAgainOnReturn()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        Flatten(board.Root!).OfType<ProductNodeViewModel>().First().Select();

        board.ShowCabinet(Twins.Id(topology, 2));

        // 控制区继续显示 C01 的产品位；C02 画面上没有高亮
        Assert.True(board.HasSelection);
        Assert.Equal("C01-L01-S01-F01-P01", board.SelectedAddressText);
        Assert.DoesNotContain(Flatten(board.Root!).OfType<ProductNodeViewModel>(), product => product.IsSelected);

        board.ShowCabinet(Twins.Id(topology, 1));

        Assert.True(Flatten(board.Root!).OfType<ProductNodeViewModel>().First().IsSelected);
    }

    [Fact]
    public void TopologyChange_KeepsTheShownCabinet()
    {
        var topology = Twins.Layout(3, 1, 1, 1, 1);
        var (board, _) = Attached(topology);
        var root = board.Root;
        board.ShowCabinet(Twins.Id(topology, 3));

        _topology.Current = topology.WithNodeSettings(Twins.Id(topology, 1), "A 柜", enabled: true).Value;
        board.Refresh();

        Assert.NotSame(root, board.Root);
        Assert.Equal(Twins.Id(topology, 3), board.CurrentCabinet!.Id);
        Assert.True(board.CurrentCabinet.IsExpanded);
    }

    // ==================== 点击：只有产品位可选中，画面不变 ====================

    [Fact]
    public void SelectingProduct_ShowsProductInfo_WithoutChangingTheView()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 4);
        var (board, store) = Attached(topology);
        var root = board.Root;
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().Last();
        store.Report(product.Id, ProductState.Standby, "SN-9");
        board.Refresh();

        product.Select();

        Assert.Same(root, board.Root);
        Assert.True(product.IsSelected);
        Assert.True(board.HasSelection);
        Assert.Equal("C01-L01-S01-F01-P04", board.SelectedAddressText);
        Assert.Equal("待机", board.SelectedStateText);
        Assert.Equal("SN-9", board.SelectedSerialText);
        Assert.Equal(product.Id.ToString(), board.SelectedIdText);
        Assert.NotEqual("从未收到", board.SelectedUpdatedText);
        Assert.False(board.HasSelectedName);
    }

    [Fact]
    public void Containers_CannotBeSelected()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().First();
        product.Select();

        // 柜体、层位、槽位、治具位、系统的 Id 一律忽略，当前选中的产品位保持不变
        foreach (var container in Flatten(board.Root!).Where(node => node is not ProductNodeViewModel))
            board.Select(container.Id);
        board.Select(TopologyNodeId.New());

        Assert.True(product.IsSelected);
        Assert.Equal(product.Id.ToString(), board.SelectedIdText);
    }

    [Fact]
    public void SelectingAnotherProduct_MovesTheHighlight()
    {
        var (board, _) = Attached(Twins.Layout(1, 1, 1, 1, 2));
        var products = Flatten(board.Root!).OfType<ProductNodeViewModel>().ToList();

        products[0].Select();
        products[1].Select();

        Assert.False(products[0].IsSelected);
        Assert.True(products[1].IsSelected);
        Assert.Equal("C01-L01-S01-F01-P02", board.SelectedAddressText);
    }

    [Fact]
    public void NamedProduct_ShowsItsName()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 1);
        var named = topology.WithNodeSettings(Twins.Id(topology, 1, 1, 1, 1, 1), "左上角", enabled: true).Value;
        var (board, _) = Attached(named);

        Flatten(board.Root!).OfType<ProductNodeViewModel>().Single().Select();

        Assert.True(board.HasSelectedName);
        Assert.Equal("左上角", board.SelectedNameText);
    }

    // ==================== 刷新 ====================

    [Fact]
    public void Refresh_UpdatesChangedProducts_AndTheirContainers()
    {
        var topology = Twins.Layout(1, 1, 2, 1, 4);
        var (board, store) = Attached(topology);
        var nodes = Flatten(board.Root!).ToList();
        var product = nodes.OfType<ProductNodeViewModel>().First();

        store.Report(product.Id, ProductState.Defective);
        board.Refresh();

        Assert.Equal(ProductState.Defective, product.State);
        var slot = nodes.OfType<SlotNodeViewModel>().First();
        Assert.Equal(ProductState.Defective, slot.Worst);
        Assert.Contains("不良品 1", slot.AlarmText);
        Assert.Equal(ProductState.Defective, board.Root!.Worst);

        // 另一个槽位没有变化
        Assert.Equal(ProductState.Idle, nodes.OfType<SlotNodeViewModel>().Last().Worst);
    }

    [Fact]
    public void Refresh_UpdatesTheSelectedDetails()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 1);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().Single();
        product.Select();
        Assert.Equal("空闲", board.SelectedStateText);

        store.Report(product.Id, ProductState.Good, "SN-1");
        board.Refresh();

        Assert.Equal("良品", board.SelectedStateText);
        Assert.Equal("SN-1", board.SelectedSerialText);
    }

    [Fact]
    public void NewProducts_ShowIdle_NotNoData()
    {
        var (board, _) = Attached(Twins.Layout(1, 1, 1, 1, 2));

        // 没有收到任何上报的产品位默认空闲
        var products = Flatten(board.Root!).OfType<ProductNodeViewModel>().ToList();
        Assert.All(products, product =>
        {
            Assert.Equal(ProductState.Idle, product.State);
            Assert.Equal("空闲", product.StateText);
        });
        Assert.Equal(ProductState.Idle, board.Root!.Worst);
        Assert.False(board.Root.HasAlarm);
        Assert.Equal("空闲 2", board.Root.SummaryText);

        products[0].Select();
        Assert.Equal("从未收到", board.SelectedUpdatedText);
    }

    [Fact]
    public void Refresh_NoTimeout_KeepsTheLastState()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().First();
        store.Report(product.Id, ProductState.Good);
        board.Refresh();
        Assert.Equal(ProductState.Good, product.State);

        // 暂不做超时判断：很久没有上报也不会变回空闲
        _time.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 8; i++)
            board.Refresh();

        Assert.Equal(ProductState.Good, product.State);
    }

    // ==================== 拓扑修改 ====================

    [Fact]
    public void TopologyChange_RebuildsAndKeepsSelectionById()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        board.ShowCabinet(Twins.Id(topology, 2));
        var product = Flatten(board.Root!).OfType<ProductNodeViewModel>().Last();
        product.Select();

        // 给所在柜体改名：拓扑被替换，产品位 Id 不变；重建后仍显示 C02
        _topology.Current = topology.WithNodeSettings(Twins.Id(topology, 2), "B 柜", enabled: true).Value;
        board.Refresh();

        var rebuilt = Flatten(board.Root!).OfType<ProductNodeViewModel>().Last();
        Assert.NotSame(product, rebuilt);
        Assert.Equal(product.Id, rebuilt.Id);
        Assert.True(rebuilt.IsSelected);
        Assert.Equal("C02-L01-S01-F01-P02", board.SelectedAddressText);
        Assert.Equal("B 柜", Flatten(board.Root!).OfType<CabinetNodeViewModel>().Last().Name);
        Assert.Same(_topology.Current, store.Topology);
    }

    [Fact]
    public void ShownCabinetRemoved_FallsBackToTheFirst_AndClearsItsSelection()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        board.ShowCabinet(Twins.Id(topology, 2));
        Flatten(board.Root!).OfType<ProductNodeViewModel>().Last().Select();

        // 删掉 C02（选中的产品位随之被删）
        _topology.Current = Twins.Layout(1, 1, 1, 1, 2, current: topology);
        board.Refresh();

        Assert.IsType<SystemNodeViewModel>(board.Root);
        Assert.Equal(Twins.Id(topology, 1), board.CurrentCabinet!.Id);
        Assert.True(board.CurrentCabinet.IsExpanded);
        Assert.False(board.HasSelection);
        Assert.Null(board.SelectedAddressText);
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
        Assert.Equal(ProductState.Idle, cabinets[0].Worst);

        // 停用的柜体也能切过去查看；透明度逐层相乘：停用柜体下的节点不再各自变淡，否则 0.4⁵ 几乎看不见
        board.ShowCabinet(cabinets[1].Id);
        Assert.Same(cabinets[1], board.CurrentCabinet);
        Assert.NotEmpty(Flatten(cabinets[1]).Skip(1));
        Assert.All(Flatten(cabinets[1]).Skip(1), node =>
        {
            Assert.True(node.IsDisabled);
            Assert.Equal(1.0, node.ContentOpacity);
        });
    }
}
