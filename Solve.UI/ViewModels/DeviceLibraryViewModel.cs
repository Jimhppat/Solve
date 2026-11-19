using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Domain.Common;

namespace Solve.UI.ViewModels;

/// <summary>
/// 设置页的「设备库」卡片：列出第三方驱动包及其状态，操作员在这里确认（启用）或撤销。
/// </summary>
/// <remarks>
/// 启用意味着下次启动时加载并执行包内代码，所以先在卡片里列出名称、版本、作者与指纹，再次确认后才写入；
/// 确认与撤销都在重启后生效。只能在 UI 线程上使用；设备库的磁盘操作经 Task.Run 放到线程池。
/// </remarks>
public sealed class DeviceLibraryViewModel : ViewModelBase
{
    private IDeviceLibrary? _library;
    private string _rootDirectory = string.Empty;
    private DriverPackageItemViewModel? _pendingApproval;
    private string? _message;
    private bool _isMessageError;

    public DeviceLibraryViewModel()
    {
        RescanCommand = new AsyncRelayCommand(RescanAsync, () => _library is not null);
        ConfirmApprovalCommand = new AsyncRelayCommand(ConfirmApprovalAsync, () => _pendingApproval is not null);
        CancelApprovalCommand = new RelayCommand(() => PendingApproval = null);
    }

    public bool IsAvailable => _library is not null;

    /// <summary>
    /// 设备库根目录（完整路径）
    /// </summary>
    public string RootDirectory
    {
        get => _rootDirectory;
        private set => SetProperty(ref _rootDirectory, value);
    }

    public ObservableCollection<DriverPackageItemViewModel> Packages { get; } = [];

    public bool HasPackages => Packages.Count > 0;

    public bool IsEmpty => IsAvailable && Packages.Count == 0;

    /// <summary>
    /// 等待再次确认启用的包；非空时卡片里显示确认框
    /// </summary>
    public DriverPackageItemViewModel? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (SetProperty(ref _pendingApproval, value))
            {
                OnPropertyChanged(nameof(IsApprovalPending));
                OnPropertyChanged(nameof(ApprovalText));
                ConfirmApprovalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsApprovalPending => _pendingApproval is not null;

    public string ApprovalText => _pendingApproval is not { } p
        ? string.Empty
        : $"启用「{p.Name}」{p.Version}（作者 {p.Author}，指纹 {p.FingerprintShort}）？" +
          "启用后，重启程序时会加载并执行这个驱动包中的代码。只启用来源可信的驱动包。";

    /// <summary>
    /// 最近一次操作的结果；为空时不显示
    /// </summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public bool IsMessageError
    {
        get => _isMessageError;
        private set => SetProperty(ref _isMessageError, value);
    }

    public AsyncRelayCommand RescanCommand { get; }
    public AsyncRelayCommand ConfirmApprovalCommand { get; }
    public RelayCommand CancelApprovalCommand { get; }

    /// <summary>
    /// 接入设备库，显示启动时的扫描结果。须在 UI 线程上调用。
    /// </summary>
    public void Attach(IDeviceLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        RootDirectory = library.RootDirectory;
        OnPropertyChanged(nameof(IsAvailable));
        RescanCommand.RaiseCanExecuteChanged();
        Show(library.Packages);
    }

    /// <summary>
    /// 由视图报告纯视图操作（打开目录）的失败
    /// </summary>
    public void ReportError(string message)
    {
        Message = message;
        IsMessageError = true;
    }

    private void Show(IReadOnlyList<DriverPackageInfo> packages)
    {
        Packages.Clear();
        foreach (var package in packages)
            Packages.Add(new DriverPackageItemViewModel(package, item => PendingApproval = item, RevokeAsync));

        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task RescanAsync()
    {
        if (_library is not { } library)
            return;

        PendingApproval = null;
        var result = await Task.Run(() => library.RescanAsync(CancellationToken.None));
        Apply(result, result.IsSuccess ? $"已重新扫描，共 {result.Value.Count} 个驱动包" : null);
    }

    private async Task ConfirmApprovalAsync()
    {
        var item = PendingApproval;
        PendingApproval = null;
        if (item is null || _library is not { } library)
            return;

        var result = await Task.Run(() => library.ApproveAsync(item.Id, item.Fingerprint, CancellationToken.None));
        Apply(result, $"已启用「{item.Name}」，重启程序后加载");
    }

    private async Task RevokeAsync(DriverPackageItemViewModel item)
    {
        if (_library is not { } library)
            return;

        var result = await Task.Run(() => library.RevokeAsync(item.Id, CancellationToken.None));
        Apply(result, $"已撤销「{item.Name}」，重启程序后不再加载");
    }

    private void Apply(Result<IReadOnlyList<DriverPackageInfo>> result, string? successMessage)
    {
        if (result.IsFailure)
        {
            ReportError(result.Error.Message);
            return;
        }

        Show(result.Value);
        Message = successMessage;
        IsMessageError = false;
    }
}

/// <summary>
/// 设备库卡片中的一行
/// </summary>
public sealed class DriverPackageItemViewModel
{
    public DriverPackageItemViewModel(
        DriverPackageInfo package,
        Action<DriverPackageItemViewModel> onApprove,
        Func<DriverPackageItemViewModel, Task> onRevoke)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(onApprove);
        ArgumentNullException.ThrowIfNull(onRevoke);

        Package = package;
        (StatusText, StatusColor) = package.Status switch
        {
            DriverPackageStatus.Loaded => ("已加载", "#10B981"),
            DriverPackageStatus.PendingApproval => ("待确认", "#F59E0B"),
            DriverPackageStatus.ApprovedRestartRequired => ("已启用，重启后加载", "#0EA5E9"),
            DriverPackageStatus.RevokedRestartRequired => ("已撤销，重启后停用", "#F59E0B"),
            DriverPackageStatus.Incompatible => ("版本不兼容", "#EF4444"),
            DriverPackageStatus.Invalid => ("无效", "#EF4444"),
            DriverPackageStatus.Conflict => ("冲突", "#EF4444"),
            DriverPackageStatus.LoadFailed => ("加载失败", "#EF4444"),
            _ => ("未知", "#94A3B8"),
        };

        ApproveCommand = new RelayCommand(() => onApprove(this), () => package.CanApprove);
        RevokeCommand = new AsyncRelayCommand(() => onRevoke(this), () => package.CanRevoke);
    }

    public DriverPackageInfo Package { get; }

    public string Id => Package.Id;

    public string Name => Package.Name;

    public string Version => Package.Version;

    public string Author => Package.Author;

    public string Fingerprint => Package.Fingerprint;

    /// <summary>
    /// 指纹前 16 位，四位一组，供操作员与驱动作者核对
    /// </summary>
    public string FingerprintShort => Package.Fingerprint.Length < 16
        ? Package.Fingerprint
        : string.Join(" ", Enumerable.Range(0, 4).Select(i => Package.Fingerprint.Substring(i * 4, 4)));

    public string DriversText => Package.DriverNames.Count == 0 ? "-" : string.Join("、", Package.DriverNames);

    public string StatusText { get; }

    public string StatusColor { get; }

    public string Problem => Package.Problem ?? string.Empty;

    public bool HasProblem => !string.IsNullOrEmpty(Package.Problem);

    public bool CanApprove => Package.CanApprove;

    public bool CanRevoke => Package.CanRevoke;

    public RelayCommand ApproveCommand { get; }

    public AsyncRelayCommand RevokeCommand { get; }
}
