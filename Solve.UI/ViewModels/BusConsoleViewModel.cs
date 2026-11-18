using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;

namespace Solve.UI.ViewModels;

/// <summary>
/// 总线控制台专属视图模型：收敛管理全站串口与以太网总线链路的 CRUD、状态轮询与物理连接调度
/// </summary>
public sealed class BusConsoleViewModel : ViewModelBase, IDisposable
{
    private IBusManagementService? _busService;
    private BusManager? _manager;
    private readonly CancellationTokenSource _cts = new();

    private bool _isEditModalOpen;
    private BusEditViewModel _editForm = new();

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

    public RelayCommand OpenAddDialogCommand { get; }
    public RelayCommand CloseEditModalCommand { get; }
    public AsyncRelayCommand SaveEditModalCommand { get; }
    public RelayCommand ConnectAllCommand { get; }
    public AsyncRelayCommand DisconnectAllCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// 初始化接入总线内核服务与事件通道
    /// </summary>
    public Task InitializeAsync(IBusManagementService busService, BusManager manager)
    {
        _busService = busService ?? throw new ArgumentNullException(nameof(busService));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        _ = Task.Run(ListenRegistryEventsAsync);

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await ReloadBusesAsync();
        });
    }

    public Task ReloadBusesAsync()
    {
        if (_busService is null) return Task.CompletedTask;

        foreach (var item in Buses)
            item.Dispose();

        Buses.Clear();

        var runtimes = _busService.GetAllRuntimes();
        foreach (var runtime in runtimes)
        {
            var itemVm = new BusItemViewModel(runtime, OpenEditDialog, DeleteBus);
            itemVm.PropertyChanged += (_, _) => RefreshCounts();
            Buses.Add(itemVm);
        }

        RefreshCounts();
        return Task.CompletedTask;
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

    private async void DeleteBus(BusItemViewModel item)
    {
        if (_busService is null) return;
        var deleted = await _busService.DeleteBusAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            Dispatcher.UIThread.Post(() => item.Note = $"删除失败: {deleted.Error.Message}");
        }
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

    private async Task ListenRegistryEventsAsync()
    {
        if (_manager is null) return;
        var reader = _manager.RegistryEvents;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        switch (evt)
                        {
                            case BusRegistered registered:
                                if (!Buses.Any(b => b.Id == registered.BusId) && _manager.TryGet(registered.BusId, out var rt))
                                {
                                    var item = new BusItemViewModel(rt, OpenEditDialog, DeleteBus);
                                    item.PropertyChanged += (_, _) => RefreshCounts();
                                    Buses.Add(item);
                                    RefreshCounts();
                                }
                                break;

                            case BusReconfigured reconfigured:
                                var target = Buses.FirstOrDefault(b => b.Id == reconfigured.BusId);
                                target?.RefreshDescriptor(reconfigured.Descriptor);
                                break;

                            case BusUnregistered unregistered:
                                var removed = Buses.FirstOrDefault(b => b.Id == unregistered.BusId);
                                if (removed is not null)
                                {
                                    removed.Dispose();
                                    Buses.Remove(removed);
                                    RefreshCounts();
                                }
                                break;
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit on dispose
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        foreach (var item in Buses)
            item.Dispose();
    }
}
