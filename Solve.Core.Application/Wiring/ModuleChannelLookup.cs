using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Wiring;

namespace Solve.Core.Application.Wiring;

/// <summary>
/// 一台模块实际可用的通道：驱动目录里的通道能力 + 模块自己的通道配置。接线服务、运行视图与模块服务共用这一处解析。
/// </summary>
internal static class ModuleChannelLookup
{
    /// <summary>
    /// 解析模块的通道组。驱动未加载、或通道配置与驱动不符（如驱动升级后减少了通道）时失败。
    /// </summary>
    public static Result<IReadOnlyList<ChannelGroup>> Resolve(IDriverCatalog catalog, DeviceDescriptor device)
    {
        if (!catalog.TryGet(device.Driver, out var driver))
        {
            return Result<IReadOnlyList<ChannelGroup>>.Failure(WiringErrors.ChannelNotDeclared(
                $"{device.Name}的驱动「{device.Driver}」未加载，无法确认它有哪些通道。"));
        }

        var resolved = ModuleChannels.Resolve(driver, device.Channels);
        return resolved.IsSuccess
            ? resolved
            : Result<IReadOnlyList<ChannelGroup>>.Failure(WiringErrors.ChannelNotDeclared(
                $"{device.Name}的通道配置与驱动「{driver.Name}」不符：{resolved.Error.Message}"));
    }

    public static ChannelGroup? Find(IReadOnlyList<ChannelGroup> groups, string? key)
        => groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));
}
