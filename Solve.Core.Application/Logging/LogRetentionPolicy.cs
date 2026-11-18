using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Logging;

/// <summary>
/// 运行日志的保留与分卷：保存天数、单个文件的大小上限。只能经 <see cref="Create"/> 创建，取值一定在允许范围内。
/// </summary>
/// <remarks>
/// <para>日志按天分文件；单个文件超过上限后，同一天续写为 _001、_002…</para>
/// <para>超过保存天数的日志、以及全部日志合计超过 <see cref="TotalSizeLimitBytes"/> 时最旧的日志，会被自动删除。
/// 总量上限固定不可改：防止日志异常暴增时占满磁盘（配置库也在同一磁盘上）。</para>
/// </remarks>
public sealed record LogRetentionPolicy
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
    public const int DefaultRetentionDays = 30;

    public const int MinFileSizeLimitMegabytes = 10;
    public const int MaxFileSizeLimitMegabytes = 1024;
    public const int DefaultFileSizeLimitMegabytes = 50;

    /// <summary>
    /// 全部日志文件合计的大小上限
    /// </summary>
    public const long TotalSizeLimitBytes = 2L * 1024 * 1024 * 1024;

    private LogRetentionPolicy(int retentionDays, int fileSizeLimitMegabytes)
    {
        RetentionDays = retentionDays;
        FileSizeLimitMegabytes = fileSizeLimitMegabytes;
    }

    public static LogRetentionPolicy Default { get; } = new(DefaultRetentionDays, DefaultFileSizeLimitMegabytes);

    /// <summary>
    /// 保存最近多少天（含今天）
    /// </summary>
    public int RetentionDays { get; }

    public int FileSizeLimitMegabytes { get; }

    public long FileSizeLimitBytes => FileSizeLimitMegabytes * 1024L * 1024L;

    public static Result<LogRetentionPolicy> Create(int retentionDays, int fileSizeLimitMegabytes)
    {
        if (retentionDays is < MinRetentionDays or > MaxRetentionDays)
            return Result<LogRetentionPolicy>.Failure(LogRetentionErrors.RetentionDaysOutOfRange(retentionDays));
        if (fileSizeLimitMegabytes is < MinFileSizeLimitMegabytes or > MaxFileSizeLimitMegabytes)
            return Result<LogRetentionPolicy>.Failure(LogRetentionErrors.FileSizeLimitOutOfRange(fileSizeLimitMegabytes));

        return Result<LogRetentionPolicy>.Success(new LogRetentionPolicy(retentionDays, fileSizeLimitMegabytes));
    }

    public override string ToString() => $"保存 {RetentionDays} 天，单个文件 {FileSizeLimitMegabytes} MB";
}

/// <summary>
/// 日志保留设置相关错误码
/// </summary>
public static class LogRetentionErrors
{
    public const string RetentionDaysOutOfRangeCode = "LogRetention.RetentionDaysOutOfRange";
    public const string FileSizeLimitOutOfRangeCode = "LogRetention.FileSizeLimitOutOfRange";
    public const string PreviewFailedCode = "LogRetention.PreviewFailed";

    public static Error RetentionDaysOutOfRange(int value)
        => new(RetentionDaysOutOfRangeCode,
            $"保存天数须在 {LogRetentionPolicy.MinRetentionDays}～{LogRetentionPolicy.MaxRetentionDays} 天之间：{value}");

    public static Error FileSizeLimitOutOfRange(int value)
        => new(FileSizeLimitOutOfRangeCode,
            $"单个文件上限须在 {LogRetentionPolicy.MinFileSizeLimitMegabytes}～{LogRetentionPolicy.MaxFileSizeLimitMegabytes} MB 之间：{value}");

    public static Error PreviewFailed(string directory, string detail)
        => new(PreviewFailedCode, $"无法检查 {directory} 中的日志文件：{detail}");
}
