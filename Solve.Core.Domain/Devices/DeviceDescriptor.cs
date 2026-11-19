using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Devices;

/// <summary>
/// 挂载在一条总线上的设备（配置，不可变）。只描述"哪条总线上、哪个地址有一台什么设备"，
/// 不包含任何协议、寄存器或点位信息；这些在协议层确定后再扩展。
/// </summary>
/// <remarks>
/// <para>描述对象来自 UI 输入或数据库，属于不可信数据：保存前必须调用 <see cref="Validate"/>，返回非空列表时拒绝保存。</para>
/// <para>同一条总线上地址唯一（RS-485 多站点按地址区分设备），该约束涉及其他设备，由仓储的唯一索引保证。</para>
/// </remarks>
/// <param name="BusId">所属总线。设备不在总线之间移动：换总线等于删除后在另一条总线上新增。</param>
/// <param name="Address">设备在总线上的地址（站号）</param>
public sealed record DeviceDescriptor(DeviceId Id, BusId BusId, string Name, int Address)
{
    public const int MaxNameLength = 64;
    public const int MaxRemarkLength = 200;

    /// <summary>
    /// 地址下限。0 在 Modbus 等常见现场总线协议中是广播地址，广播没有应答，不能当作一台设备的地址。
    /// </summary>
    public const int MinAddress = 1;

    /// <summary>
    /// 地址上限（单字节地址）。协议确定后按协议收紧，如 Modbus RTU 为 247。
    /// </summary>
    public const int MaxAddress = 255;

    /// <summary>
    /// 备注（如设备型号、安装位置说明），未填为 null
    /// </summary>
    public string? Remark { get; init; }

    /// <summary>
    /// 校验全部字段并一次性返回所有错误。空列表表示有效。
    /// </summary>
    public IReadOnlyList<Error> Validate()
    {
        var errors = new List<Error>();

        if (Id.IsEmpty)
            errors.Add(DeviceErrors.InvalidField("Id", "模块 Id 不能为空。"));

        if (BusId.IsEmpty)
            errors.Add(DeviceErrors.InvalidField("BusId", "模块必须挂在一条总线上。"));

        // 记录构造参数可被反序列化或 with 表达式置为 null，这里不能信任可空注解
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(DeviceErrors.InvalidField("Name", "模块名称不能为空。"));
        else if (Name.Length > MaxNameLength)
            errors.Add(DeviceErrors.InvalidField("Name", $"模块名称不能超过 {MaxNameLength} 个字符。"));

        if (Address is < MinAddress or > MaxAddress)
            errors.Add(DeviceErrors.InvalidField("Address", $"地址必须在 [{MinAddress}, {MaxAddress}] 之间，当前为 {Address}。"));

        if (Remark is { Length: > MaxRemarkLength })
            errors.Add(DeviceErrors.InvalidField("Remark", $"备注不能超过 {MaxRemarkLength} 个字符。"));

        return errors;
    }

    /// <summary>
    /// 日志格式：人读的名称与地址在前，机器检索用的 Id 在后
    /// </summary>
    public override string ToString() => $"模块 {Name}（地址 {Address}）[{Id}]";
}
