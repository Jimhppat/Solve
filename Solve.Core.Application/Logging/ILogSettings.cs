using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 按设置会被删除（或已被删除）的日志文件汇总
/// </summary>
/// <param name="OldestDate">其中最早的日志日期；没有文件时为 null</param>
/// <param name="NewestDate">其中最晚的日志日期；没有文件时为 null</param>
public sealed record LogPrunePreview(int FileCount, long TotalBytes, DateOnly? OldestDate, DateOnly? NewestDate)
{
    public static LogPrunePreview None { get; } = new(0, 0, null, null);

    public bool IsEmpty => FileCount == 0;
}

/// <summary>
/// 一次保存的结果
/// </summary>
/// <param name="Directory">之后写入的目录（规范化后的完整路径）</param>
/// <param name="Deleted">保存后立即清理掉的日志文件</param>
public sealed record LogSettingsSaved(string Directory, LogRetentionPolicy Retention, LogPrunePreview Deleted);

/// <summary>
/// 黑匣子日志的设置：存储目录、保存天数、单个文件上限，供设置页整页查看与保存。
/// </summary>
/// <remarks>
/// <para>保存立即生效，并作为之后启动使用的设置。切换目录时旧目录中已有的日志原地保留：不迁移，也不再自动清理。</para>
/// <para>保存后会立即按新的保存天数清理当前目录中过期的日志，删除不可恢复：保存前先用 <see cref="PreviewAsync"/>
/// 列出会被删除的文件，由界面让人确认。</para>
/// <para>切换前先确认目录可写；校验或保存失败时设置与写入位置都保持原样，日志不会中断。</para>
/// </remarks>
public interface ILogSettings
{
    /// <summary>
    /// 当前正在写入的目录（完整路径）
    /// </summary>
    string CurrentDirectory { get; }

    /// <summary>
    /// 默认目录（程序目录下的 logs）
    /// </summary>
    string DefaultDirectory { get; }

    /// <summary>
    /// 当前生效的保存天数与单个文件上限
    /// </summary>
    LogRetentionPolicy Retention { get; }

    /// <summary>
    /// 启动时保存的设置不可用（目录不可写、数值超出范围等）、本次运行改用默认值的原因；为 null 表示没有发生这种情况
    /// </summary>
    string? StartupFallbackReason { get; }

    /// <summary>
    /// 按 <paramref name="retention"/> 保存到 <paramref name="directory"/> 后会被删除的日志文件，不做任何修改。
    /// 目录为空白、不是完整路径或不存在时视为没有要删除的文件（是否可用由 <see cref="SaveAsync"/> 校验）。在线程池上执行。
    /// </summary>
    Task<Result<LogPrunePreview>> PreviewAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken);

    /// <summary>
    /// 保存全部设置并立即生效；<paramref name="directory"/> 为 null 或空白表示恢复默认目录。
    /// 涉及磁盘 I/O，在线程池上执行；开始后不再响应取消，要么全部生效、要么全部保持原样。
    /// </summary>
    Task<Result<LogSettingsSaved>> SaveAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken);
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
        => new(SettingsUnavailableCode, "本次运行未提供日志设置文件，无法保存");

    public static Error LoadFailed(string path, string detail)
        => new(LoadFailedCode, $"日志设置文件 {path} 读取失败：{detail}");

    public static Error SaveFailed(string path, string detail)
        => new(SaveFailedCode, $"日志设置文件 {path} 保存失败：{detail}");

    public static Error SwitchFailed(string directory, string detail)
        => new(SwitchFailedCode, $"切换到 {directory} 失败，继续写入原目录：{detail}");
}
