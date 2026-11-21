using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 接线表单的模块选项。下拉框直接显示 <see cref="ToString"/>
/// </summary>
public sealed class WiringModuleOption(WiringModule module)
{
    public WiringModule Module { get; } = module;

    public override string ToString() => $"{Module.BusName} · {Module.Device.Name}（地址 {Module.Device.Address}）";
}

/// <summary>
/// 接线表单的通道组选项，如"输出（供电，1 路）"
/// </summary>
public sealed class WiringGroupOption(ChannelGroup group)
{
    public ChannelGroup Group { get; } = group;

    public override string ToString() => Group.EnabledCount == Group.Count
        ? $"{Group.Name}（{Group.Kind.DisplayName()}，{Group.Count} 路）"
        : $"{Group.Name}（{Group.Kind.DisplayName()}，启用 {Group.EnabledCount}/{Group.Count} 路）";
}

/// <summary>
/// 批量接线的层级选项：所选位置本身，或它下面的某一层（带位置数）
/// </summary>
public sealed class WiringLevelOption(TopologyLevel level, int count, bool isSelf)
{
    public TopologyLevel Level { get; } = level;

    public int Count { get; } = count;

    public override string ToString() => isSelf ? "本位置" : $"全部{Level.DisplayName()}（{Count} 个）";
}
