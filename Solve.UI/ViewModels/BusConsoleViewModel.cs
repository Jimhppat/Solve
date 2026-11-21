using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Common;
using Solve.Core.Application.Devices;
using Solve.Core.Domain.Buses;

namespace Solve.UI.ViewModels;

/// <summary>
/// 总线控制台专属视图模型：收敛管理全站串口与以太网总线链路的 CRUD、状态轮询与物理连接调度
/// </summary>
public sealed class BusConsoleViewModel : ViewModelBase, IDisposable
{
    private IBusManagementService? _busService;
    private IDeviceService? _deviceService;
    private EventSubscription<BusRegistryEvent>? _registrySubscription;

    private bool _isEditModalOpen;
    private BusEditViewModel _editForm = new();
    private StartupNotice? _startupNotice;
    private BusItemViewModel? _pendingDelete;
    private int? _pendingDeleteDeviceCount;

    // 已打开模块配置窗口的总线 → 页模型；只在 UI 线程上访问
    private readonly Dictionary<BusId, BusDevicesViewModel> _modulePages = [];

    public BusConsoleViewModel()
    {
        // 预览器 / 设计模式下的初始示例数据
        if (Design.IsDesignMode)
        {
            Buses.Add(new BusItemViewModel("主控柜 PLC", "串口 RS-485", "COM3 · 115200 · 8N1", BusState.Connected, "收发正常"));
            Buses.Add(new BusItemViewModel("温控仪表", "串口 RS-232", "COM5 · 9600 · 8E1", BusState.Connected, "收发正常"));
            Buses.Add(new BusItemViewModel("视觉工位", "TCP 客户端", "192.168.10.21:502", BusState.Reconnecting, "第 3 次重连"));
            Buses.Add(new BusItemViewModel("称重模块", "TCP 客户端", "192.168.10.40:4001", BusState.Faulted, "重连次数耗尽，需人工复位"));
            RefreshCounts();
        }

        OpenAddDialogCommand = new RelayCommand(OpenAddDialog);
        CloseEditModalCommand = new RelayCommand(() => IsEditModalOpen = false);
        SaveEditModalCommand = new AsyncRelayCommand(SaveEditModalAsync);

        ConnectAllCommand = new RelayCommand(ConnectAll);
        DisconnectAllCommand = new AsyncRelayCommand(DisconnectAllAsync);
        RefreshCommand = new AsyncRelayCommand(ReloadBusesAsync);

        DismissStartupNoticeCommand = new RelayCommand(() => StartupNotice = null);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync);
    }

    public ObservableCollection<BusItemViewModel> Buses { get; } = [];

    public bool IsEditModalOpen
    {
        get => _isEditModalOpen;
        set => SetProperty(ref _isEditModalOpen, value);
    }

    public BusEditViewModel EditForm
    {
        get => _editForm;
        set => SetProperty(ref _editForm, value);
    }

    public int TotalCount => Buses.Count;
    public bool IsEmpty => Buses.Count == 0;
    public bool HasBuses => Buses.Count > 0;
    public int OnlineCount => Buses.Count(b => b.State == BusState.Connected);
    public int ReconnectingCount => Buses.Count(b => b.State is BusState.Reconnecting or BusState.Connecting);
    public int FaultedCount => Buses.Count(b => b.State is BusState.Faulted or BusState.SafeStop);

    public string StatusSummaryText =>
        $"共 {TotalCount} 条链路 · 在线 {OnlineCount} · 重连中 {ReconnectingCount} · 异常/停止 {FaultedCount}";

    // ==================== 启动问题横幅 ====================

    public StartupNotice? StartupNotice
    {
        get => _startupNotice;
        private set
        {
            if (SetProperty(ref _startupNotice, value))
                OnPropertyChanged(nameof(HasStartupNotice));
        }
    }

    public bool HasStartupNotice => _startupNotice is not null;

    public RelayCommand DismissStartupNoticeCommand { get; }

    /// <summary>
    /// 显示启动阶段的问题（可在任意线程调用）
    /// </summary>
    public void ShowStartupNotice(StartupNotice notice)
        => Dispatcher.UIThread.Post(() => StartupNotice = notice);

    // ==================== 删除二次确认 ====================

    /// <summary>
    /// 等待确认删除的总线；非空时显示确认弹窗
    /// </summary>
    public BusItemViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (SetProperty(ref _pendingDelete, value))
            {
                OnPropertyChanged(nameof(IsDeleteConfirmOpen));
                OnPropertyChanged(nameof(DeleteConfirmTitle));
                OnPropertyChanged(nameof(DeleteConfirmMessage));
            }
        }
    }

    public bool IsDeleteConfirmOpen => _pendingDelete is not null;

    public string DeleteConfirmTitle => $"删除总线「{_pendingDelete?.Name}」？";

    public string DeleteConfirmMessage
    {
        get
        {
            // 模块数异步读取：读到之前（或读取失败）也要明确告知模块会一起删除
            var devices = _pendingDeleteDeviceCount switch
            {
                null => "，挂在它上面的模块也会一起删除",
                0 => string.Empty,
                var count => $"，以及挂在它上面的 {count} 个模块",
            };

            // 接线随模块级联拆除：确认框里一并说明，避免操作员以为位置上的硬件关联还在
            var wires = _pendingDelete is { } bus && _deviceService is { } deviceService
                ? deviceService.CountWires(deviceService.Bindings.Get(bus.Id).Select(b => b.Device.Id).ToList())
                : 0;
            if (wires > 0)
                devices += $"（其上 {wires} 路接线一并拆除）";

            return _pendingDelete?.State is BusState.Connected or BusState.Connecting or BusState.Reconnecting
                ? $"该总线当前{_pendingDelete.StateText}，删除会先断开连接，然后永久删除它的配置{devices}。此操作无法撤销。"
                : $"将永久删除该总线的配置{devices}。此操作无法撤销。";
        }
    }

    public RelayCommand CancelDeleteCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand OpenAddDialogCommand { get; }
    public RelayCommand CloseEditModalCommand { get; }
    public AsyncRelayCommand SaveEditModalCommand { get; }
    public RelayCommand ConnectAllCommand { get; }
    public AsyncRelayCommand DisconnectAllCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    // ==================== 模块配置窗口 ====================

    /// <summary>
    /// 驱动库：模块配置窗口的「驱动库」按钮打开它。全程序共用一个，由主视图模型创建后设置
    /// </summary>
    public DeviceLibraryViewModel? DeviceLibrary { get; init; }

    /// <summary>
    /// 请求显示一条总线的模块配置窗口（在 UI 线程上触发）。同一条总线只有一个页模型：
    /// 已打开时再次请求传入的是同一个实例，由窗口宿主激活已有窗口，不重复创建。
    /// 窗口关闭后宿主须调用 <see cref="ReleaseModulePage"/>。
    /// </summary>
    public event EventHandler<BusDevicesViewModel>? ModuleWindowRequested;

    /// <summary>
    /// 窗口已关闭，释放该总线的页模型；之后再次打开会重新读取。须在 UI 线程上调用。
    /// </summary>
    public void ReleaseModulePage(BusDevicesViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        // 只释放仍登记着的同一实例：总线删除时已先行移除，防止误删同总线新开的页
        if (_modulePages.TryGetValue(page.BusId, out var current) && ReferenceEquals(current, page))
            _modulePages.Remove(page.BusId);
    }

    /// <summary>
    /// 接入设备配置服务，须在 UI 线程上调用。配置库不可用时不调用，模块配置入口点击后在说明栏提示原因。
    /// </summary>
    public void AttachDevices(IDeviceService deviceService)
        => _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));

    /// <summary>
    /// 初始化接入总线内核服务与事件通道
    /// </summary>
    public Task InitializeAsync(IBusManagementService busService)
    {
        _busService = busService ?? throw new ArgumentNullException(nameof(busService));

        // 快照与订阅原子完成：快照里的总线不会再收到 BusRegistered，之后新增的一定会收到
        var subscription = busService.SubscribeRegistry(out var current);
        _registrySubscription = subscription;
        _ = Task.Run(() => ListenRegistryEventsAsync(subscription.Reader));

        return Dispatcher.UIThread.InvokeAsync(() => ShowBuses(current)).GetTask();
    }

    public Task ReloadBusesAsync()
    {
        if (_busService is not null)
            ShowBuses(_busService.GetAllRuntimes());

        return Task.CompletedTask;
    }

    private void ShowBuses(IReadOnlyList<BusRuntime> runtimes)
    {
        foreach (var item in Buses)
            item.Dispose();

        Buses.Clear();

        foreach (var runtime in runtimes)
            Buses.Add(CreateItem(runtime));

        // 旧行已释放、不再跟随状态：已打开的模块配置窗口改绑到新行；总线已不在的直接关闭窗口
        foreach (var page in _modulePages.Values.ToList())
        {
            var item = Buses.FirstOrDefault(b => b.Id == page.BusId);
            if (item is not null)
            {
                page.Rebind(item);
            }
            else
            {
                _modulePages.Remove(page.BusId);
                page.RequestClose();
            }
        }

        RefreshCounts();
    }

    private BusItemViewModel CreateItem(BusRuntime runtime)
    {
        var item = new BusItemViewModel(runtime, OpenEditDialog, DeleteBus, OpenModules);
        item.PropertyChanged += (_, _) => RefreshCounts();
        return item;
    }

    private void OpenAddDialog()
    {
        EditForm.ResetForCreate();
        IsEditModalOpen = true;
    }

    private void OpenEditDialog(BusItemViewModel item)
    {
        if (item.Runtime is null) return;
        EditForm.LoadFrom(item.Runtime.Descriptor);
        IsEditModalOpen = true;
    }

    private async Task SaveEditModalAsync()
    {
        if (_busService is null)
        {
            EditForm.ErrorMessage = "系统内核尚未完全就绪，请稍后重试。";
            return;
        }

        var buildResult = EditForm.BuildDescriptor();
        if (buildResult.IsFailure)
            return;

        var descriptor = buildResult.Value;
        if (EditForm.IsCreateMode)
        {
            var created = await _busService.CreateBusAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
            if (created.IsFailure)
            {
                Dispatcher.UIThread.Post(() => EditForm.ErrorMessage = created.Error.Message);
                return;
            }
        }
        else
        {
            var updated = await _busService.UpdateBusAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
            if (updated.IsFailure)
            {
                Dispatcher.UIThread.Post(() => EditForm.ErrorMessage = updated.Error.Message);
                return;
            }
        }

        Dispatcher.UIThread.Post(() => IsEditModalOpen = false);
    }

    // 由命令在 UI 线程上调用；async void 与 AsyncRelayCommand.Execute 一致，意外异常交给 UI 线程的未处理异常保护
    private async void OpenModules(BusItemViewModel item)
    {
        if (_deviceService is null)
        {
            item.ShowError("模块配置不可用：配置库未就绪");
            return;
        }

        if (_modulePages.TryGetValue(item.Id, out var existing))
        {
            ModuleWindowRequested?.Invoke(this, existing);
            return;
        }

        var page = new BusDevicesViewModel(item, _deviceService) { Library = DeviceLibrary };
        _modulePages.Add(item.Id, page);
        ModuleWindowRequested?.Invoke(this, page);
        await page.LoadAsync();
    }

    // 行内删除按钮只打开确认弹窗，真正的删除在 ConfirmDeleteAsync 中执行；
    // 弹窗先显示，再在后台读取挂载模块数补全提示（读库是同步磁盘 I/O，不在 UI 线程上做）
    private async void DeleteBus(BusItemViewModel item)
    {
        _pendingDeleteDeviceCount = null;
        PendingDelete = item;

        if (_deviceService is null)
            return;

        var service = _deviceService;
        var counted = await Task.Run(() => service.CountAsync(item.Id, CancellationToken.None));

        // 等待期间弹窗可能已取消或换成另一条总线，只更新仍在确认中的那一条
        if (counted.IsSuccess && ReferenceEquals(PendingDelete, item))
        {
            _pendingDeleteDeviceCount = counted.Value;
            OnPropertyChanged(nameof(DeleteConfirmMessage));
        }
    }

    private async Task ConfirmDeleteAsync()
    {
        var item = PendingDelete;
        PendingDelete = null;
        if (item is null || _busService is null)
            return;

        // 仓储的 Async 方法实际是同步磁盘 I/O，按端口约定不在 UI 线程上执行
        var service = _busService;
        var deleted = await Task.Run(() => service.DeleteBusAsync(item.Id, CancellationToken.None));

        // 成功时行会随 BusUnregistered 事件移除；失败时行仍在，把原因显示在说明栏（显式回到 UI 线程）
        if (deleted.IsFailure)
            Dispatcher.UIThread.Post(() => item.ShowError($"删除失败：{deleted.Error.Message}"));
    }

    private void ConnectAll()
    {
        foreach (var bus in Buses.Where(b => b.CanConnect))
            bus.ConnectCommand?.Execute(null);
    }

    private async Task DisconnectAllAsync()
    {
        foreach (var bus in Buses.Where(b => b.CanDisconnect))
        {
            if (bus.DisconnectCommand is not null)
                await bus.DisconnectCommand.ExecuteAsync();
        }
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasBuses));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(ReconnectingCount));
        OnPropertyChanged(nameof(FaultedCount));
        OnPropertyChanged(nameof(StatusSummaryText));
    }

    // 退订（Dispose）或 BusManager 释放后队列完成，循环自然退出
    private async Task ListenRegistryEventsAsync(ChannelReader<BusRegistryEvent> reader)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var evt))
                Dispatcher.UIThread.Post(() => ApplyRegistryEvent(evt));
        }
    }

    private void ApplyRegistryEvent(BusRegistryEvent evt)
    {
        switch (evt)
        {
            // 去重：刷新按钮可能已经从 GetAllRuntimes 加载过这条总线
            case BusRegistered registered
                when Buses.All(b => b.Id != registered.BusId)
                     && _busService?.GetRuntime(registered.BusId) is { IsSuccess: true } found:
                Buses.Add(CreateItem(found.Value));
                RefreshCounts();
                break;

            case BusReconfigured reconfigured:
                Buses.FirstOrDefault(b => b.Id == reconfigured.BusId)?.RefreshDescriptor(reconfigured.Descriptor);
                break;

            case BusUnregistered unregistered:
                var removed = Buses.FirstOrDefault(b => b.Id == unregistered.BusId);
                // 总线删除后其模块已被级联删除，窗口不能再留着编辑一条不存在的总线
                if (_modulePages.Remove(unregistered.BusId, out var page))
                    page.RequestClose();

                if (removed is not null)
                {
                    removed.Dispose();
                    Buses.Remove(removed);
                    RefreshCounts();
                }
                break;
        }
    }

    public void Dispose()
    {
        _registrySubscription?.Dispose();
        foreach (var item in Buses)
            item.Dispose();
    }
}
