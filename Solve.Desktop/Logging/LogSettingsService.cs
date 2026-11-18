using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 启动时决定的日志设置
/// </summary>
/// <param name="Directory">本次运行使用的目录</param>
/// <param name="ConfiguredDirectory">设置文件中保存的目录；未配置或文件读取失败时为 null</param>
/// <param name="Retention">本次运行使用的保存天数与单个文件上限</param>
/// <param name="FallbackReason">保存的设置不可用、改用默认值的原因（可能有多条）；为 null 表示没有发生退回</param>
public sealed record LogSettingsStartup(
    string Directory,
    string? ConfiguredDirectory,
    LogRetentionPolicy Retention,
    string? FallbackReason);

/// <summary>
/// 日志设置的查看与保存：校验 → 保存设置文件 → 应用保留设置 → 切换目录 → 立即清理。
/// </summary>
/// <remarks>
/// <para>先保存后生效：保存失败时什么都不变；生效失败时把设置文件与保留设置恢复为原值，
/// 保证"正在使用的设置"与"下次启动的设置"一致。</para>
/// <para>先应用保留设置再切换目录：新目录第一次打开文件时就按新的保存天数清理，不会先按旧的天数误删。</para>
/// </remarks>
public sealed class LogSettingsService : ILogSettings
{
    private readonly LoggingPipeline _pipeline;
    private readonly LogSettingsStore? _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _configuredDirectory;

    public LogSettingsService(
        LoggingPipeline pipeline,
        LogSettingsStore? store,
        string defaultDirectory,
        LogSettingsStartup startup,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _store = store;
        _logger = logger;
        _configuredDirectory = startup.ConfiguredDirectory;
        DefaultDirectory = defaultDirectory;
        StartupFallbackReason = startup.FallbackReason;
    }

    public string CurrentDirectory => _pipeline.LogDirectory;

    public string DefaultDirectory { get; }

    public LogRetentionPolicy Retention => _pipeline.Retention;

    public string? StartupFallbackReason { get; }

    /// <summary>
    /// 启动时决定日志设置：读取设置文件，确认目录可写、数值在允许范围内；不可用的项改用默认值并给出原因，日志必须能启动。
    /// </summary>
    /// <remarks>
    /// 退回默认值时不改写设置文件：U 盘、扩展盘等下次接好后，重启即可恢复使用保存的目录。
    /// 各项分别退回：目录不可用不影响保存天数，反之亦然。
    /// </remarks>
    public static LogSettingsStartup ResolveStartup(string defaultDirectory, LogSettingsStore? store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);

        if (store is null)
            return new LogSettingsStartup(defaultDirectory, null, LogRetentionPolicy.Default, null);

        var loaded = store.Load();
        if (loaded.IsFailure)
            return new LogSettingsStartup(defaultDirectory, null, LogRetentionPolicy.Default, loaded.Error.Message);

        var stored = loaded.Value;
        var reasons = new List<string>();

        var directory = defaultDirectory;
        if (stored.LogDirectory is { } configured)
        {
            var prepared = LogDirectoryValidator.Prepare(configured);
            if (prepared.IsSuccess)
                directory = prepared.Value;
            else
                reasons.Add($"保存的日志目录不可用：{prepared.Error.Message}");
        }

        var days = stored.RetentionDays ?? LogRetentionPolicy.DefaultRetentionDays;
        if (days is < LogRetentionPolicy.MinRetentionDays or > LogRetentionPolicy.MaxRetentionDays)
        {
            reasons.Add($"保存的保存天数 {days} 超出 {LogRetentionPolicy.MinRetentionDays}～{LogRetentionPolicy.MaxRetentionDays} 天，" +
                        $"本次运行使用默认的 {LogRetentionPolicy.DefaultRetentionDays} 天");
            days = LogRetentionPolicy.DefaultRetentionDays;
        }

        var megabytes = stored.FileSizeLimitMegabytes ?? LogRetentionPolicy.DefaultFileSizeLimitMegabytes;
        if (megabytes is < LogRetentionPolicy.MinFileSizeLimitMegabytes or > LogRetentionPolicy.MaxFileSizeLimitMegabytes)
        {
            reasons.Add($"保存的单个文件上限 {megabytes} MB 超出 {LogRetentionPolicy.MinFileSizeLimitMegabytes}～" +
                        $"{LogRetentionPolicy.MaxFileSizeLimitMegabytes} MB，本次运行使用默认的 {LogRetentionPolicy.DefaultFileSizeLimitMegabytes} MB");
            megabytes = LogRetentionPolicy.DefaultFileSizeLimitMegabytes;
        }

        // 上面已收拢到合法范围，这里一定成功
        var retention = LogRetentionPolicy.Create(days, megabytes).Value;
        return new LogSettingsStartup(directory, stored.LogDirectory, retention, reasons.Count == 0 ? null : string.Join("；", reasons));
    }

    public Task<Result<LogPrunePreview>> PreviewAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        return Task.Run(() => Preview(directory, retention), cancellationToken);
    }

    public async Task<Result<LogSettingsSaved>> SaveAsync(string? directory, LogRetentionPolicy retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 开始后不再响应取消：保存与生效要么都完成、要么都回到原状，中途停下会留下不一致的状态
            return await Task.Run(() => Save(directory, retention), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result<LogPrunePreview> Preview(string? requested, LogRetentionPolicy retention)
    {
        var target = string.IsNullOrWhiteSpace(requested) ? DefaultDirectory : requested.Trim();
        if (!Path.IsPathFullyQualified(target))
            return Result<LogPrunePreview>.Success(LogPrunePreview.None);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<LogPrunePreview>.Success(LogPrunePreview.None);
        }

        // 网络路径保存时一定被拒绝；这里不去访问它，免得网络不通时卡住数十秒
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return Result<LogPrunePreview>.Success(LogPrunePreview.None);

        try
        {
            return Result<LogPrunePreview>.Success(DailyLogFiles.Summarize(_pipeline.PreviewPrune(fullPath, retention)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Result<LogPrunePreview>.Failure(LogRetentionErrors.PreviewFailed(fullPath, ex.Message));
        }
    }

    private Result<LogSettingsSaved> Save(string? requested, LogRetentionPolicy retention)
    {
        try
        {
            return SaveCore(requested, retention);
        }
        catch (Exception ex)
        {
            // 线程池任务的边界：意外异常记录后以失败返回，不让它回到 UI 线程触发全站跳闸退出
            _logger.LogError(ex, "保存日志设置时发生意外异常，继续写入 {Directory}，{Retention}", _pipeline.LogDirectory, _pipeline.Retention);
            return Result<LogSettingsSaved>.Failure(LogDirectoryErrors.SwitchFailed(requested ?? DefaultDirectory, ex.Message));
        }
    }

    private Result<LogSettingsSaved> SaveCore(string? requested, LogRetentionPolicy retention)
    {
        var restoreDefault = string.IsNullOrWhiteSpace(requested);
        var prepared = LogDirectoryValidator.Prepare(restoreDefault ? DefaultDirectory : requested!);
        if (prepared.IsFailure)
        {
            _logger.LogWarning("日志设置保存被拒绝 目录={Target}：{Error}", requested, prepared.Error);
            return Result<LogSettingsSaved>.Failure(prepared.Error);
        }

        if (_store is null)
            return Result<LogSettingsSaved>.Failure(LogDirectoryErrors.SettingsUnavailable());

        var target = prepared.Value;
        var previousDirectory = _pipeline.LogDirectory;
        var previousRetention = _pipeline.Retention;
        var previousStored = Stored(_configuredDirectory, previousRetention);

        // 选中的就是默认目录时保存为"未配置"：程序搬家后默认目录随之变化
        var toSave = LogDirectoryValidator.SameDirectory(target, DefaultDirectory) ? null : target;
        var saved = _store.Save(Stored(toSave, retention));
        if (saved.IsFailure)
        {
            _logger.LogError("日志设置保存失败，继续写入 {Directory}，{Retention}：{Error}", previousDirectory, previousRetention, saved.Error);
            return Result<LogSettingsSaved>.Failure(saved.Error);
        }

        _configuredDirectory = toSave;

        var directoryChanged = !LogDirectoryValidator.SameDirectory(target, previousDirectory);
        if (retention != previousRetention)
            _logger.LogInformation("日志保留设置更改：{Previous} → {Target}", previousRetention, retention);

        // 这条主要落在原目录，切换后的说明落在新目录：两边都记下对方的位置，事后能顺着找到
        if (directoryChanged)
            _logger.LogInformation("日志目录即将切换 {Previous} → {Target}", previousDirectory, target);

        try
        {
            _pipeline.ApplyRetention(retention);
            if (directoryChanged)
                _pipeline.SwitchDirectory(target);
        }
        catch (Exception ex)
        {
            Rollback(previousStored, previousRetention, ex);
            return Result<LogSettingsSaved>.Failure(LogDirectoryErrors.SwitchFailed(target, ex.Message));
        }

        if (directoryChanged)
            _logger.LogInformation("日志目录已切换 {Previous} → {Target}，此前的日志保留在原目录", previousDirectory, target);
        else if (retention == previousRetention)
            _logger.LogInformation("日志设置已保存，未变化：{Directory}，{Retention}", target, retention);

        // 按新设置立即清理：缩短保存天数后过期文件马上删除，界面能报告删了哪些（界面已在保存前让人确认过）
        var deleted = DailyLogFiles.Summarize(_pipeline.PruneNow());
        if (!deleted.IsEmpty)
        {
            _logger.LogWarning(
                "已按日志保留设置删除 {Count} 个日志文件：{Oldest:yyyy-MM-dd}～{Newest:yyyy-MM-dd}，共 {Bytes} 字节，目录 {Directory}",
                deleted.FileCount, deleted.OldestDate, deleted.NewestDate, deleted.TotalBytes, target);
        }

        return Result<LogSettingsSaved>.Success(new LogSettingsSaved(target, retention, deleted));
    }

    // 生效失败：恢复保留设置与设置文件，继续按原设置写原目录
    private void Rollback(StoredLogSettings previousStored, LogRetentionPolicy previousRetention, Exception cause)
    {
        Exception? retentionRollbackError = null;
        try
        {
            _pipeline.ApplyRetention(previousRetention);
        }
        catch (Exception ex)
        {
            retentionRollbackError = ex;
        }

        var fileRollback = _store!.Save(previousStored);
        if (fileRollback.IsSuccess)
            _configuredDirectory = previousStored.LogDirectory;

        _logger.LogError(cause,
            "日志设置生效失败，继续写入 {Directory}；保留设置恢复={RetentionRollback}，设置文件恢复={FileRollback}",
            _pipeline.LogDirectory, retentionRollbackError?.Message ?? "成功", fileRollback);
    }

    private static StoredLogSettings Stored(string? directory, LogRetentionPolicy retention)
        => new(directory, retention.RetentionDays, retention.FileSizeLimitMegabytes);
}
