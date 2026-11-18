using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;
using Solve.Core.Tests.Twin.TestSupport;

namespace Solve.Core.Tests.Twin;

/// <summary>
/// 孪生状态库：初始无数据、逐级汇总、停用单独计数、变化标记、过期退回、拓扑切换保留状态、并发一致性
/// </summary>
public sealed class TwinStateStoreTests
{
    private readonly ManualTimeProvider _time = new(Twins.T0);

    // 2 柜 × 1 层 × 1 槽 × 2 治具位 × 3 产品位 = 12 个产品位
    private readonly EquipmentTopology _topology = Twins.Layout(2, 1, 1, 2, 3);

    private TwinStateStore Store(EquipmentTopology? topology = null)
        => Twins.Store(new FixedTopologyService(topology ?? _topology), _time);

    [Fact]
    public void NoTopology_IgnoresEverything()
    {
        var store = Twins.Store(new FixedTopologyService(), _time);

        Assert.Null(store.Topology);
        Assert.Empty(store.ProductIds);
        Assert.False(store.Report(TopologyNodeId.New(), ProductState.Running));
        Assert.Same(TwinStatusSummary.None, store.GetSummary(TopologyNodeId.New()));
        Assert.Empty(store.DrainChanges());
        Assert.Equal(0, store.ExpireStale());
    }

    [Fact]
    public void Initially_EveryProductIsUnknown()
    {
        var store = Store();

        Assert.Equal(12, store.ProductIds.Count);
        var root = store.GetSummary(_topology.Root.Id);
        Assert.Equal(12, root[ProductState.Unknown]);
        Assert.Equal(12, root.Active);
        Assert.Equal(ProductState.Unknown, root.Worst);
        Assert.True(store.TryGetProduct(store.ProductIds[0], out var product));
        Assert.Equal(ProductState.Unknown, product.State);
        Assert.Null(product.UpdatedAt);
        Assert.Empty(store.DrainChanges());
    }

    [Fact]
    public void Report_UpdatesProduct_AndEveryAncestor()
    {
        var store = Store();
        var product = Twins.Id(_topology, 2, 1, 1, 2, 3);

        Assert.True(store.Report(product, ProductState.Fault, "SN-1"));

        Assert.True(store.TryGetProduct(product, out var snapshot));
        Assert.Equal((ProductState.Fault, "SN-1", Twins.T0), (snapshot.State, snapshot.SerialNumber, snapshot.UpdatedAt!.Value));
        foreach (var ancestor in new[] { _topology.Root.Id, Twins.Id(_topology, 2), Twins.Id(_topology, 2, 1, 1, 2) })
        {
            var summary = store.GetSummary(ancestor);
            Assert.Equal(1, summary[ProductState.Fault]);
            Assert.Equal(ProductState.Fault, summary.Worst);
        }

        Assert.Equal(0, store.GetSummary(Twins.Id(_topology, 1))[ProductState.Fault]);
        Assert.Equal(11, store.GetSummary(_topology.Root.Id)[ProductState.Unknown]);
    }

    [Fact]
    public void Report_UnknownId_OrContainer_IsIgnored()
    {
        var store = Store();

        Assert.False(store.Report(TopologyNodeId.New(), ProductState.Running));
        Assert.False(store.Report(Twins.Id(_topology, 1), ProductState.Running));
        Assert.Empty(store.DrainChanges());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Report(store.ProductIds[0], (ProductState)42));
    }

    [Fact]
    public void DrainChanges_ReturnsProductAndAncestors_Once()
    {
        var store = Store();
        var product = Twins.Id(_topology, 1, 1, 1, 1, 1);
        store.Report(product, ProductState.Running);

        var changed = store.DrainChanges();

        // 产品位、治具位、槽位、层位、柜体、系统
        Assert.Equal(6, changed.Count);
        Assert.Contains(product, changed);
        Assert.Contains(_topology.Root.Id, changed);
        Assert.Empty(store.DrainChanges());

        // 心跳（状态与 SN 都没变）只刷新时间，不产生变化
        store.Report(product, ProductState.Running);
        Assert.Empty(store.DrainChanges());
    }

    [Fact]
    public void DisabledProducts_AreCountedSeparately()
    {
        var fixture = Twins.Id(_topology, 1, 1, 1, 1);
        var masked = _topology.WithNodeSettings(fixture, null, enabled: false).Value;
        var store = Store(masked);

        var root = store.GetSummary(masked.Root.Id);
        Assert.Equal(3, root.Disabled);
        Assert.Equal(9, root.Active);

        // 停用的产品位仍记录状态（便于核对），但不计入状态统计
        var product = Twins.Id(masked, 1, 1, 1, 1, 1);
        Assert.True(store.Report(product, ProductState.Fault));
        Assert.Equal(0, store.GetSummary(masked.Root.Id)[ProductState.Fault]);
        Assert.True(store.TryGetProduct(product, out var snapshot));
        Assert.False(snapshot.IsEnabled);
        Assert.Null(store.GetSummary(fixture).Worst);
    }

    [Fact]
    public void ExpireStale_ReturnsSilentProductsToUnknown_KeepingSerial()
    {
        var store = Store();
        var quiet = Twins.Id(_topology, 1, 1, 1, 1, 1);
        var chatty = Twins.Id(_topology, 1, 1, 1, 1, 2);
        store.Report(quiet, ProductState.Passed, "SN-Q");
        store.Report(chatty, ProductState.Running);
        store.DrainChanges();

        _time.Advance(TimeSpan.FromSeconds(8));
        store.Report(chatty, ProductState.Running); // 心跳
        Assert.Equal(0, store.ExpireStale());

        _time.Advance(TimeSpan.FromSeconds(3)); // quiet 已 11 s 未上报，chatty 只有 3 s
        Assert.Equal(1, store.ExpireStale());

        Assert.True(store.TryGetProduct(quiet, out var expired));
        Assert.Equal((ProductState.Unknown, "SN-Q"), (expired.State, expired.SerialNumber));
        Assert.True(store.TryGetProduct(chatty, out var alive));
        Assert.Equal(ProductState.Running, alive.State);
        Assert.Contains(quiet, store.DrainChanges());
        Assert.Equal(0, store.GetSummary(_topology.Root.Id)[ProductState.Passed]);
    }

    [Fact]
    public void TopologyChange_KeepsStatesById_AndDropsRemovedPositions()
    {
        var service = new FixedTopologyService(_topology);
        var store = Twins.Store(service, _time);
        var kept = Twins.Id(_topology, 1, 1, 1, 1, 1);
        var removed = Twins.Id(_topology, 2, 1, 1, 1, 1);
        store.Report(kept, ProductState.Fault, "SN-K");
        store.Report(removed, ProductState.Alarm);

        // 缩容为 1 柜，并把 1 号柜改编号为 3 号：位置编码变了，Id 不变
        var shrunk = Twins.Layout(1, 1, 1, 2, 3, current: _topology);
        var exported = shrunk.ToDefinition();
        var renumbered = EquipmentTopology.Create(exported with { Cabinets = [exported.Cabinets[0] with { Ordinal = 3 }] }).Value;
        service.Current = renumbered;

        Assert.Same(renumbered, store.Topology);
        Assert.Equal(6, store.ProductIds.Count);
        Assert.True(store.TryGetProduct(kept, out var snapshot));
        Assert.Equal((ProductState.Fault, "SN-K"), (snapshot.State, snapshot.SerialNumber));
        Assert.Equal(1, store.GetSummary(renumbered.Root.Id)[ProductState.Fault]);
        Assert.Equal(0, store.GetSummary(renumbered.Root.Id)[ProductState.Alarm]);
        Assert.False(store.Report(removed, ProductState.Running));
    }

    [Fact]
    public void ConcurrentReports_KeepCountsConsistent()
    {
        var topology = Twins.Layout(4, 4, 4, 2, 8); // 1,024 个产品位
        var store = Store(topology);
        var products = store.ProductIds;

        Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var random = new Random(i);
            store.Report(products[random.Next(products.Count)], ProductStates.BySeverity[random.Next(ProductStates.Count)]);
        });

        var expected = new int[ProductStates.Count];
        foreach (var id in products)
        {
            Assert.True(store.TryGetProduct(id, out var snapshot));
            expected[(int)snapshot.State]++;
        }

        var root = store.GetSummary(topology.Root.Id);
        Assert.Equal(products.Count, root.Active);
        foreach (var state in ProductStates.BySeverity)
            Assert.Equal(expected[(int)state], root[state]);
    }
}
