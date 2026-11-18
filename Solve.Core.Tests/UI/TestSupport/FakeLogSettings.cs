using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.UI.TestSupport;

/// <summary>
/// 日志设置替身：记录收到的预览与保存请求，按 <see cref="NextPreview"/>、<see cref="NextResult"/> 返回；保存成功时更新当前设置
/// </summary>
internal sealed class FakeLogSettings(string currentDirectory, string defaultDirectory, string? startupFallbackReason = null)
    : ILogSettings
{
    public string CurrentDirectory { get; private set; } = currentDirectory;

    public string DefaultDirectory { get; } = defaultDirectory;

    public LogRetentionPolicy Retention { get; set; } = LogRetentionPolicy.Default;

    public string? StartupFallbackReason { get; } = startupFallbackReason;

    public List<(string? Directory, LogRetentionPolicy Retention)> Previews { get; } = [];

    public List<(string? Directory, LogRetentionPolicy Retention)> Saves { get; } = [];

    /// <summary>
    /// 下一次预览的结果；为 null 时没有要删除的文件
    /// </summary>
    public Result<LogPrunePreview>? NextPreview { get; set; }

    /// <summary>
    /// 预览时抛出的异常（模拟服务实现缺陷）
    /// </summary>
    public Exception? PreviewException { get; set; }

    /// <summary>
    /// 下一次保存的结果；为 null 时成功，切换到请求的目录与设置，删除的文件与预览一致
    /// </summary>
    public Result<LogSettingsSaved>? NextResult { get; set; }

    public Task<Result<LogPrunePreview>> PreviewAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken)
    {
        Previews.Add((directory, retention));
        if (PreviewException is not null)
            throw PreviewException;

        return Task.FromResult(NextPreview ?? Result<LogPrunePreview>.Success(LogPrunePreview.None));
    }

    public Task<Result<LogSettingsSaved>> SaveAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken)
    {
        Saves.Add((directory, retention));
        var deleted = NextPreview is { IsSuccess: true } preview ? preview.Value : LogPrunePreview.None;
        var result = NextResult ?? Result<LogSettingsSaved>.Success(new LogSettingsSaved(directory ?? DefaultDirectory, retention, deleted));
        if (result.IsSuccess)
        {
            CurrentDirectory = result.Value.Directory;
            Retention = result.Value.Retention;
        }

        return Task.FromResult(result);
    }
}
