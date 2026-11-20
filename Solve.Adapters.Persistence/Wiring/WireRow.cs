using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.Adapters.Persistence.Wiring;

/// <summary>
/// topology_wires 表的一行，只使用 SQLite 原生类型（INTEGER → long，TEXT → string）。
/// </summary>
internal sealed class WireRow
{
    public string Id { get; set; } = "";

    public string NodeId { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public string ChannelGroup { get; set; } = "";

    public long ChannelIndex { get; set; }

    public long Kind { get; set; }

    public string? Purpose { get; set; }

    public string? Label { get; set; }

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// 领域接线 ⇄ 数据库行的显式转换。读取时把库内数据视为不可信输入：解析错误与领域校验错误一并收集。
/// </summary>
internal static class WireRowMapper
{
    public static WireRow ToRow(Wire wire, DateTimeOffset now)
    {
        var timestamp = now.UtcDateTime.ToString("O");
        return new WireRow
        {
            Id = wire.Id.ToString(),
            NodeId = wire.Node.ToString(),
            DeviceId = wire.Channel.Device.ToString(),
            ChannelGroup = wire.Channel.Group,
            ChannelIndex = wire.Channel.Index,
            Kind = (long)wire.Kind,
            Purpose = Wire.NormalizeText(wire.Purpose),
            Label = Wire.NormalizeText(wire.Label),
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    public static Result<Wire> ToWire(WireRow row, out IReadOnlyList<Error> problems)
    {
        var errors = new List<Error>();

        if (!WireId.TryParse(row.Id, out var id))
            errors.Add(WiringErrors.StorageCorruptRow("id", $"'{row.Id}' 不是有效的 GUID"));
        if (!TopologyNodeId.TryParse(row.NodeId, out var node) || node.IsEmpty)
            errors.Add(WiringErrors.StorageCorruptRow("node_id", $"'{row.NodeId}' 不是有效的 GUID"));
        if (!Core.Domain.Devices.DeviceId.TryParse(row.DeviceId, out var device))
            errors.Add(WiringErrors.StorageCorruptRow("device_id", $"'{row.DeviceId}' 不是有效的 GUID"));

        // 先确认在 int 范围内再收窄，避免手工改库写入的超大值截断后恰好落进合法区间
        var index = 0;
        if (row.ChannelIndex is < 1 or > int.MaxValue)
            errors.Add(WiringErrors.StorageCorruptRow("channel_index", $"{row.ChannelIndex} 超出范围"));
        else
            index = (int)row.ChannelIndex;

        var kind = default(ChannelKind);
        if (row.Kind is < int.MinValue or > int.MaxValue || !((ChannelKind)(int)row.Kind).IsDefined())
            errors.Add(WiringErrors.StorageCorruptRow("kind", $"{row.Kind} 不是已定义的通道功能"));
        else
            kind = (ChannelKind)(int)row.Kind;

        var wire = new Wire(id, node, new ChannelRef(device, row.ChannelGroup ?? string.Empty, index), kind)
        {
            Purpose = row.Purpose,
            Label = row.Label,
        };

        if (errors.Count == 0)
            errors.AddRange(wire.Validate());

        problems = errors;
        return errors.Count == 0 ? Result<Wire>.Success(wire) : Result<Wire>.Failure(WiringErrors.InvalidWire(errors));
    }
}
