using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Domain.Devices;

/// <summary>
/// 总线挂载设备的错误码。Code 一经发布即视为稳定接口（日志检索、告警映射、UI 字段高亮依赖它）。
/// </summary>
public static class DeviceErrors
{
    /// <summary>
    /// 配置字段错误的 Code 前缀，完整 Code 形如 Device.Descriptor.Address
    /// </summary>
    public const string DescriptorCodePrefix = "Device.Descriptor.";

    public const string InvalidDescriptorCode = "Device.InvalidDescriptor";
    public const string AddressConflictCode = "Device.AddressConflict";
    public const string BusNotFoundCode = "Device.BusNotFound";
    public const string StorageFailedCode = "Device.Storage.Failed";
    public const string StorageNotFoundCode = "Device.Storage.NotFound";
    public const string StorageCorruptRowCode = "Device.Storage.CorruptRow";
    public const string UnknownDriverCode = "Device.UnknownDriver";

    public static Error InvalidField(string field, string message)
        => new(DescriptorCodePrefix + field, message);

    public static Error InvalidDescriptor(IReadOnlyList<Error> errors)
        => new(InvalidDescriptorCode, string.Join("；", errors.Select(e => e.Message)));

    public static Error AddressConflict(int address)
        => new(AddressConflictCode, $"地址 {address} 已被这条总线上的其他模块使用。");

    /// <summary>
    /// 批量添加时列出全部被占用的地址，便于一次改好起始地址或数量
    /// </summary>
    public static Error AddressConflict(IReadOnlyCollection<int> addresses)
        => new(AddressConflictCode, $"地址 {string.Join("、", addresses)} 已被这条总线上的其他模块使用。");

    /// <summary>
    /// 并发写入时由唯一索引拦下，此时无法得知具体是哪个地址
    /// </summary>
    public static Error AddressConflict()
        => new(AddressConflictCode, "部分地址已被这条总线上的其他模块使用，请刷新后重试。");

    public static Error UnknownDriver(DriverId driver)
        => new(UnknownDriverCode, $"找不到驱动「{driver}」，可能已从程序中移除。");

    // 以下两条会显示在界面上，消息里不带系统 Id；Id 由调用方写进日志
    public static Error BusNotFound() => new(BusNotFoundCode, "所属总线已不存在，可能已被删除。");

    public static Error StorageFailed(string detail) => new(StorageFailedCode, $"模块配置存储失败：{detail}");

    public static Error StorageNotFound() => new(StorageNotFoundCode, "该模块已不存在，可能已被删除。");

    public static Error StorageCorruptRow(string field, string detail)
        => new(StorageCorruptRowCode, $"模块配置行字段 {field} 无法解析：{detail}");
}
