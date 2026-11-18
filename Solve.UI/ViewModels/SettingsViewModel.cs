using System;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 系统设置页。目前包含运行日志目录的查看与切换。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly Action<string>? _onLogDirectoryChanged;
    private ILogDirectorySettings? _logDirectory;
    private string? _currentLogDirectory;
    private string _logDirectoryInput = string.Empty;
    private string? _logDirectoryMessage;
    private bool _isLogDirectoryError;
    private bool _isChangingLogDirectory;

    /// <summary>
    /// XAML 设计器使用：未接入日志目录设置，按钮不可用
    /// </summary>
    public SettingsViewModel() : this(onLogDirectoryChanged: null)
    {
    }

    /// <param name="onLogDirectoryChanged">日志目录切换成功后在 UI 线程上调用，由主窗口宿主同步到日志页的「打开日志目录」</param>
    public SettingsViewModel(Action<string>? onLogDirectoryChanged)
    {
        _onLogDirectoryChanged = onLogDirectoryChanged;
        ApplyLogDirectoryCommand = new AsyncRelayCommand(
            () => ChangeLogDirectoryAsync(_logDirectoryInput),
            () => CanChangeLogDirectory && !string.IsNullOrWhiteSpace(_logDirectoryInput));
        RestoreDefaultLogDirectoryCommand = new AsyncRelayCommand(
            () => ChangeLogDirectoryAsync(null),
            () => CanChangeLogDirectory);
    }

    public string PageTitle => "系统设置";

    // ==================== 运行日志目录 ====================

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

    public string? DefaultLogDirectory => _logDirectory?.DefaultDirectory;

    /// <summary>
    /// 启动时保存的目录不可用、本次运行改用默认目录的原因
    /// </summary>
    public string? LogDirectoryStartupFallback => _logDirectory?.StartupFallbackReason;

    public bool HasLogDirectoryStartupFallback => LogDirectoryStartupFallback is not null;

    /// <summary>
    /// 输入框中的目标目录
    /// </summary>
    public string LogDirectoryInput
    {
        get => _logDirectoryInput;
        set
        {
            if (SetProperty(ref _logDirectoryInput, value ?? string.Empty))
                ApplyLogDirectoryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 最近一次操作的结果说明
    /// </summary>
    public string? LogDirectoryMessage
    {
        get => _logDirectoryMessage;
        private set
        {
            if (SetProperty(ref _logDirectoryMessage, value))
                OnPropertyChanged(nameof(HasLogDirectoryMessage));
        }
    }

    public bool HasLogDirectoryMessage => _logDirectoryMessage is not null;

    public bool IsLogDirectoryError
    {
        get => _isLogDirectoryError;
        private set => SetProperty(ref _isLogDirectoryError, value);
    }

    /// <summary>
    /// 切换到输入框中的目录
    /// </summary>
    public AsyncRelayCommand ApplyLogDirectoryCommand { get; }

    /// <summary>
    /// 切换回默认目录
    /// </summary>
    public AsyncRelayCommand RestoreDefaultLogDirectoryCommand { get; }

    // 两个命令共用一条切换通道：任一执行中时两个按钮都不可点
    private bool CanChangeLogDirectory => _logDirectory is not null && !_isChangingLogDirectory;

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
        OnPropertyChanged(nameof(DefaultLogDirectory));
        OnPropertyChanged(nameof(LogDirectoryStartupFallback));
        OnPropertyChanged(nameof(HasLogDirectoryStartupFallback));
        RaiseLogDirectoryCommandsChanged();
    }

    /// <summary>
    /// 视图上的操作（打开文件夹、选择文件夹）失败时显示原因
    /// </summary>
    public void ReportLogDirectoryError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        IsLogDirectoryError = true;
        LogDirectoryMessage = message;
    }

    private async Task ChangeLogDirectoryAsync(string? directory)
    {
        if (_logDirectory is not { } settings || _isChangingLogDirectory)
            return;

        _isChangingLogDirectory = true;
        RaiseLogDirectoryCommandsChanged();
        IsLogDirectoryError = false;
        LogDirectoryMessage = "正在切换…";
        try
        {
            // 服务内部在线程池上做磁盘 I/O；这里的续体回到 UI 线程更新界面
            var result = await settings.ChangeDirectoryAsync(directory, CancellationToken.None);
            if (result.IsFailure)
            {
                IsLogDirectoryError = true;
                LogDirectoryMessage = result.Error.Message;
                return;
            }

            CurrentLogDirectory = result.Value;
            LogDirectoryInput = result.Value;
            LogDirectoryMessage = "已应用：之后的日志写入该目录，原目录中的日志保留在原处";
            _onLogDirectoryChanged?.Invoke(result.Value);
        }
        finally
        {
            _isChangingLogDirectory = false;
            RaiseLogDirectoryCommandsChanged();
        }
    }

    private void RaiseLogDirectoryCommandsChanged()
    {
        ApplyLogDirectoryCommand.RaiseCanExecuteChanged();
        RestoreDefaultLogDirectoryCommand.RaiseCanExecuteChanged();
    }
}
