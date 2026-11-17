namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线配置描述基类，具体物理总线（串口、TCP 等）派生自此类型。字段在 M2 补充。
/// </summary>
public abstract record BusDescriptor
{
}
