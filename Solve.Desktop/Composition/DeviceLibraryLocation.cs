using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Composition;

/// <summary>
/// 驱动库目录：保存在配置库（键 <see cref="SettingKey"/>），启动时解析一次，修改在下次启动时生效。
/// </summary>
/// <remarks>
/// 保存空串表示"跟随默认目录"：默认目录随程序所在位置变化（源码目录 / 部署目录），不应被写死成某一次的绝对路径。
/// 保存的目录在启动时不可用（不是完整路径、指向文件、无法创建）时，本次运行改用默认目录并说明原因，不让驱动库整体失效。
/// </remarks>
public sealed class DeviceLibraryLocation : IDeviceLibraryLocation
{
    public const string SettingKey = "devicelibrary.directory";

    private readonly IAppSettingsStore _settings;
    private readonly ILogger _logger;
    private string _next;

    private DeviceLibraryLocation(
        IAppSettingsStore settings,
        ILogger logger,
        string current,
        string defaultDirectory,
        string next,
        string? fallbackReason)
    {
        _settings = settings;
        _logger = logger;
        CurrentDirectory = current;
        DefaultDirectory = defaultDirectory;
        _next = next;
        StartupFallbackReason = fallbackReason;
    }

    public string CurrentDirectory { get; }

    public string DefaultDirectory { get; }

    public string NextDirectory => Volatile.Read(ref _next);

    public string? StartupFallbackReason { get; }

    /// <summary>
    /// 启动时读取保存的目录并决定本次运行使用哪个目录
    /// </summary>
    public static async Task<DeviceLibraryLocation> ResolveAsync(
        IAppSettingsStore settings,
        string defaultDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var fallback = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDirectory));
        var read = await settings.GetAsync(SettingKey, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            var reason = $"读取驱动库目录设置失败，本次运行使用默认目录：{read.Error.Message}";
            logger.LogError("{Reason}", reason);
            return new DeviceLibraryLocation(settings, logger, fallback, fallback, fallback, reason);
        }

        if (string.IsNullOrWhiteSpace(read.Value))
            return new DeviceLibraryLocation(settings, logger, fallback, fallback, fallback, null);

        var saved = Normalize(read.Value);
        if (saved.IsFailure)
        {
            var reason = $"保存的驱动库目录「{read.Value}」不可用，本次运行使用默认目录：{saved.Error.Message}";
            logger.LogWarning("{Reason}", reason);
            return new DeviceLibraryLocation(settings, logger, fallback, fallback, read.Value!, reason);
        }

        return new DeviceLibraryLocation(settings, logger, saved.Value, fallback, saved.Value, null);
    }

    public Task<Result<string>> SaveAsync(string? directory, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            var target = string.IsNullOrWhiteSpace(directory) ? Result<string>.Success(DefaultDirectory) : Normalize(directory);
            if (target.IsFailure)
                return target;

            try
            {
                Directory.CreateDirectory(target.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Result<string>.Failure(DeviceLibraryLocationErrors.InvalidPath($"无法创建目录 {target.Value}：{ex.Message}"));
            }

            // 选的就是默认目录时存空串：之后默认目录变了（换了部署位置）仍然跟随
            var isDefault = string.Equals(target.Value, DefaultDirectory, StringComparison.OrdinalIgnoreCase);
            var saved = await _settings.SetAsync(SettingKey, isDefault ? string.Empty : target.Value, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<string>.Failure(saved.Error);

            Volatile.Write(ref _next, target.Value);
            _logger.LogWarning("驱动库目录已设为 {Directory}（{Kind}），重启程序后生效；本次运行仍使用 {Current}",
                target.Value, isDefault ? "默认目录" : "自定义目录", CurrentDirectory);
            return Result<string>.Success(target.Value);
        }, cancellationToken);

    /// <summary>
    /// 只接受完整路径：相对路径会随工作目录变化，换个方式启动程序就扫描到别处去了
    /// </summary>
    internal static Result<string> Normalize(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return Result<string>.Failure(DeviceLibraryLocationErrors.InvalidPath("路径为空。"));
        if (!Path.IsPathFullyQualified(text))
            return Result<string>.Failure(DeviceLibraryLocationErrors.InvalidPath($"必须是完整路径（如 E:\\Solve\\Solve\\Devicelibrary），当前为「{text}」。"));

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(DeviceLibraryLocationErrors.InvalidPath(ex.Message));
        }

        if (File.Exists(full))
            return Result<string>.Failure(DeviceLibraryLocationErrors.InvalidPath($"{full} 是一个文件，不是目录。"));

        return Result<string>.Success(full);
    }
}
