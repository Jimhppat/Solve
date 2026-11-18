using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 启动时决定的日志目录
/// </summary>
/// <param name="Directory">本次运行使用的目录</param>
/// <param name="Configured">设置文件中保存的目录；未配置或文件读取失败时为 null</param>
/// <param name="FallbackReason">保存的目录不可用、改用默认目录的原因；为 null 表示没有发生退回</param>
public sealed record LogDirectoryStartup(string Directory, string? Configured, string? FallbackReason);

/// <summary>
/// 日志目录的查看与切换：校验 → 保存设置 → 切换日志管线的输出目录。
/// </summary>
/// <remarks>
/// 先保存后切换：保存失败时什么都不变；切换失败时把设置恢复为原值，保证"正在写的目录"与"下次启动的目录"一致。
/// </remarks>
public sealed class LogDirectoryService : ILogDirectorySettings
{
    private readonly LoggingPipeline _pipeline;
    private readonly LogLocationStore? _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _configured;

    public LogDirectoryService(
        LoggingPipeline pipeline,
        LogLocationStore? store,
        string defaultDirectory,
        LogDirectoryStartup startup,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _store = store;
        _logger = logger;
        _configured = startup.Configured;
        DefaultDirectory = defaultDirectory;
        StartupFallbackReason = startup.FallbackReason;
    }

    public string CurrentDirectory => _pipeline.LogDirectory;

    public string DefaultDirectory { get; }

    public string? StartupFallbackReason { get; }

    /// <summary>
    /// 启动时决定日志目录：读取设置文件中保存的目录并确认可写；不可用时改用默认目录并给出原因，日志必须能启动。
    /// </summary>
    /// <remarks>
    /// 退回默认目录时不改写设置文件：U 盘、扩展盘等下次接好后，重启即可恢复使用保存的目录。
    /// </remarks>
    public static LogDirectoryStartup ResolveStartup(string defaultDirectory, LogLocationStore? store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);

        if (store is null)
            return new LogDirectoryStartup(defaultDirectory, null, null);

        var loaded = store.Load();
        if (loaded.IsFailure)
            return new LogDirectoryStartup(defaultDirectory, null, loaded.Error.Message);

        if (loaded.Value is not { } configured)
            return new LogDirectoryStartup(defaultDirectory, null, null);

        var prepared = LogDirectoryValidator.Prepare(configured);
        return prepared.IsSuccess
            ? new LogDirectoryStartup(prepared.Value, configured, null)
            : new LogDirectoryStartup(defaultDirectory, configured, $"保存的日志目录不可用：{prepared.Error.Message}");
    }

    public async Task<Result<string>> ChangeDirectoryAsync(string? directory, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 开始后不再响应取消：保存与切换要么都完成、要么都回到原状，中途停下会留下不一致的状态
            return await Task.Run(() => Change(directory), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result<string> Change(string? requested)
    {
        try
        {
            return ChangeCore(requested);
        }
        catch (Exception ex)
        {
            // 线程池任务的边界：意外异常记录后以失败返回，不让它回到 UI 线程触发全站跳闸退出
            _logger.LogError(ex, "切换日志目录时发生意外异常，继续写入 {Directory}", _pipeline.LogDirectory);
            return Result<string>.Failure(LogDirectoryErrors.SwitchFailed(requested ?? DefaultDirectory, ex.Message));
        }
    }

    private Result<string> ChangeCore(string? requested)
    {
        var restoreDefault = string.IsNullOrWhiteSpace(requested);
        var prepared = LogDirectoryValidator.Prepare(restoreDefault ? DefaultDirectory : requested!);
        if (prepared.IsFailure)
        {
            _logger.LogWarning("日志目录切换被拒绝 目标={Target}：{Error}", requested, prepared.Error);
            return prepared;
        }

        if (_store is null)
            return Result<string>.Failure(LogDirectoryErrors.SettingsUnavailable());

        var target = prepared.Value;
        var previousDirectory = _pipeline.LogDirectory;
        var previousConfigured = _configured;

        // 选中的就是默认目录时保存为"未配置"：程序搬家后默认目录随之变化
        var toSave = LogDirectoryValidator.SameDirectory(target, DefaultDirectory) ? null : target;
        var saved = _store.Save(toSave);
        if (saved.IsFailure)
        {
            _logger.LogError("日志目录设置保存失败，继续写入 {Directory}：{Error}", previousDirectory, saved.Error);
            return Result<string>.Failure(saved.Error);
        }

        _configured = toSave;

        if (LogDirectoryValidator.SameDirectory(target, previousDirectory))
        {
            _logger.LogInformation("日志目录设置已保存，目录未变化：{Directory}", target);
            return Result<string>.Success(target);
        }

        // 这条主要落在原目录，下面那条落在新目录：两边都记下对方的位置，事后能顺着找到
        _logger.LogInformation("日志目录即将切换 {Previous} → {Target}", previousDirectory, target);
        try
        {
            _pipeline.SwitchDirectory(target);
        }
        catch (Exception ex)
        {
            var rollback = _store.Save(previousConfigured);
            if (rollback.IsSuccess)
                _configured = previousConfigured;

            _logger.LogError(ex, "日志目录切换失败，继续写入 {Directory}；设置恢复={Rollback}", previousDirectory, rollback);
            return Result<string>.Failure(LogDirectoryErrors.SwitchFailed(target, ex.Message));
        }

        _logger.LogInformation("日志目录已切换 {Previous} → {Target}，此前的日志保留在原目录", previousDirectory, target);
        return Result<string>.Success(target);
    }
}
