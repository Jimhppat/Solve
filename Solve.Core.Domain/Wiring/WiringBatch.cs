using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Domain.Wiring;

/// <summary>
/// 批量接线的一段通道来源：一个模块上的一组通道
/// </summary>
public sealed record ChannelSource(DeviceId Device, ChannelGroup Group);

/// <summary>
/// 批量接线请求：把 <see cref="Root"/> 下面 <see cref="TargetLevel"/> 层的全部位置（按位置编码顺序），
/// 依次接到通道来源的连续通道上：第一个来源从 <see cref="StartIndex"/> 起，用完后续接下一个来源的第 1 路。
/// </summary>
/// <remarks>
/// 典型用法："把 C01 柜的 48 个产品位，从采集卡-01 的第 1 路起依次接上，不够时续接采集卡-02"。
/// 停用的位置同样接线：停用只是维护屏蔽，现场的线仍在。
/// </remarks>
/// <param name="TargetLevel">要接线的层级；与 <see cref="Root"/> 同层时只接它自己</param>
/// <param name="Sources">通道来源，按续接顺序排列，各组功能必须相同</param>
/// <param name="StartIndex">第一个来源的起始通道号</param>
public sealed record WiringBatchRequest(
    TopologyNodeId Root,
    TopologyLevel TargetLevel,
    IReadOnlyList<ChannelSource> Sources,
    int StartIndex)
{
    /// <summary>
    /// 这批接线共用的用途，未填为 null
    /// </summary>
    public string? Purpose { get; init; }
}

/// <summary>
/// 把批量请求展开成逐条接线（纯计算，不访问存储）。通道占用与路径冲突由 <see cref="WiringMap.WithAdded"/> 校验。
/// </summary>
public static class WiringBatch
{
    public static Result<IReadOnlyList<Wire>> Expand(EquipmentTopology topology, WiringBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(request);

        if (!topology.TryGetNode(request.Root, out var root))
            return Failure(WiringErrors.NodeNotFound());
        if (!request.TargetLevel.IsDefined() || request.TargetLevel == TopologyLevel.System || request.TargetLevel < root.Level)
            return Failure(WiringErrors.InvalidField("TargetLevel", "接线层级必须是所选位置本身或它下面的层级，且不能是系统根。"));
        if (request.Sources is not { Count: > 0 } sources)
            return Failure(WiringErrors.InvalidField("Sources", "请选择要接线的模块与通道。"));

        var kind = sources[0].Group.Kind;
        if (sources.Any(s => s.Group.Kind != kind))
            return Failure(WiringErrors.KindMismatch("续接的模块通道功能不一致，不能接成同一批。"));
        if (!sources[0].Group.Contains(request.StartIndex))
            return Failure(WiringErrors.InvalidField("StartIndex",
                $"起始通道必须在 [1, {sources[0].Group.Count}] 之间，当前为 {request.StartIndex}。"));

        var targets = topology.GetDescendants(root.Address, request.TargetLevel);
        if (targets.Count == 0)
            return Failure(WiringErrors.NoTargets());

        var channels = Channels(sources, request.StartIndex, targets.Count);
        if (channels.Count < targets.Count)
            return Failure(WiringErrors.NotEnoughChannels(targets.Count, channels.Count));

        var purpose = Wire.NormalizeText(request.Purpose);
        var wires = new Wire[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            wires[i] = new Wire(WireId.New(), targets[i].Id, channels[i], kind) { Purpose = purpose };

        return Result<IReadOnlyList<Wire>>.Success(wires);
    }

    /// <summary>
    /// 按续接顺序列出通道，最多 <paramref name="needed"/> 路
    /// </summary>
    private static List<ChannelRef> Channels(IReadOnlyList<ChannelSource> sources, int startIndex, int needed)
    {
        var channels = new List<ChannelRef>(needed);
        for (var s = 0; s < sources.Count && channels.Count < needed; s++)
        {
            var source = sources[s];
            for (var index = s == 0 ? startIndex : 1; index <= source.Group.Count && channels.Count < needed; index++)
                channels.Add(new ChannelRef(source.Device, source.Group.Key, index));
        }

        return channels;
    }

    private static Result<IReadOnlyList<Wire>> Failure(Error error) => Result<IReadOnlyList<Wire>>.Failure(error);
}
