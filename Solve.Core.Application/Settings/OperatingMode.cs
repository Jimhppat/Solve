using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Settings;

/// <summary>
/// 运行模式（操作员视角）：生产 = 实机串口 / TCP，仿真 = 仿真通道与仿真设备
/// </summary>
public enum OperatingMode
{
    Simulation = 0,
    Production = 1,
}

/// <summary>
/// 生产 / 仿真切换。模式在启动时一次性决定、整个进程内不变；切换只保存目标模式，由调用方安排受控重启后生效。
/// </summary>
/// <remarks>
/// 不做运行中热切换：总线通道在注册时就按模式建好，命令的确认规则也随模式固定，
/// 重启让新进程按新模式整体重建，不会出现新旧模式的对象混在同一进程里。
/// </remarks>
public interface IOperatingModeSwitch
{
    /// <summary>
    /// 本次运行实际生效的模式
    /// </summary>
    OperatingMode CurrentMode { get; }

    /// <summary>
    /// 本机不允许切到生产模式的原因；允许时为 null
    /// </summary>
    string? ProductionBlockedReason { get; }

    /// <summary>
    /// 已成功保存目标模式、等待重启。为 true 后不再接受新的切换请求
    /// </summary>
    bool IsRestartPending { get; }

    /// <summary>
    /// 校验并保存目标模式（下次启动生效）。要求全部总线已断开；成功后调用方须立即安排重启。
    /// 内部访问配置库（同步磁盘 I/O），不要在 UI 线程上直接等待。
    /// </summary>
    Task<Result> SaveAsync(OperatingMode target, CancellationToken cancellationToken);
}

/// <summary>
/// 模式切换错误码
/// </summary>
public static class OperatingModeErrors
{
    public static Error Unchanged(OperatingMode mode)
        => new("Mode.Unchanged", $"当前已经是{Describe(mode)}模式。");

    public static Error ProductionNotAllowed(string reason)
        => new("Mode.ProductionNotAllowed", reason);

    public static Error BusesActive(IReadOnlyCollection<string> names)
        => new("Mode.BusesActive", $"以下总线仍在连接或断开中：{string.Join("、", names)}。请先在总线监控页断开全部总线再切换。");

    public static Error InProgress()
        => new("Mode.InProgress", "正在处理上一次切换，请稍候。");

    public static Error RestartPending()
        => new("Mode.RestartPending", "模式已保存，程序正在重启。");

    public static Error VerifyFailed(string detail)
        => new("Mode.VerifyFailed", $"模式保存后回读校验失败，本次不重启：{detail}");

    public static string Describe(OperatingMode mode) => mode == OperatingMode.Production ? "生产" : "仿真";
}
