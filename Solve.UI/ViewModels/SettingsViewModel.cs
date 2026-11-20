using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.UI.ViewModels;

/// <summary>
/// 设置页保存流程的状态
/// </summary>
public enum SettingsSaveState
{
    /// <summary>
    /// 可以保存
    /// </summary>
    Idle,

    /// <summary>
    /// 正在检查按新设置会删除哪些日志文件
    /// </summary>
    Checking,

    /// <summary>
    /// 新设置会删除日志文件，等待确认；确认前什么都没有改
    /// </summary>
    AwaitingConfirmation,

    /// <summary>
    /// 正在保存并生效
    /// </summary>
    Saving,
}

/// <summary>
/// 系统设置页。页面上的修改只在点「保存」时统一生效；包含运行日志的存储路径、保存天数与单个文件上限，以及驱动库目录（重启后生效）。
/// </summary>
/// <remarks>
/// <para>保存前先检查新设置会删除哪些日志文件：有要删的就停在 <see cref="SettingsSaveState.AwaitingConfirmation"/>，
/// 页面上列出个数、日期范围与大小，确认后才保存；取消或改动输入则什么都不改。黑匣子日志删除后无法恢复。</para>
/// <para>只能在 UI 线程上使用；磁盘 I/O 由设置服务在线程池上完成。</para>
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly Action<string>? _onLogDirectoryChanged;
    private ILogSettings? _settings;
    private string? _currentLogDirectory;
    private string _logDirectoryInput = string.Empty;
    private decimal? _retentionDaysInput = LogRetentionPolicy.DefaultRetentionDays;
    private decimal? _fileSizeLimitInput = LogRetentionPolicy.DefaultFileSizeLimitMegabytes;
    private string? _saveMessage;
    private bool _isSaveError;
    private SettingsSaveState _state = SettingsSaveState.Idle;
    private PendingSave? _pending;
    private bool _startupFallbackResolved;
    private IDeviceLibraryLocation? _library;
    private string _libraryDirectoryInput = string.Empty;

    /// <summary>
    /// XAML 设计器使用：未接入任何设置，保存不可用
    /// </summary>
    public SettingsViewModel() : this(onLogDirectoryChanged: null)
    {
    }

    /// <param name="onLogDirectoryChanged">保存成功后在 UI 线程上调用，由主窗口宿主同步到日志页的「打开日志目录」</param>
    public SettingsViewModel(Action<string>? onLogDirectoryChanged)
    {
        _onLogDirectoryChanged = onLogDirectoryChanged;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _settings is not null && _state == SettingsSaveState.Idle);
        ConfirmDeletionCommand = new AsyncRelayCommand(ConfirmDeletionAsync, () => _state == SettingsSaveState.AwaitingConfirmation);
        CancelDeletionCommand = new RelayCommand(CancelDeletion, () => _state == SettingsSaveState.AwaitingConfirmation);
    }

    public string PageTitle => "系统设置";

    /// <summary>
    /// 保存页面上的全部设置
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// 确认删除列出的日志文件并保存
    /// </summary>
    public AsyncRelayCommand ConfirmDeletionCommand { get; }

    /// <summary>
    /// 放弃这次保存，什么都不改
    /// </summary>
    public RelayCommand CancelDeletionCommand { get; }

    public SettingsSaveState State => _state;

    public bool IsAwaitingConfirmation => _state == SettingsSaveState.AwaitingConfirmation;

    /// <summary>
    /// 等待确认时的说明：会删除多少个文件、哪些日期、多大
    /// </summary>
    public string? PendingDeletionText => _pending is { } pending ? DescribeDeletion(pending.Preview) : null;

    /// <summary>
    /// 最近一次保存的结果说明
    /// </summary>
    public string? SaveMessage
    {
        get => _saveMessage;
        private set
        {
            if (SetProperty(ref _saveMessage, value))
                OnPropertyChanged(nameof(HasSaveMessage));
        }
    }

    public bool HasSaveMessage => _saveMessage is not null;

    public bool IsSaveError
    {
        get => _isSaveError;
        private set => SetProperty(ref _isSaveError, value);
    }

    // ==================== 运行日志 ====================

    /// <summary>
    /// 已接入日志设置（设计器中为 false）
    /// </summary>
    public bool IsLogSettingsAvailable => _settings is not null;

    /// <summary>
    /// 当前正在写入的日志目录
    /// </summary>
    public string? CurrentLogDirectory
    {
        get => _currentLogDirectory;
        private set => SetProperty(ref _currentLogDirectory, value);
    }

    /// <summary>
    /// 输入框中的存储路径，点「保存」后生效
    /// </summary>
    public string LogDirectoryInput
    {
        get => _logDirectoryInput;
        set
        {
            if (SetProperty(ref _logDirectoryInput, value ?? string.Empty))
                OnInputChanged();
        }
    }

    /// <summary>
    /// 输入框中的保存天数（数字输入框的值为 decimal，可能为空），点「保存」后生效
    /// </summary>
    public decimal? RetentionDaysInput
    {
        get => _retentionDaysInput;
        set
        {
            if (SetProperty(ref _retentionDaysInput, value))
                OnInputChanged();
        }
    }

    /// <summary>
    /// 输入框中的单个文件上限（MB），点「保存」后生效
    /// </summary>
    public decimal? FileSizeLimitMegabytesInput
    {
        get => _fileSizeLimitInput;
        set
        {
            if (SetProperty(ref _fileSizeLimitInput, value))
                OnInputChanged();
        }
    }

    // 数字输入框的范围，与 LogRetentionPolicy 一致
    public decimal MinRetentionDays => LogRetentionPolicy.MinRetentionDays;

    public decimal MaxRetentionDays => LogRetentionPolicy.MaxRetentionDays;

    public decimal MinFileSizeLimitMegabytes => LogRetentionPolicy.MinFileSizeLimitMegabytes;

    public decimal MaxFileSizeLimitMegabytes => LogRetentionPolicy.MaxFileSizeLimitMegabytes;

    /// <summary>
    /// 启动时保存的设置不可用、本次运行改用默认值的原因
    /// </summary>
    public string? StartupFallback => _startupFallbackResolved ? null : _settings?.StartupFallbackReason;

    public bool HasStartupFallback => StartupFallback is not null;

    /// <summary>
    /// 接入日志设置。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachLogSettings(ILogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_settings is not null)
            throw new InvalidOperationException("设置页已接入日志设置");

        _settings = settings;
        ShowApplied(settings.CurrentDirectory, settings.Retention);

        OnPropertyChanged(nameof(IsLogSettingsAvailable));
        RaiseStartupFallbackChanged();
        RaiseCommandsChanged();
    }

    // ==================== 驱动库 ====================

    /// <summary>
    /// 已接入驱动库目录设置（设计器中、配置库不可用时为 false）
    /// </summary>
    public bool IsLibraryAvailable => _library is not null;

    /// <summary>
    /// 输入框中的驱动库目录，点「保存」后保存，重启程序后生效
    /// </summary>
    public string LibraryDirectoryInput
    {
        get => _libraryDirectoryInput;
        set
        {
            if (SetProperty(ref _libraryDirectoryInput, value ?? string.Empty))
                OnInputChanged();
        }
    }

    /// <summary>
    /// 本次运行正在扫描的目录
    /// </summary>
    public string? CurrentLibraryDirectory => _library?.CurrentDirectory;

    /// <summary>
    /// 已保存的目录与本次运行使用的不同：说明重启后才生效
    /// </summary>
    public string? LibraryRestartNotice => _library is { } library
                                           && !string.Equals(library.NextDirectory, library.CurrentDirectory, StringComparison.OrdinalIgnoreCase)
        ? $"已保存为 {library.NextDirectory}，重启程序后生效；本次运行仍扫描 {library.CurrentDirectory}"
        : null;

    public bool HasLibraryRestartNotice => LibraryRestartNotice is not null;

    /// <summary>
    /// 启动时保存的驱动库目录不可用、本次运行改用默认目录的原因
    /// </summary>
    public string? LibraryFallback => _library?.StartupFallbackReason;

    public bool HasLibraryFallback => LibraryFallback is not null;

    /// <summary>
    /// 接入驱动库目录设置。须在 UI 线程上调用。
    /// </summary>
    public void AttachLibraryLocation(IDeviceLibraryLocation location)
    {
        _library = location ?? throw new ArgumentNullException(nameof(location));

        // 程序回填，不算用户改动
        _libraryDirectoryInput = location.NextDirectory;
        OnPropertyChanged(nameof(LibraryDirectoryInput));
        OnPropertyChanged(nameof(IsLibraryAvailable));
        OnPropertyChanged(nameof(CurrentLibraryDirectory));
        OnPropertyChanged(nameof(LibraryFallback));
        OnPropertyChanged(nameof(HasLibraryFallback));
        RaiseLibraryNoticeChanged();
        RaiseCommandsChanged();
    }

    private void RaiseLibraryNoticeChanged()
    {
        OnPropertyChanged(nameof(LibraryRestartNotice));
        OnPropertyChanged(nameof(HasLibraryRestartNotice));
    }

    /// <summary>
    /// 视图上的操作（打开文件夹、选择文件夹）失败时显示原因
    /// </summary>
    public void ReportError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        IsSaveError = true;
        SaveMessage = message;
    }

    private async Task SaveAsync()
    {
        if (_settings is not { } settings || _state != SettingsSaveState.Idle)
            return;

        // 留空不当作"恢复默认"：误清空输入框后点保存，不应悄悄把日志改写到别处
        var directory = _logDirectoryInput.Trim();
        if (directory.Length == 0)
        {
            ReportError("存储路径不能为空");
            return;
        }

        var retention = ReadRetention();
        if (retention.IsFailure)
        {
            ReportError(retention.Error.Message);
            return;
        }

        // 同样不把留空当作恢复默认：要用默认目录就填写或浏览到默认目录，保存时识别为"跟随默认"
        if (_library is not null && _libraryDirectoryInput.Trim().Length == 0)
        {
            ReportError("驱动库目录不能为空");
            return;
        }

        SetState(SettingsSaveState.Checking);
        IsSaveError = false;
        SaveMessage = "正在检查日志文件…";

        Result<LogPrunePreview> preview;
        try
        {
            preview = await settings.PreviewAsync(directory, retention.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
            preview = Result<LogPrunePreview>.Failure(new Error("LogRetention.PreviewFailed", ex.Message));
        }

        if (preview.IsFailure)
        {
            SetState(SettingsSaveState.Idle);
            ReportError(preview.Error.Message);
            return;
        }

        if (!preview.Value.IsEmpty)
        {
            // 什么都还没改：等确认。确认前改动任何输入都会取消这次保存
            _pending = new PendingSave(directory, retention.Value, preview.Value);
            SaveMessage = null;
            SetState(SettingsSaveState.AwaitingConfirmation);
            return;
        }

        await CommitAsync(settings, directory, retention.Value);
    }

    private async Task ConfirmDeletionAsync()
    {
        if (_settings is not { } settings || _state != SettingsSaveState.AwaitingConfirmation || _pending is not { } pending)
            return;

        await CommitAsync(settings, pending.Directory, pending.Retention);
    }

    private void CancelDeletion()
    {
        if (_state != SettingsSaveState.AwaitingConfirmation)
            return;

        SetState(SettingsSaveState.Idle);
        IsSaveError = false;
        SaveMessage = "已取消，设置未保存";
    }

    private async Task CommitAsync(ILogSettings settings, string directory, LogRetentionPolicy retention)
    {
        SetState(SettingsSaveState.Saving);
        IsSaveError = false;
        SaveMessage = "正在保存…";
        try
        {
            // 服务内部在线程池上做磁盘 I/O 且不抛出；这里的续体回到 UI 线程更新界面。
            // 设置未变化时服务只重新保存：启动时退回默认值的情况下，保存即确认改用当前设置
            var result = await settings.SaveAsync(directory, retention, CancellationToken.None);
            if (result.IsFailure)
            {
                ReportError(result.Error.Message);
                return;
            }

            var saved = result.Value;
            ShowApplied(saved.Directory, saved.Retention);
            _startupFallbackResolved = true;
            RaiseStartupFallbackChanged();
            SaveMessage = saved.Deleted.IsEmpty
                ? "设置已保存"
                : $"设置已保存，已删除 {saved.Deleted.FileCount} 个日志文件";
            _onLogDirectoryChanged?.Invoke(saved.Directory);

            // 驱动库目录只在改了时保存：它在下次启动时生效，与日志设置互不影响
            if (_library is { } library
                && !string.Equals(_libraryDirectoryInput.Trim(), library.NextDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var libraryResult = await library.SaveAsync(_libraryDirectoryInput, CancellationToken.None);
                if (libraryResult.IsFailure)
                {
                    ReportError($"日志设置已保存，但驱动库目录没有保存：{libraryResult.Error.Message}");
                    return;
                }

                _libraryDirectoryInput = libraryResult.Value;
                OnPropertyChanged(nameof(LibraryDirectoryInput));
                RaiseLibraryNoticeChanged();
                SaveMessage += "；驱动库目录重启程序后生效";
            }
        }
        finally
        {
            SetState(SettingsSaveState.Idle);
        }
    }

    private Result<LogRetentionPolicy> ReadRetention()
    {
        if (_retentionDaysInput is not { } days)
            return Result<LogRetentionPolicy>.Failure(new Error(LogRetentionErrors.RetentionDaysOutOfRangeCode, "请填写保存天数"));
        if (_fileSizeLimitInput is not { } megabytes)
            return Result<LogRetentionPolicy>.Failure(new Error(LogRetentionErrors.FileSizeLimitOutOfRangeCode, "请填写单个文件上限"));

        // 先在 decimal 上判断范围与是否为整数，再转 int：手输的超大数直接转换会溢出
        if (days != decimal.Truncate(days) || days < MinRetentionDays || days > MaxRetentionDays)
            return Result<LogRetentionPolicy>.Failure(LogRetentionErrors.RetentionDaysOutOfRange((int)Math.Clamp(days, int.MinValue, int.MaxValue)));
        if (megabytes != decimal.Truncate(megabytes) || megabytes < MinFileSizeLimitMegabytes || megabytes > MaxFileSizeLimitMegabytes)
            return Result<LogRetentionPolicy>.Failure(LogRetentionErrors.FileSizeLimitOutOfRange((int)Math.Clamp(megabytes, int.MinValue, int.MaxValue)));

        return LogRetentionPolicy.Create((int)days, (int)megabytes);
    }

    // 显示已生效的设置。直接写字段：这是程序回填，不算用户改动，不能触发取消确认
    private void ShowApplied(string directory, LogRetentionPolicy retention)
    {
        CurrentLogDirectory = directory;
        _logDirectoryInput = directory;
        _retentionDaysInput = retention.RetentionDays;
        _fileSizeLimitInput = retention.FileSizeLimitMegabytes;
        OnPropertyChanged(nameof(LogDirectoryInput));
        OnPropertyChanged(nameof(RetentionDaysInput));
        OnPropertyChanged(nameof(FileSizeLimitMegabytesInput));
    }

    // 等待确认时改了输入：确认的内容已不是现在的输入，取消这次保存
    private void OnInputChanged()
    {
        if (_state != SettingsSaveState.AwaitingConfirmation)
            return;

        SetState(SettingsSaveState.Idle);
        IsSaveError = false;
        SaveMessage = "设置有改动，请重新保存";
    }

    private void SetState(SettingsSaveState state)
    {
        if (state != SettingsSaveState.AwaitingConfirmation)
            _pending = null;

        if (_state == state)
            return;

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsAwaitingConfirmation));
        OnPropertyChanged(nameof(PendingDeletionText));
        RaiseCommandsChanged();
    }

    private void RaiseCommandsChanged()
    {
        SaveCommand.RaiseCanExecuteChanged();
        ConfirmDeletionCommand.RaiseCanExecuteChanged();
        CancelDeletionCommand.RaiseCanExecuteChanged();
    }

    private void RaiseStartupFallbackChanged()
    {
        OnPropertyChanged(nameof(StartupFallback));
        OnPropertyChanged(nameof(HasStartupFallback));
    }

    private static string DescribeDeletion(LogPrunePreview preview)
    {
        var range = preview.OldestDate == preview.NewestDate
            ? $"{preview.OldestDate:yyyy-MM-dd}"
            : $"{preview.OldestDate:yyyy-MM-dd} ～ {preview.NewestDate:yyyy-MM-dd}";
        return $"按新的设置，保存后将立即删除 {preview.FileCount} 个日志文件（{range}，共 {FormatSize(preview.TotalBytes)}），删除后无法恢复。";
    }

    private static string FormatSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb)
            return (bytes / gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= mb)
            return (bytes / mb).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        return Math.Max(1, (long)Math.Ceiling(bytes / kb)).ToString(CultureInfo.InvariantCulture) + " KB";
    }

    private sealed record PendingSave(string Directory, LogRetentionPolicy Retention, LogPrunePreview Preview);
}
