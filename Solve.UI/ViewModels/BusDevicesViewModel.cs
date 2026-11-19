using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Solve.Core.Application.Devices;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 一条总线的模块配置窗口：查看、新增、修改、删除挂在这条总线上的模块（领域中的设备）。只编辑配置，不访问硬件。
/// </summary>
/// <remarks>
/// 线程：命令由 UI 线程发起；存储调用（同步磁盘 I/O）经 Task.Run 放到线程池，await 后回到 UI 线程更新列表。
/// 保存与删除使用 AsyncRelayCommand，执行期间按钮不可再次点击，防止重复提交。
/// </remarks>
public sealed class BusDevicesViewModel : ViewModelBase
{
    private readonly IDeviceService? _service;
    private readonly BusId _busId;

    private BusItemViewModel _bus;
    private bool _isEditOpen;
    private DeviceItemViewModel? _pendingDelete;
    private string? _pageError;

    /// <summary>
    /// 设计模式 / 预览器专用构造函数
    /// </summary>
    public BusDevicesViewModel()
    {
        _bus = new BusItemViewModel("1 号线 RS-485", "串口", "COM3 · 115200 · 8N1", BusState.Disconnected, "通道已关闭，就绪");
        OpenAddCommand = new RelayCommand(() => { });
        CloseEditCommand = new RelayCommand(() => { });
        SaveCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        CancelDeleteCommand = new RelayCommand(() => { });
        ConfirmDeleteCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        if (Design.IsDesignMode)
        {
            var busId = BusId.New();
            Show(
            [
                new DeviceDescriptor(DeviceId.New(), busId, "程控电源", 1) { Remark = "1 号柜" },
                new DeviceDescriptor(DeviceId.New(), busId, "温控表", 2),
            ]);
        }
    }

    public BusDevicesViewModel(BusItemViewModel bus, IDeviceService service)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(service);

        _bus = bus;
        _busId = bus.Id;
        _service = service;

        OpenAddCommand = new RelayCommand(OpenAdd);
        CloseEditCommand = new RelayCommand(() => IsEditOpen = false);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync);
    }

    /// <summary>
    /// 请求关闭承载本页的窗口（所属总线已被删除）。由窗口订阅，在 UI 线程上触发。
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 页头显示的总线（名称、链路参数、实时状态），与总线列表共用同一个行视图模型
    /// </summary>
    public BusItemViewModel Bus
    {
        get => _bus;
        private set => SetProperty(ref _bus, value);
    }

    public BusId BusId => _busId;

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    public bool IsEmpty => Devices.Count == 0 && !HasPageError;

    public bool HasDevices => Devices.Count > 0;

    /// <summary>
    /// 读取或删除失败的原因，显示在模块表上方；为空表示正常
    /// </summary>
    public string? PageError
    {
        get => _pageError;
        private set
        {
            if (SetProperty(ref _pageError, value))
            {
                OnPropertyChanged(nameof(HasPageError));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool HasPageError => !string.IsNullOrEmpty(_pageError);

    // ==================== 新增 / 编辑弹窗 ====================

    public DeviceEditViewModel EditForm { get; } = new();

    public bool IsEditOpen
    {
        get => _isEditOpen;
        private set => SetProperty(ref _isEditOpen, value);
    }

    // ==================== 删除二次确认 ====================

    public DeviceItemViewModel? PendingDelete
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

    public string DeleteConfirmTitle => $"删除模块「{_pendingDelete?.Name}」？";

    public string DeleteConfirmMessage => $"将从总线「{Bus.Name}」上删除该模块的配置。此操作无法撤销。";

    public RelayCommand OpenAddCommand { get; }
    public RelayCommand CloseEditCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }

    /// <summary>
    /// 总线列表刷新后行视图模型会重建（旧行已退订状态事件），页头改绑到新行，状态才能继续跟随。须在 UI 线程上调用。
    /// </summary>
    public void Rebind(BusItemViewModel bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (bus.Id != _busId)
            throw new ArgumentException($"只能改绑到同一条总线 {_busId}，实际为 {bus.Id}。", nameof(bus));

        Bus = bus;
    }

    /// <summary>
    /// 所属总线已被删除：关闭窗口。须在 UI 线程上调用。
    /// </summary>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 从存储读取这条总线上的模块。须在 UI 线程上调用。
    /// </summary>
    public async Task LoadAsync()
    {
        if (_service is null)
            return;

        var service = _service;
        var busId = _busId;
        var listed = await Task.Run(() => service.ListAsync(busId, CancellationToken.None));
        if (listed.IsFailure)
        {
            PageError = $"读取模块失败：{listed.Error.Message}";
            return;
        }

        PageError = null;
        Show(listed.Value);
    }

    private void Show(IReadOnlyList<DeviceDescriptor> devices)
    {
        Devices.Clear();
        foreach (var device in devices)
            Devices.Add(new DeviceItemViewModel(device, OpenEdit, item => PendingDelete = item));

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDevices));
    }

    private void OpenAdd()
    {
        EditForm.ResetForCreate(SuggestAddress());
        IsEditOpen = true;
    }

    private void OpenEdit(DeviceItemViewModel item)
    {
        EditForm.LoadFrom(item.Device);
        IsEditOpen = true;
    }

    /// <summary>
    /// 新增时预填最小的空闲地址：现场多数按 1、2、3… 顺序拨码；全部占用时仍填下限，由保存时的冲突检查报错
    /// </summary>
    private int SuggestAddress()
    {
        var used = Devices.Select(d => d.Address).ToHashSet();
        for (var address = DeviceDescriptor.MinAddress; address <= DeviceDescriptor.MaxAddress; address++)
        {
            if (!used.Contains(address))
                return address;
        }

        return DeviceDescriptor.MinAddress;
    }

    private async Task SaveAsync()
    {
        if (_service is null)
            return;

        var built = EditForm.BuildDescriptor(_busId);
        if (built.IsFailure)
            return;

        var service = _service;
        var device = built.Value;
        var isCreate = EditForm.IsCreateMode;
        var saved = await Task.Run(() => isCreate
            ? service.AddAsync(device, CancellationToken.None)
            : service.UpdateAsync(device, CancellationToken.None));

        if (saved.IsFailure)
        {
            EditForm.ErrorMessage = saved.Error.Message;
            return;
        }

        IsEditOpen = false;
        await LoadAsync();
    }

    private async Task ConfirmDeleteAsync()
    {
        var item = PendingDelete;
        PendingDelete = null;
        if (item is null || _service is null)
            return;

        var service = _service;
        var deleted = await Task.Run(() => service.DeleteAsync(item.Device, CancellationToken.None));

        // 已被删除也重新读取，让列表与存储一致；其他失败原因显示在列表上方
        if (deleted.IsFailure && deleted.Error.Code != DeviceErrors.StorageNotFoundCode)
        {
            PageError = $"删除失败：{deleted.Error.Message}";
            return;
        }

        await LoadAsync();
    }
}
