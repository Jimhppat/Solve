using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// SDK 通道功能 → 内核通道功能的唯一映射处。SDK 追加功能时必须同时在这里与 <see cref="ChannelKind"/> 中追加，
/// 架构测试会检查 SDK 的每个功能都有映射，防止驱动包因"通道功能未定义"整体载入失败。
/// </summary>
public static class ChannelKindMap
{
    public static bool TryMap(ChannelFunction function, out ChannelKind mapped)
    {
        ChannelKind? result = function switch
        {
            ChannelFunction.PowerSupply => ChannelKind.PowerSupply,
            ChannelFunction.Load => ChannelKind.Load,
            ChannelFunction.Measure => ChannelKind.Measure,
            ChannelFunction.Switch => ChannelKind.Switch,
            ChannelFunction.Detect => ChannelKind.Detect,
            ChannelFunction.Communication => ChannelKind.Communication,
            _ => null,
        };

        mapped = result ?? default;
        return result is not null;
    }
}
