using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Devices;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.UI.ViewModels;

/// <summary>
/// 驱动库（独立窗口，从模块配置窗口的「驱动库」按钮打开）：扫描驱动库目录、从目录导入驱动包、查看包内驱动、信任并载入、撤销、忽略、逐个启停驱动。
/// </summary>
/// <remarks>
/// <para>信任会执行包内代码、停用会让模块停止通信，这两类操作都先弹出确认，写明后果再执行。</para>
/// <para>线程：只能在 UI 线程上使用；设备库操作（磁盘 I/O、载入程序集）经 Task.Run 放到线程池，带超时。
/// 任一操作进行期间所有操作按钮不可用，防止重复提交。</para>
/// </remarks>
public sealed class DeviceLibraryViewModel : ViewModelBase
{
    /// <summary>
    /// 单次操作的上限：扫描与导入要读完整个包（单包最大 64 MB），30 s 足够；超时说明磁盘或网络路径异常
    /// </summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    private IDeviceLibrary? _library;
    private IDeviceService? _devices;
    private string _rootDirectory = string.Empty;
    private bool _isBusy;
    private DriverPackageItemViewModel? _selectedPackage;
    private ConfirmationViewModel? _confirmation;
    private string? _message;
    private bool _isMessageError;

    public DeviceLibraryViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanOperate);
        ApproveCommand = new RelayCommand(RequestApprove, () => CanOperate && _selectedPackage?.Package.CanApprove == true);
        RevokeCommand = new RelayCommand(RequestRevoke, () => CanOperate && _selectedPackage?.Package.CanRevoke == true);
        IgnoreCommand = new AsyncRelayCommand(IgnoreAsync, () => CanOperate && _selectedPackage?.Package.CanIgnore == true);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => _confirmation is not null && !_isBusy);
        CancelConfirmCommand = new RelayCommand(() => Confirmation = null);
        OpenCommand = new RelayCommand(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// 请求显示驱动库窗口（在 UI 线程上触发）。窗口全程序只有一个，由主窗口持有：已打开时只激活
    /// </summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// 打开驱动库窗口。模块配置窗口的「驱动库」按钮与空分类处的入口都绑定它
    /// </summary>
    public RelayCommand OpenCommand { get; }

    /// <summary>
    /// 入口按钮的文字：有待确认的驱动包时直接写出数量，不必打开窗口才知道
    /// </summary>
    public string EntryText => PendingCount > 0 ? $"驱动库（{PendingCount} 个待确认）" : "驱动库";

    public bool IsAvailable => _library is not null;

    public string RootDirectory
    {
        get => _rootDirectory;
        private set => SetProperty(ref _rootDirectory, value);
    }

    public ObservableCollection<DriverPackageItemViewModel> Packages { get; } = [];

    public bool HasPackages => Packages.Count > 0;

    public bool IsEmpty => IsAvailable && Packages.Count == 0;

    /// <summary>
    /// 待操作员决定的包数（新发现、内容有变化）
    /// </summary>
    public int PendingCount => Packages.Count(p => p.Package.Status == DriverPackageStatus.PendingApproval);

    public string SummaryText => $"共 {Packages.Count} 个驱动包，待确认 {PendingCount} 个";

    public DriverPackageItemViewModel? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (!SetProperty(ref _selectedPackage, value))
                return;

            OnPropertyChanged(nameof(HasSelection));
            RaiseCommands();
        }
    }

    public bool HasSelection => _selectedPackage is not null;

    /// <summary>
    /// 任一设备库操作正在进行
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommands();
        }
    }

    /// <summary>
    /// 等待操作员再次确认的操作；非空时页面显示确认框
    /// </summary>
    public ConfirmationViewModel? Confirmation
    {
        get => _confirmation;
        private set
        {
            if (!SetProperty(ref _confirmation, value))
                return;

            OnPropertyChanged(nameof(IsConfirming));
            RaiseCommands();
        }
    }

    public bool IsConfirming => _confirmation is not null;

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

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand RevokeCommand { get; }
    public AsyncRelayCommand IgnoreCommand { get; }
    public AsyncRelayCommand ConfirmCommand { get; }
    public RelayCommand CancelConfirmCommand { get; }

    private bool CanOperate => _library is not null && !_isBusy && _confirmation is null;

    /// <summary>
    /// 「从目录导入」按钮可用（选择文件夹在视图中进行，不走命令）
    /// </summary>
    public bool CanImport => CanOperate;

    /// <summary>
    /// 接入设备库，显示启动时的扫描结果。须在 UI 线程上调用。
    /// </summary>
    public void Attach(IDeviceLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        RootDirectory = library.RootDirectory;
        OnPropertyChanged(nameof(IsAvailable));
        Show(library.Packages);
    }

    /// <summary>
    /// 接入模块服务：停用驱动前据此统计受影响的模块数。须在 UI 线程上调用。
    /// </summary>
    public void AttachDevices(IDeviceService devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        if (_library is not null)
            Show(_library.Packages);
    }

    /// <summary>
    /// 由视图报告纯视图操作（打开目录、选择文件夹）的失败
    /// </summary>
    public void ReportError(string message)
    {
        Message = message;
        IsMessageError = true;
    }

    /// <summary>
    /// 从视图选好的目录导入驱动包。设备库里已有同一包的不同内容时先确认是否替换。须在 UI 线程上调用。
    /// </summary>
    public async Task ImportAsync(string sourceDirectory)
    {
        if (!CanOperate || _library is not { } library)
            return;

        var result = await RunAsync(token => library.ImportAsync(sourceDirectory, overwrite: false, token));
        if (result.IsFailure && result.Error.Code == DeviceLibraryErrors.AlreadyExistsCode)
        {
            Confirmation = new ConfirmationViewModel(
                "替换驱动包",
                $"{result.Error.Message}用所选目录中的内容替换它吗？替换后需要重新信任；如果旧版本正在使用，重启程序后才会换成新版本。",
                "替换",
                IsDanger: true,
                async () => Apply(
                    await RunAsync(token => library.ImportAsync(sourceDirectory, overwrite: true, token)),
                    packages => $"已导入并替换，等待信任（{Describe(packages)}）"));
            return;
        }

        Apply(result, packages => $"已导入，等待信任（{Describe(packages)}）");
    }

    // ==================== 操作 ====================

    private async Task ScanAsync()
    {
        if (_library is not { } library)
            return;

        Apply(await RunAsync(library.RescanAsync), packages => $"扫描完成：{Describe(packages)}");
    }

    private void RequestApprove()
    {
        if (_selectedPackage is not { } item || _library is not { } library)
            return;

        var drivers = string.Join("、", item.Drivers.Select(d => d.Name));
        Confirmation = new ConfirmationViewModel(
            "信任并载入驱动包",
            $"信任「{item.Name}」{item.Version}（作者 {item.Author}，指纹 {item.FingerprintShort}）？" +
            $"信任后会立即载入并执行这个驱动包中的代码，驱动（{drivers}）出现在模块配置的驱动表中。只信任来源可靠的驱动包。",
            "信任并载入",
            IsDanger: true,
            async () =>
            {
                var result = await RunAsync(token => library.ApproveAsync(item.Id, item.Fingerprint, token));
                Apply(result, packages => packages.FirstOrDefault(p => p.Id == item.Id) switch
                {
                    { Status: DriverPackageStatus.Loaded } => $"已信任并载入「{item.Name}」，模块配置窗口中可以使用它的驱动",
                    { Status: DriverPackageStatus.ApprovedRestartRequired } => $"已信任「{item.Name}」的新版本，重启程序后替换正在使用的版本",
                    { Problem: { } problem } p => $"已信任「{item.Name}」，但未能载入：{p.Status}，{problem}",
                    _ => $"已信任「{item.Name}」",
                });
            });
    }

    private void RequestRevoke()
    {
        if (_selectedPackage is not { } item || _library is not { } library)
            return;

        var mounted = item.Drivers.Sum(d => d.Mounted);
        Confirmation = new ConfirmationViewModel(
            "撤销信任",
            $"撤销对「{item.Name}」的信任？它的驱动会立即从驱动表移除，" +
            (mounted > 0 ? $"使用这些驱动的 {mounted} 个模块将停止通信（停止轮询不会关闭设备输出，请先确认设备处于安全状态）。" : "目前没有模块使用它的驱动。") +
            "代码在重启前仍留在内存中，但不再被调用。",
            "撤销信任",
            IsDanger: true,
            async () => Apply(
                await RunAsync(token => library.RevokeAsync(item.Id, token)),
                _ => $"已撤销对「{item.Name}」的信任"));
    }

    private async Task IgnoreAsync()
    {
        if (_selectedPackage is not { } item || _library is not { } library)
            return;

        Apply(await RunAsync(token => library.IgnoreAsync(item.Id, item.Fingerprint, token)),
            _ => $"已忽略「{item.Name}」的这个版本，内容变化后会重新提示");
    }

    private async Task ToggleDriverAsync(DriverEntryItemViewModel driver)
    {
        if (!CanOperate || _library is not { } library)
            return;

        var enable = !driver.IsEnabled;
        if (enable || driver.Mounted == 0)
        {
            await SetDriverEnabledAsync(library, driver, enable);
            return;
        }

        Confirmation = new ConfirmationViewModel(
            "停用驱动",
            $"停用「{driver.Name}」？使用它的 {driver.Mounted} 个模块将停止通信。" +
            "停止轮询不会关闭设备输出，请先确认设备处于安全状态。之后可随时重新启用。",
            "停用",
            IsDanger: true,
            () => SetDriverEnabledAsync(library, driver, enable: false));
    }

    private async Task SetDriverEnabledAsync(IDeviceLibrary library, DriverEntryItemViewModel driver, bool enable)
        => Apply(await RunAsync(token => library.SetDriverEnabledAsync(driver.Id, enable, token)),
            _ => enable ? $"已启用「{driver.Name}」" : $"已停用「{driver.Name}」");

    private async Task ConfirmAsync()
    {
        if (_confirmation is not { } confirmation)
            return;

        Confirmation = null;
        await confirmation.Action();
    }

    /// <summary>
    /// 在线程池上执行一次设备库操作，带超时；期间窗口处于忙碌状态
    /// </summary>
    private async Task<Result<IReadOnlyList<DriverPackageInfo>>> RunAsync(
        Func<CancellationToken, Task<Result<IReadOnlyList<DriverPackageInfo>>>> operation)
    {
        IsBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            return await Task.Run(() => operation(timeout.Token));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(Result<IReadOnlyList<DriverPackageInfo>> result, Func<IReadOnlyList<DriverPackageInfo>, string> successMessage)
    {
        if (result.IsFailure)
        {
            ReportError(result.Error.Message);
            return;
        }

        Show(result.Value);
        Message = successMessage(result.Value);
        IsMessageError = false;
    }

    private static string Describe(IReadOnlyList<DriverPackageInfo> packages)
        => $"共 {packages.Count} 个驱动包，待确认 {packages.Count(p => p.Status == DriverPackageStatus.PendingApproval)} 个";

    private void Show(IReadOnlyList<DriverPackageInfo> packages)
    {
        var selectedKey = _selectedPackage?.Key;

        Packages.Clear();
        foreach (var package in packages)
        {
            var drivers = package.Drivers
                .Select(d => new DriverEntryItemViewModel(d, package.CanToggleDrivers, _devices?.Bindings.CountByDriver(d.Id) ?? 0, ToggleDriverAsync))
                .ToList();
            Packages.Add(new DriverPackageItemViewModel(package, drivers));
        }

        // 刷新后仍选中原来的包；原来的包不在了就选第一个（通常是待确认的），让右侧始终有内容
        SelectedPackage = Packages.FirstOrDefault(p => p.Key == selectedKey) ?? Packages.FirstOrDefault();

        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EntryText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        ScanCommand.RaiseCanExecuteChanged();
        ApproveCommand.RaiseCanExecuteChanged();
        RevokeCommand.RaiseCanExecuteChanged();
        IgnoreCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        foreach (var package in Packages)
        {
            foreach (var driver in package.Drivers)
                driver.SetOperable(CanOperate);
        }
    }
}

/// <summary>
/// 一次需要操作员再次确认的操作
/// </summary>
/// <param name="IsDanger">确认按钮用危险色（会执行第三方代码、会让模块停止通信、会替换文件）</param>
public sealed record ConfirmationViewModel(string Title, string Text, string ConfirmText, bool IsDanger, Func<Task> Action);

/// <summary>
/// 驱动库窗口左侧列表中的一个驱动包
/// </summary>
public sealed class DriverPackageItemViewModel
{
    public DriverPackageItemViewModel(DriverPackageInfo package, IReadOnlyList<DriverEntryItemViewModel> drivers)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(drivers);

        Package = package;
        Drivers = drivers;
        (StatusText, StatusColor) = package.Status switch
        {
            DriverPackageStatus.Loaded => ("已载入", "#10B981"),
            DriverPackageStatus.PendingApproval when package.WasApproved => ("内容有变化", "#F59E0B"),
            DriverPackageStatus.PendingApproval => ("新发现", "#F59E0B"),
            DriverPackageStatus.ApprovedRestartRequired => ("重启后生效", "#0EA5E9"),
            DriverPackageStatus.Revoked => ("已撤销信任", "#94A3B8"),
            DriverPackageStatus.Ignored => ("已忽略", "#94A3B8"),
            DriverPackageStatus.Incompatible => ("版本不兼容", "#EF4444"),
            DriverPackageStatus.Invalid => ("无效", "#EF4444"),
            DriverPackageStatus.Conflict => ("冲突", "#EF4444"),
            DriverPackageStatus.LoadFailed => ("载入失败", "#EF4444"),
            _ => ("未知", "#94A3B8"),
        };
    }

    public DriverPackageInfo Package { get; }

    /// <summary>
    /// 列表内唯一的键：包标识重复（冲突）时再加目录名区分
    /// </summary>
    public string Key => $"{Package.Id}@{Package.Directory}";

    public string Id => Package.Id;

    public string Name => Package.Name;

    public string Version => Package.Version;

    public string Author => Package.Author;

    public string Directory => Package.Directory;

    public string Fingerprint => Package.Fingerprint;

    /// <summary>
    /// 指纹前 16 位，四位一组，供操作员与驱动作者核对
    /// </summary>
    public string FingerprintShort => Package.Fingerprint.Length < 16
        ? Package.Fingerprint
        : string.Join(" ", Enumerable.Range(0, 4).Select(i => Package.Fingerprint.Substring(i * 4, 4)));

    public string Subtitle => $"{Package.Version} · {Package.Author} · 驱动 {Drivers.Count} 个";

    public IReadOnlyList<DriverEntryItemViewModel> Drivers { get; }

    public bool HasDrivers => Drivers.Count > 0;

    public string StatusText { get; }

    public string StatusColor { get; }

    public string Problem => Package.Problem ?? string.Empty;

    public bool HasProblem => !string.IsNullOrEmpty(Package.Problem);
}

/// <summary>
/// 驱动包中的一个驱动（驱动库窗口右侧的驱动表）
/// </summary>
public sealed class DriverEntryItemViewModel : ViewModelBase
{
    private readonly bool _canToggle;
    private bool _operable = true;

    public DriverEntryItemViewModel(
        DriverEntryInfo entry,
        bool canToggle,
        int mounted,
        Func<DriverEntryItemViewModel, Task> toggle)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(toggle);

        Entry = entry;
        Mounted = mounted;
        _canToggle = canToggle && entry.IsLoaded;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this), () => _canToggle && _operable);
        (StateText, StateColor) = (entry.IsLoaded, entry.IsEnabled, canToggle) switch
        {
            (true, true, true) => ("已启用", "#10B981"),
            (true, false, true) => ("已停用", "#94A3B8"),
            (true, _, false) => ("未使用", "#94A3B8"),
            _ => ("未载入", "#94A3B8"),
        };
    }

    public DriverEntryInfo Entry { get; }

    public DriverId Id => Entry.Id;

    public string Name => Entry.Name;

    public string CategoryText => Entry.Category.DisplayName();

    public string Manufacturer => Entry.Manufacturer.Length == 0 ? "-" : Entry.Manufacturer;

    public string Model => Entry.Model.Length == 0 ? "-" : Entry.Model;

    public string AddressText => Entry.MinAddress is { } min && Entry.MaxAddress is { } max ? $"{min} – {max}" : "-";

    public bool IsEnabled => Entry.IsEnabled;

    /// <summary>
    /// 全部总线上使用该驱动的模块数
    /// </summary>
    public int Mounted { get; }

    public string MountedText => Mounted == 0 ? "-" : $"{Mounted} 个";

    public string StateText { get; }

    public string StateColor { get; }

    public bool CanToggle => _canToggle;

    public string ToggleText => Entry.IsEnabled ? "停用" : "启用";

    public AsyncRelayCommand ToggleCommand { get; }

    internal void SetOperable(bool operable)
    {
        _operable = operable;
        ToggleCommand.RaiseCanExecuteChanged();
    }
}
