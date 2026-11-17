using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Settings.Ports;

/// <summary>
/// 应用级键值设置存储出站端口。值一律为文本，由调用方负责严格解析；存储失败返回 Result，不抛异常。
/// </summary>
/// <remarks>
/// 实现可能执行同步磁盘 I/O，调用方不要在 UI / 控制线程上直接等待。
/// </remarks>
public interface IAppSettingsStore
{
    /// <summary>
    /// 读取设置；不存在时返回成功且值为 null。
    /// </summary>
    Task<Result<string?>> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// 写入或覆盖设置。
    /// </summary>
    Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>
/// 设置相关错误码
/// </summary>
public static class SettingsErrors
{
    public const string StorageFailedCode = "Settings.Storage.Failed";

    public static Error StorageFailed(string detail) => new(StorageFailedCode, $"设置存储失败：{detail}");
}
