using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;
using Solve.Core.Tests.Twin.TestSupport;
using Solve.UI.ViewModels.Twin;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 主控台显示区：焦点与显示数量上限、逐层进入与返回、选中详情、按变化刷新、拓扑修改后重建
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
        Assert.Null(board.Focus);
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

        var system = Assert.IsType<SystemNodeViewModel>(board.Focus);
        Assert.Equal(25, board.VisibleCount);
        var nodes = Flatten(system).ToList();
        Assert.Equal(16, nodes.OfType<ProductNodeViewModel>().Count());
        Assert.All(nodes.Where(n => n is not ProductNodeViewModel), n => Assert.True(n.IsExpanded));
        Assert.Equal(["系统 老化房"], board.Breadcrumb.Select(b => b.Text));
        Assert.True(Assert.Single(board.Breadcrumb).IsCurrent);
        Assert.False(board.FocusParentCommand.CanExecute(null));

        // 等分网格的行列数：柜体 1 个一行；每治具 4 个产品位，一行放满 4 个
        Assert.Equal(1, system.ChildCount);
        Assert.All(nodes.OfType<FixtureNodeViewModel>(), fixture => Assert.Equal(4, fixture.ProductColumns));
    }

    [Fact]
    public void FixtureWithFewProducts_UsesAllColumnsForThem()
    {
        var (board, _) = Attached(Twins.Layout(1, 1, 1, 1, 2));

        var fixture = Flatten(board.Focus!).OfType<FixtureNodeViewModel>().Single();

        // 只有 2 个产品位时一行 2 列，格子铺满整行，不留两个空位
        Assert.Equal(2, fixture.ProductColumns);
    }

    [Fact]
    public void LargeTopology_StopsAtBudget_DeepestLevelInSummaryMode()
    {
        // 逐层：柜 2、层 4、槽 8、治具位 16、产品位 64；上限 20 时画到槽位（1 + 2 + 4 + 8 = 15）
        var (board, _) = Attached(Twins.Layout(2, 2, 2, 2, 4), budget: 20);

        Assert.Equal(15, board.VisibleCount);
        var nodes = Flatten(board.Focus!).ToList();
        var slots = nodes.OfType<SlotNodeViewModel>().ToList();
        Assert.Equal(8, slots.Count);
        Assert.All(slots, slot => Assert.True(slot.IsSummary));
        Assert.All(nodes.OfType<LayerNodeViewModel>(), layer => Assert.True(layer.IsExpanded));
        Assert.Empty(nodes.OfType<FixtureNodeViewModel>());
    }

    [Fact]
    public void TinyBudget_StillShowsTheDirectChildren()
    {
        var (board, _) = Attached(Twins.Layout(3, 2, 2, 2, 4), budget: 1);

        var cabinets = Flatten(board.Focus!).OfType<CabinetNodeViewModel>().ToList();
        Assert.Equal(3, cabinets.Count);
        Assert.All(cabinets, cabinet => Assert.True(cabinet.IsSummary));
    }

    [Fact]
    public void ActivatingContainer_DrillsDown_AndParentCommandGoesBack()
    {
        var topology = Twins.Layout(2, 2, 2, 2, 4);
        var (board, _) = Attached(topology, budget: 20);
        var layer = Flatten(board.Focus!).OfType<LayerNodeViewModel>().First(l => l.Node.Address == TopologyAddress.Of(2, 1));

        layer.Activate();

        Assert.IsType<LayerNodeViewModel>(board.Focus);
        Assert.Equal(TopologyAddress.Of(2, 1), board.Focus!.Node.Address);
        Assert.Equal(["系统 老化房", "C02", "C02-L01"], board.Breadcrumb.Select(b => b.Text));
        Assert.Equal([false, false, true], board.Breadcrumb.Select(b => b.IsCurrent));
        Assert.Equal("层位 C02-L01", board.SelectedTitle);

        // 层位下 1 + 2 槽 + 4 治具位 = 7，再加 16 个产品位超过上限 20：治具位为摘要模式
        Assert.Empty(Flatten(board.Focus).OfType<ProductNodeViewModel>());
        Assert.All(Flatten(board.Focus).OfType<FixtureNodeViewModel>(), fixture => Assert.True(fixture.IsSummary));

        // 再进入槽位：1 + 2 + 8 = 11，画到产品位
        Flatten(board.Focus).OfType<SlotNodeViewModel>().First().Activate();
        Assert.Equal(8, Flatten(board.Focus!).OfType<ProductNodeViewModel>().Count());
        board.FocusParentCommand.Execute(null);

        board.FocusParentCommand.Execute(null);
        Assert.Equal(TopologyAddress.Of(2), board.Focus!.Node.Address);

        board.Breadcrumb[0].Command.Execute(null);
        Assert.IsType<SystemNodeViewModel>(board.Focus);
    }

    [Fact]
    public void ActivatingProduct_SelectsWithoutMovingFocus()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 4);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Focus!).OfType<ProductNodeViewModel>().Last();
        store.Report(product.Id, ProductState.Running, "SN-9");
        board.Refresh();

        product.Activate();

        Assert.IsType<SystemNodeViewModel>(board.Focus);
        Assert.True(product.IsSelected);
        Assert.True(board.IsProductSelected);
        Assert.Equal("产品位 C01-L01-S01-F01-P04", board.SelectedTitle);
        Assert.Equal("测试中", board.SelectedStateText);
        Assert.Equal("SN-9", board.SelectedSerialText);
        Assert.Equal(product.Id.ToString(), board.SelectedIdText);
        Assert.NotEqual("从未收到", board.SelectedUpdatedText);
    }

    [Fact]
    public void Refresh_UpdatesChangedProducts_AndTheirContainers()
    {
        var topology = Twins.Layout(1, 1, 2, 1, 4);
        var (board, store) = Attached(topology);
        var nodes = Flatten(board.Focus!).ToList();
        var product = nodes.OfType<ProductNodeViewModel>().First();

        store.Report(product.Id, ProductState.Fault);
        board.Refresh();

        Assert.Equal(ProductState.Fault, product.State);
        var slot = nodes.OfType<SlotNodeViewModel>().First();
        Assert.Equal(ProductState.Fault, slot.Worst);
        Assert.Contains("故障 1", slot.AlarmText);
        Assert.Equal(ProductState.Fault, board.Focus!.Worst);

        // 另一个槽位没有变化
        Assert.Equal(ProductState.Unknown, nodes.OfType<SlotNodeViewModel>().Last().Worst);
    }

    [Fact]
    public void Refresh_ExpiresSilentProducts()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var product = Flatten(board.Focus!).OfType<ProductNodeViewModel>().First();
        store.Report(product.Id, ProductState.Passed);
        board.Refresh();
        Assert.Equal(ProductState.Passed, product.State);

        _time.Advance(TimeSpan.FromSeconds(11));
        for (var i = 0; i < 4; i++) // 过期检查每 4 次刷新做一次
            board.Refresh();

        Assert.Equal(ProductState.Unknown, product.State);
    }

    [Fact]
    public void TopologyChange_RebuildsAndKeepsFocusAndSelectionById()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, store) = Attached(topology);
        var cabinet = Flatten(board.Focus!).OfType<CabinetNodeViewModel>().Last();
        cabinet.Activate();
        var focusId = board.Focus!.Id;

        // 改名：拓扑被替换，节点 Id 不变
        _topology.Current = topology.WithNodeSettings(focusId, "B 柜", enabled: true).Value;
        board.Refresh();

        Assert.Equal(focusId, board.Focus!.Id);
        Assert.Equal("B 柜", board.Focus.Name);
        Assert.True(board.Focus.IsSelected);
        Assert.Equal("B 柜", board.SelectedNameText);
        Assert.Equal(["系统 老化房", "C02 B 柜"], board.Breadcrumb.Select(b => b.Text));
        Assert.Same(_topology.Current, store.Topology);
    }

    [Fact]
    public void FocusedNodeRemoved_FallsBackToRoot()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var (board, _) = Attached(topology);
        Flatten(board.Focus!).OfType<CabinetNodeViewModel>().Last().Activate();

        _topology.Current = Twins.Layout(1, 1, 1, 1, 2, current: topology);
        board.Refresh();

        Assert.IsType<SystemNodeViewModel>(board.Focus);
        Assert.False(board.HasSelection);
    }

    [Fact]
    public void FocusOnProduct_UsesItsFixture()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 2);
        var (board, _) = Attached(topology);

        board.FocusOn(Twins.Id(topology, 1, 1, 1, 1, 2));

        Assert.IsType<FixtureNodeViewModel>(board.Focus);
    }

    [Fact]
    public void DisabledSubtree_IsDimmed_AndExcludedFromColors()
    {
        var topology = Twins.Layout(2, 1, 1, 1, 2);
        var masked = topology.WithNodeSettings(Twins.Id(topology, 2), null, enabled: false).Value;
        var (board, _) = Attached(masked);

        var cabinets = Flatten(board.Focus!).OfType<CabinetNodeViewModel>().ToList();
        Assert.True(cabinets[1].IsDisabled);
        Assert.Equal(0.4, cabinets[1].ContentOpacity);
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

    [Fact]
    public void FocusInsideDisabledSubtree_DimsTheFocusItself()
    {
        var topology = Twins.Layout(1, 1, 1, 1, 2);
        var masked = topology.WithNodeSettings(Twins.Id(topology, 1), null, enabled: false).Value;
        var (board, _) = Attached(masked);

        // 进入停用柜体下的层位：上级柜体不在画面上，层位自己承担变淡
        board.FocusOn(Twins.Id(masked, 1, 1));

        Assert.True(board.Focus!.IsFocusRoot);
        Assert.Equal(TwinNodeViewModel.DisabledOpacity, board.Focus.ContentOpacity);
        Assert.All(Flatten(board.Focus).Skip(1), node => Assert.Equal(1.0, node.ContentOpacity));
    }
}
