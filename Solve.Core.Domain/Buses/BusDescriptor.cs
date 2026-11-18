using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线配置描述基类。派生类型只描述物理链路参数，不包含任何协议（帧格式、站号等）信息。
/// </summary>
/// <remarks>
/// 描述对象来自配置文件或 UI 输入，属于不可信数据：注册到运行时之前必须调用 <see cref="Validate"/>，
/// 返回非空列表时拒绝注册。
/// </remarks>
public abstract record BusDescriptor(BusId Id, string Name)
{
    public const int MaxNameLength = 64;

    public BusTimeouts Timeouts { get; init; } = BusTimeouts.Default;

    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>
    /// 校验全部字段并一次性返回所有错误（便于 UI 同时高亮多个字段）。空列表表示有效。
    /// </summary>
    public IReadOnlyList<Error> Validate()
    {
        var errors = new List<Error>();

        if (Id.IsEmpty)
            errors.Add(BusErrors.InvalidField("Id", "总线 Id 不能为空。"));

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(BusErrors.InvalidField("Name", "总线名称不能为空。"));
        else if (Name.Length > MaxNameLength)
            errors.Add(BusErrors.InvalidField("Name", $"总线名称不能超过 {MaxNameLength} 个字符。"));

        // init 属性可被反序列化或 with 表达式置为 null，这里不能信任可空注解
        if (Timeouts is null)
            errors.Add(BusErrors.InvalidField("Timeouts", "超时配置不能为空。"));
        else
            Timeouts.CollectErrors(errors);

        if (Reconnect is null)
            errors.Add(BusErrors.InvalidField("Reconnect", "重连策略不能为空。"));
        else
            Reconnect.CollectErrors(errors);

        CollectLinkErrors(errors);
        return errors;
    }

    /// <summary>
    /// 派生类校验各自的物理链路参数
    /// </summary>
    protected abstract void CollectLinkErrors(List<Error> errors);

    /// <summary>
    /// 判断与另一个描述符相比是否改变了物理链路参数（如串口名、波特率、TCP Host/Port）。
    /// 返回 true 表示必须关闭并重建物理通道（Tier 2 硬参数）；返回 false 表示仅软参数变化（Tier 1），可在线热更新。
    /// </summary>
    public abstract bool RequiresTransportRebuild(BusDescriptor other);
}
