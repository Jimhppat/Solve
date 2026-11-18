using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Twin.TestSupport;

/// <summary>
/// 只提供当前拓扑的拓扑服务替身：测试直接替换 <see cref="Current"/> 模拟拓扑被修改
/// </summary>
internal sealed class FixedTopologyService(EquipmentTopology? current = null) : ITopologyService
{
    public EquipmentTopology? Current { get; set; } = current;

    public Error? LoadError => null;

    public Task<Result> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Result<TopologyChangePlan> PlanLayout(string systemName, TopologyLayout layout) => throw new NotSupportedException();

    public Result<TopologyChangePlan> PlanNodeSettings(TopologyNodeId id, string? name, bool enabled) => throw new NotSupportedException();

    public Task<Result> SaveAsync(TopologyChangePlan plan, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal static class Twins
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    public static EquipmentTopology Layout(int c, int l, int s, int f, int p, EquipmentTopology? current = null)
        => EquipmentTopology.Create(new TopologyLayout(c, l, s, f, p).ToDefinition("老化房", current).Value).Value;

    public static TwinStateStore Store(ITopologyService topology, TimeProvider? time = null)
        => new(topology, NullLogger<TwinStateStore>.Instance, time, TimeSpan.FromSeconds(10));

    public static TopologyNodeId Id(EquipmentTopology topology, params int[] ordinals)
        => topology.GetNode(TopologyAddress.Of(ordinals)).Value.Id;
}
