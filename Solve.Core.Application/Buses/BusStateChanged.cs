using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 总线状态变化事件（含自迁移，例如重连中的单次失败）。
/// </summary>
/// <param name="Timestamp">迁移生效时刻（UTC，来自注入的 TimeProvider）</param>
/// <param name="Cause">触发迁移的错误原因；用户主动操作（连接、断开、复位）时为 null</param>
public sealed record BusStateChanged(BusId BusId, BusTransition Transition, DateTimeOffset Timestamp, Error? Cause);
