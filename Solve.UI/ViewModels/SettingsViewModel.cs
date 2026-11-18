using System;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 系统设置页。页面上的修改只在点「保存」时统一生效；目前包含运行日志的存储路径。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly Action<string>? _onLogDirectoryChanged;
    private ILogDirectorySettings? _logDirectory;
    private string? _currentLogDirectory;
    private string _logDirectoryInput = string.Empty;
    private string? _saveMessage;
    private bool _isSaveError;
    private bool _isSaving;
    private bool _startupFallbackResolved;

    /// <summary>
    /// XAML 设计器使用：未接入任何设置，保存不可用
    /// </summary>
    public SettingsViewModel() : this(onLogDirectoryChanged: null)
    {
    }

    /// <param name="onLogDirectoryChanged">日志目录切换成功后在 UI 线程上调用，由主窗口宿主同步到日志页的「打开日志目录」</param>
    public SettingsViewModel(Action<string>? onLogDirectoryChanged)
    {
        _onLogDirectoryChanged = onLogDirectoryChanged;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _logDirectory is not null && !_isSaving);
    }

    public string PageTitle => "系统设置";

    /// <summary>
    /// 保存页面上的全部设置
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

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

    // ==================== 运行日志存储路径 ====================

    /// <summary>
    /// 已接入日志目录设置（设计器中为 false）
    /// </summary>
    public bool IsLogDirectoryAvailable => _logDirectory is not null;

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
        set => SetProperty(ref _logDirectoryInput, value ?? string.Empty);
    }

    /// <summary>
    /// 启动时保存的目录不可用、本次运行改用默认目录的原因
    /// </summary>
    public string? LogDirectoryStartupFallback => _startupFallbackResolved ? null : _logDirectory?.StartupFallbackReason;

    public bool HasLogDirectoryStartupFallback => LogDirectoryStartupFallback is not null;

    /// <summary>
    /// 接入日志目录设置。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachLogDirectory(ILogDirectorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_logDirectory is not null)
            throw new InvalidOperationException("设置页已接入日志目录设置");

        _logDirectory = settings;
        CurrentLogDirectory = settings.CurrentDirectory;
        LogDirectoryInput = settings.CurrentDirectory;

        OnPropertyChanged(nameof(IsLogDirectoryAvailable));
        RaiseStartupFallbackChanged();
        SaveCommand.RaiseCanExecuteChanged();
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
        if (_logDirectory is not { } settings || _isSaving)
            return;

        // 留空不当作"恢复默认"：误清空输入框后点保存，不应悄悄把日志改写到别处
        var directory = _logDirectoryInput.Trim();
        if (directory.Length == 0)
        {
            ReportError("存储路径不能为空");
            return;
        }

        _isSaving = true;
        SaveCommand.RaiseCanExecuteChanged();
        IsSaveError = false;
        SaveMessage = "正在保存…";
        try
        {
            // 服务内部在线程池上做磁盘 I/O；这里的续体回到 UI 线程更新界面。
            // 路径未变化时服务只重新保存设置、不切换：启动时退回默认目录的情况下，保存即确认改用当前目录
            var result = await settings.ChangeDirectoryAsync(directory, CancellationToken.None);
            if (result.IsFailure)
            {
                ReportError(result.Error.Message);
                return;
            }

            CurrentLogDirectory = result.Value;
            LogDirectoryInput = result.Value;
            _startupFallbackResolved = true;
            RaiseStartupFallbackChanged();
            SaveMessage = "设置已保存";
            _onLogDirectoryChanged?.Invoke(result.Value);
        }
        finally
        {
            _isSaving = false;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseStartupFallbackChanged()
    {
        OnPropertyChanged(nameof(LogDirectoryStartupFallback));
        OnPropertyChanged(nameof(HasLogDirectoryStartupFallback));
    }
}
