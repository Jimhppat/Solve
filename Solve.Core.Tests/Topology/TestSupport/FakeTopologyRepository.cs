using Solve.Core.Application.Topology.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology.TestSupport;

/// <summary>
/// 内存拓扑仓储：按 <see cref="NextLoadError"/>、<see cref="NextSaveError"/> 模拟失败，成功保存时更新 <see cref="Stored"/>
/// </summary>
internal sealed class FakeTopologyRepository : ITopologyRepository
{
    public EquipmentTopology? Stored { get; set; }

    public Error? NextLoadError { get; set; }

    public Error? NextSaveError { get; set; }

    public List<EquipmentTopology> Saves { get; } = [];

    public Task<Result<EquipmentTopology?>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NextLoadError is { } error
            ? Result<EquipmentTopology?>.Failure(error)
            : Result<EquipmentTopology?>.Success(Stored));

    public Task<Result> SaveAsync(EquipmentTopology topology, CancellationToken cancellationToken)
    {
        if (NextSaveError is { } error)
            return Task.FromResult(Result.Failure(error));

        Saves.Add(topology);
        Stored = topology;
        return Task.FromResult(Result.Success());
    }

    public static EquipmentTopology Layout(int c, int l, int s, int f, int p, string name = "老化房")
        => EquipmentTopology.Create(new TopologyLayout(c, l, s, f, p).ToDefinition(name).Value).Value;

    public static readonly Error CorruptError = new(TopologyErrors.StorageCorruptCode, "库内拓扑已损坏，拒绝加载：测试");
}
