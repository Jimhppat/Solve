using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Twin;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Twin;
using Solve.Core.Tests.Twin.TestSupport;

namespace Solve.Core.Tests.Twin;

/// <summary>
/// 仿真产品状态源：心跳覆盖全部产品位、状态会推进、后台循环可启停
/// </summary>
public sealed class SimulatedProductStatusFeedTests
{
    private static SimulatedProductStatusFeed Feed(TwinStateStore store, TimeSpan? interval = null)
        => new(store, NullLogger<SimulatedProductStatusFeed>.Instance, interval, seed: 42);

    [Fact]
    public async Task Heartbeat_ReachesEveryProduct_WithinHeartbeatRounds()
    {
        var store = Twins.Store(new FixedTopologyService(Twins.Layout(2, 2, 2, 2, 5)));
        await using var feed = Feed(store);

        for (var i = 0; i < SimulatedProductStatusFeed.HeartbeatRounds; i++)
            feed.Tick();

        Assert.All(store.ProductIds, id =>
        {
            Assert.True(store.TryGetProduct(id, out var snapshot));
            Assert.NotNull(snapshot.UpdatedAt);
        });
    }

    [Fact]
    public async Task Ticks_AdvanceStates_ThroughTheTestCycle()
    {
        var topology = Twins.Layout(2, 2, 2, 2, 8);
        var store = Twins.Store(new FixedTopologyService(topology));
        await using var feed = Feed(store);

        for (var i = 0; i < 400; i++)
            feed.Tick();

        var summary = store.GetSummary(topology.Root.Id);
        Assert.True(summary[ProductState.Standby] > 0);
        Assert.True(summary[ProductState.Good] > 0);
        Assert.True(summary[ProductState.Defective] > 0);

        var withSerial = store.ProductIds.Count(id => store.TryGetProduct(id, out var p) && p.State == ProductState.Standby && p.SerialNumber is not null);
        Assert.True(withSerial > 0);
    }

    [Fact]
    public async Task Tick_WithoutTopology_DoesNothing()
    {
        var store = Twins.Store(new FixedTopologyService());
        await using var feed = Feed(store);

        feed.Tick();

        Assert.Empty(store.ProductIds);
    }

    [Fact]
    public async Task BackgroundLoop_StartsAndStops()
    {
        var store = Twins.Store(new FixedTopologyService(Twins.Layout(1, 1, 1, 1, 4)));
        var feed = Feed(store, TimeSpan.FromMilliseconds(10));

        feed.Start();
        feed.Start(); // 幂等
        var deadline = DateTime.UtcNow.AddSeconds(5);
        // 后台循环跑过：每个产品位都至少收到过一次上报（心跳），更新时间不再为空
        while (store.ProductIds.Any(id => store.TryGetProduct(id, out var p) && p.UpdatedAt is null)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await feed.DisposeAsync();
        await feed.DisposeAsync(); // 幂等

        Assert.All(store.ProductIds, id => Assert.True(store.TryGetProduct(id, out var p) && p.UpdatedAt is not null));
        Assert.Throws<ObjectDisposedException>(feed.Start);
    }
}
