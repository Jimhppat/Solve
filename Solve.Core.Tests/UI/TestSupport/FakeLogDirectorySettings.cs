using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.UI.TestSupport;

/// <summary>
/// 日志目录设置替身：记录收到的请求，按 <see cref="NextResult"/> 返回；成功时更新当前目录
/// </summary>
internal sealed class FakeLogDirectorySettings(string currentDirectory, string defaultDirectory, string? startupFallbackReason = null)
    : ILogDirectorySettings
{
    public string CurrentDirectory { get; private set; } = currentDirectory;

    public string DefaultDirectory { get; } = defaultDirectory;

    public string? StartupFallbackReason { get; } = startupFallbackReason;

    public List<string?> Requests { get; } = [];

    /// <summary>
    /// 下一次请求的结果；为 null 时成功并切换到请求的目录（null 表示默认目录）
    /// </summary>
    public Result<string>? NextResult { get; set; }

    public Task<Result<string>> ChangeDirectoryAsync(string? directory, CancellationToken cancellationToken)
    {
        Requests.Add(directory);
        var result = NextResult ?? Result<string>.Success(directory ?? DefaultDirectory);
        if (result.IsSuccess)
            CurrentDirectory = result.Value;
        return Task.FromResult(result);
    }
}
