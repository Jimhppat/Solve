using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 黑匣子日志目录的查看与切换，供设置页使用。
/// </summary>
/// <remarks>
/// <para>切换立即生效，并保存为之后启动使用的目录。旧目录中已有的日志原地保留：不迁移，也不再按保留天数自动清理。</para>
/// <para>切换前先确认目录可写；校验或保存失败时继续写原目录，日志不会中断。</para>
/// </remarks>
public interface ILogDirectorySettings
{
    /// <summary>
    /// 当前正在写入的目录（完整路径）
    /// </summary>
    string CurrentDirectory { get; }

    /// <summary>
    /// 默认目录（程序目录下的 logs），恢复默认时使用
    /// </summary>
    string DefaultDirectory { get; }

    /// <summary>
    /// 启动时保存的目录不可用、本次运行改用默认目录的原因；为 null 表示没有发生这种情况
    /// </summary>
    string? StartupFallbackReason { get; }

    /// <summary>
    /// 切换到 <paramref name="directory"/> 并保存；null 或空白表示恢复默认目录。
    /// 涉及磁盘 I/O，在线程池上执行；成功时返回规范化后的完整路径。
    /// </summary>
    Task<Result<string>> ChangeDirectoryAsync(string? directory, CancellationToken cancellationToken);
}

/// <summary>
/// 日志目录相关错误码
/// </summary>
public static class LogDirectoryErrors
{
    public const string InvalidPathCode = "LogDirectory.InvalidPath";
    public const string NetworkPathCode = "LogDirectory.NetworkPath";
    public const string NotWritableCode = "LogDirectory.NotWritable";
    public const string SettingsUnavailableCode = "LogDirectory.SettingsUnavailable";
    public const string LoadFailedCode = "LogDirectory.LoadFailed";
    public const string SaveFailedCode = "LogDirectory.SaveFailed";
    public const string SwitchFailedCode = "LogDirectory.SwitchFailed";

    public static Error InvalidPath(string detail) => new(InvalidPathCode, detail);

    public static Error NetworkPath(string directory)
        => new(NetworkPathCode, $"不支持网络路径：{directory}。网络中断时日志会丢失，请选择本机磁盘上的目录");

    public static Error NotWritable(string directory, string detail)
        => new(NotWritableCode, $"目录不可写：{directory}（{detail}）");

    public static Error SettingsUnavailable()
        => new(SettingsUnavailableCode, "本次运行未提供日志目录设置文件，无法保存");

    public static Error LoadFailed(string path, string detail)
        => new(LoadFailedCode, $"日志目录设置文件 {path} 读取失败：{detail}");

    public static Error SaveFailed(string path, string detail)
        => new(SaveFailedCode, $"日志目录设置文件 {path} 保存失败：{detail}");

    public static Error SwitchFailed(string directory, string detail)
        => new(SwitchFailedCode, $"切换到 {directory} 失败，继续写入原目录：{detail}");
}
