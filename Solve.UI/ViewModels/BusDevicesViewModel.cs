using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Solve.Core.Application.Devices;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

namespace Solve.UI.ViewModels;

/// <summary>
/// 一条总线的模块配置窗口：左栏按硬件分类浏览驱动，右上用选中的驱动批量添加模块，右下查看、修改、勾选删除已挂载模块。
/// 一条总线可挂任意多个、任意驱动的模块（只要地址不重复），随时增删改，不受总线连接状态限制。只编辑配置，不访问硬件。
/// </summary>
/// <remarks>
/// 线程：命令由 UI 线程发起；存储调用（同步磁盘 I/O）经 Task.Run 放到线程池，await 后回到 UI 线程更新列表。
/// 添加、保存、删除使用 AsyncRelayCommand，执行期间按钮不可再次点击，防止重复提交。
/// </remarks>
public sealed class BusDevicesViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(500);

    private DispatcherTimer? _statusTimer;
    private bool _disposed;

    private const string UnknownDriverName = "未知驱动";

    private readonly IDeviceService? _service;
    private readonly BusId _busId;
    private IReadOnlyList<DriverItemViewModel> _allDrivers;
    private IReadOnlyList<DriverCategoryItem> _categories;
    private bool _reloadPending;

    private BusItemViewModel _bus;
    private DriverCategoryItem? _selectedCategory;
    private DriverItemViewModel? _selectedDriver;
    private bool _isEditOpen;
    private IReadOnlyList<DeviceItemViewModel>? _pendingDelete;
    private string? _pageError;

    /// <summary>
    /// 设计模式 / 预览器专用构造函数
    /// </summary>
    public BusDevicesViewModel()
    {
        _bus = new BusItemViewModel("1 号线 RS-485", "串口", "COM3 · 115200 · 8N1", BusState.Disconnected, "通道已关闭，就绪");
        _allDrivers =
        [
            new DriverItemViewModel(new DriverDescriptor(DriverId.Of("generic.module"), "通用模块", HardwareCategory.General,
                "不含通信协议，只登记模块名称与地址", DeviceDescriptor.MinAddress, DeviceDescriptor.MaxAddress)),
        ];
        _categories = BuildCategories(_allDrivers);
        AddCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        CloseEditCommand = new RelayCommand(() => { });
        SaveEditCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        DeleteCheckedCommand = new RelayCommand(() => { });
        CancelDeleteCommand = new RelayCommand(() => { });
        ConfirmDeleteCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        SelectedCategory = Categories[0];
        SelectedDriver = Drivers.FirstOrDefault();

        if (Design.IsDesignMode)
        {
            var busId = BusId.New();
            Show(
            [
                new DeviceDescriptor(DeviceId.New(), busId, _allDrivers[0].Driver.Id, "通用模块-01", 1) { Remark = "1 号柜" },
                new DeviceDescriptor(DeviceId.New(), busId, _allDrivers[0].Driver.Id, "通用模块-02", 2),
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
        _allDrivers = BuildDrivers(service);
        _categories = BuildCategories(_allDrivers);

        AddCommand = new AsyncRelayCommand(AddAsync, () => BatchForm.HasDriver);
        CloseEditCommand = new RelayCommand(() => IsEditOpen = false);
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync);
        DeleteCheckedCommand = new RelayCommand(
            () => PendingDelete = Devices.Where(d => d.IsChecked).ToList(),
            () => CheckedCount > 0);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync);

        // 默认选中第一个驱动：窗口一打开就能直接添加，不必先去左栏点选
        SelectedCategory = Categories[0];
        SelectedDriver = Drivers.FirstOrDefault();

        // 设备库载入、撤销、停用驱动后驱动表随之刷新；绑定变化（驱动可用性变了）后模块的驱动状态随之刷新。
        // 两个事件都来自后台线程，这里只投递到 UI 线程
        service.Drivers.Changed += OnCatalogChanged;
        service.Bindings.Changed += OnBindingsChanged;
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

    /// <summary>
    /// 驱动库入口（左栏标题的「驱动库」按钮、空分类处的「打开驱动库」）。全程序共用一个；设计器中为 null，入口不显示
    /// </summary>
    public DeviceLibraryViewModel? Library { get; init; }

    public bool HasLibrary => Library is not null;

    // ==================== 左栏：分类与驱动表 ====================

    /// <summary>
    /// "全部" + 每个硬件分类（驱动数为 0 的分类也列出，表示该类硬件的驱动尚未接入）
    /// </summary>
    public IReadOnlyList<DriverCategoryItem> Categories
    {
        get => _categories;
        private set => SetProperty(ref _categories, value);
    }

    /// <summary>
    /// 切换分类只筛选驱动表，不影响右侧表单已选的驱动：看别的分类时也能继续添加
    /// </summary>
    public DriverCategoryItem? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
                FilterDrivers();
        }
    }

    private void FilterDrivers()
    {
        var formDriver = BatchForm.Driver;
        Drivers.Clear();
        foreach (var driver in _allDrivers.Where(d => _selectedCategory?.Category is null || d.Driver.Category == _selectedCategory.Category))
            Drivers.Add(driver);

        OnPropertyChanged(nameof(HasDrivers));

        // 表单所用的驱动仍在新列表里就保持高亮（驱动表重建后按标识找回），不在就只取消高亮
        _selectedDriver = formDriver is null ? null : Drivers.FirstOrDefault(d => d.Driver.Id == formDriver.Driver.Id);
        OnPropertyChanged(nameof(SelectedDriver));
    }

    public ObservableCollection<DriverItemViewModel> Drivers { get; } = [];

    public bool HasDrivers => Drivers.Count > 0;

    /// <summary>
    /// 驱动表的选中项。点选驱动即换成该驱动添加；列表筛选造成的取消选中（null）不清空表单。
    /// </summary>
    public DriverItemViewModel? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (!SetProperty(ref _selectedDriver, value) || value is null)
                return;

            BatchForm.SelectDriver(value, SuggestStartAddress(value.Driver));
            AddCommand.RaiseCanExecuteChanged();
        }
    }

    // ==================== 右上：添加模块 ====================

    public ModuleBatchFormViewModel BatchForm { get; } = new();

    public AsyncRelayCommand AddCommand { get; }

    // ==================== 右下：已挂载模块 ====================

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    public bool IsEmpty => Devices.Count == 0 && !HasPageError;

    public bool HasDevices => Devices.Count > 0;

    public string DeviceCountText => $"已挂载模块（{Devices.Count}）";

    public int CheckedCount => Devices.Count(d => d.IsChecked);

    public string DeleteCheckedText => $"删除所选（{CheckedCount}）";

    /// <summary>
    /// 表头"全选"：全部勾选为 true、全未勾选为 false、部分勾选为 null（三态）
    /// </summary>
    public bool? IsAllChecked
    {
        get
        {
            var count = CheckedCount;
            return count == 0 ? false : count == Devices.Count ? true : null;
        }
        set
        {
            // 三态复选框从"部分"点击时传来 null，按全选处理
            var check = value != false;
            foreach (var device in Devices)
                device.IsChecked = check;
        }
    }

    public RelayCommand DeleteCheckedCommand { get; }

    /// <summary>
    /// 读取或删除失败的原因，显示在窗口上方；为空表示正常
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

    // ==================== 编辑弹窗 ====================

    public DeviceEditViewModel EditForm { get; } = new();

    public bool IsEditOpen
    {
        get => _isEditOpen;
        private set => SetProperty(ref _isEditOpen, value);
    }

    public RelayCommand CloseEditCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }

    // ==================== 删除二次确认（单个或勾选的多个） ====================

    public IReadOnlyList<DeviceItemViewModel>? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (SetProperty(ref _pendingDelete, value is { Count: > 0 } ? value : null))
            {
                OnPropertyChanged(nameof(IsDeleteConfirmOpen));
                OnPropertyChanged(nameof(DeleteConfirmTitle));
                OnPropertyChanged(nameof(DeleteConfirmMessage));
            }
        }
    }

    public bool IsDeleteConfirmOpen => _pendingDelete is not null;

    public string DeleteConfirmTitle => _pendingDelete switch
    {
        [var single] => $"删除模块「{single.Name}」？",
        { Count: var count } => $"删除选中的 {count} 个模块？",
        _ => string.Empty,
    };

    public string DeleteConfirmMessage => _pendingDelete is { Count: > 1 }
        ? $"将从总线「{Bus.Name}」上删除这些模块的配置。此操作无法撤销。"
        : $"将从总线「{Bus.Name}」上删除该模块的配置。此操作无法撤销。";

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

        // 绑定在增删改成功后已由服务重新计算；这里只读取内存中的快照，不触发绑定
        var bindings = service.Bindings.Get(busId).ToDictionary(b => b.Device.Id);
        Show(listed.Value, bindings);
        RefreshStatuses();

        // 通信状态与读数按固定频率从内存快照刷新（不随每次轮询刷新，限制界面重绘频率）
        if (_statusTimer is null && !_disposed)
        {
            _statusTimer = new DispatcherTimer(StatusRefreshInterval, DispatcherPriority.Background, (_, _) => RefreshStatuses());
            _statusTimer.Start();
        }
    }

    /// <summary>
    /// 窗口关闭：停止刷新。须在 UI 线程上调用，幂等。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (_service is not null)
        {
            _service.Drivers.Changed -= OnCatalogChanged;
            _service.Bindings.Changed -= OnBindingsChanged;
        }

        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void RefreshStatuses()
    {
        if (_service is null)
            return;

        foreach (var item in Devices)
            item.ApplyStatus(_service.Statuses.Get(item.Device.Id));
    }

    /// <summary>
    /// 有实现（来自驱动包）的驱动带上参数声明，添加与编辑表单据此生成参数输入项。
    /// 先取一次 All 再逐个查实现：目录可能在运行中被整份替换，这样至少驱动列表本身是同一份快照
    /// </summary>
    private static IReadOnlyList<DriverItemViewModel> BuildDrivers(IDeviceService service)
        => service.Drivers.All
            .Select(d => new DriverItemViewModel(d, service.Drivers.TryGetImplementation(d.Id, out var implementation) ? implementation.Info.Parameters : null))
            .ToList();

    // 后台线程：只投递
    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ReloadDrivers);

    // 后台线程：只关心本总线；连续多次变化合并成一次重新读取
    private void OnBindingsChanged(object? sender, BusId busId)
    {
        if (busId == _busId)
            Dispatcher.UIThread.Post(ScheduleReload);
    }

    /// <summary>
    /// 稍后重新读取模块列表（合并连续的多次请求）。须在 UI 线程上调用。
    /// </summary>
    private void ScheduleReload()
    {
        if (_disposed || _reloadPending)
            return;

        _reloadPending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _reloadPending = false;
            if (_disposed)
                return;

            // async void 回调：异常必须在这里处理，否则 UI 线程未处理异常会触发全站安全跳闸
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                PageError = $"刷新模块列表失败：{ex.Message}";
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 驱动目录变化后重建驱动表与分类计数，保持当前分类与表单所用驱动的高亮。须在 UI 线程上调用。
    /// </summary>
    private void ReloadDrivers()
    {
        if (_disposed || _service is null)
            return;

        var category = _selectedCategory?.Category;
        _allDrivers = BuildDrivers(_service);
        Categories = BuildCategories(_allDrivers);
        _selectedCategory = Categories.FirstOrDefault(c => c.Category == category) ?? Categories[0];
        OnPropertyChanged(nameof(SelectedCategory));
        FilterDrivers();

        // 模块行引用的驱动项也要换成新的（名称、参数声明可能变了，或驱动已不可用）：重新读取模块列表
        ScheduleReload();
    }

    private static IReadOnlyList<DriverCategoryItem> BuildCategories(IReadOnlyList<DriverItemViewModel> drivers)
    {
        var items = new List<DriverCategoryItem> { new(null, "全部", drivers.Count) };
        foreach (var category in HardwareCategories.All)
            items.Add(new DriverCategoryItem(category, category.DisplayName(), drivers.Count(d => d.Driver.Category == category)));

        return items;
    }

    private void Show(IReadOnlyList<DeviceDescriptor> devices, IReadOnlyDictionary<DeviceId, DriverBinding>? bindings = null)
    {
        Devices.Clear();
        foreach (var device in devices)
        {
            // 绑定按模块 Id 对应，并要求配置一致：库里的模块刚被改过而绑定尚未刷新时，显示为未绑定而不是旧状态
            var binding = bindings is not null && bindings.TryGetValue(device.Id, out var found) && found.Device == device ? found : null;
            Devices.Add(new DeviceItemViewModel(
                device, DriverOf(device), binding, OpenEdit, item => PendingDelete = [item], OnCheckedChanged));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(DeviceCountText));
        OnCheckedChanged();

        // 列表变了，起始地址换成当前驱动的下一个空闲地址，便于接着添加
        if (BatchForm.Driver is { } driver)
            BatchForm.AfterAdded(SuggestStartAddress(driver.Driver));
    }

    private void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(DeleteCheckedText));
        OnPropertyChanged(nameof(IsAllChecked));
        DeleteCheckedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 模块所用驱动；驱动已从程序中移除时给一个"未知驱动"占位项（地址按绝对范围），仍可编辑、可换成其他驱动
    /// </summary>
    private DriverItemViewModel DriverOf(DeviceDescriptor device)
        => _allDrivers.FirstOrDefault(d => d.Driver.Id == device.Driver)
           ?? new DriverItemViewModel(new DriverDescriptor(device.Driver, UnknownDriverName, HardwareCategory.General,
               string.Empty, DeviceDescriptor.MinAddress, DeviceDescriptor.MaxAddress));

    /// <summary>
    /// 驱动地址范围内最小的空闲地址：现场多数按 1、2、3… 顺序拨码；全部占用时仍填下限，由添加时的冲突检查报错
    /// </summary>
    private int SuggestStartAddress(DriverDescriptor driver)
    {
        var used = Devices.Select(d => d.Address).ToHashSet();
        for (var address = driver.MinAddress; address <= driver.MaxAddress; address++)
        {
            if (!used.Contains(address))
                return address;
        }

        return driver.MinAddress;
    }

    private async Task AddAsync()
    {
        if (_service is null)
            return;

        var built = BatchForm.BuildRequest(_busId);
        if (built.IsFailure)
            return;

        var service = _service;
        var request = built.Value;
        var added = await Task.Run(() => service.AddBatchAsync(request, CancellationToken.None));
        if (added.IsFailure)
        {
            BatchForm.ErrorMessage = added.Error.Message;
            return;
        }

        await LoadAsync();
    }

    private void OpenEdit(DeviceItemViewModel item)
    {
        // 原驱动在目录里就用目录里的全部驱动；已被移除时把占位项放在最前，不改驱动也能保存
        IReadOnlyList<DriverItemViewModel> choices = _allDrivers.Contains(item.Driver)
            ? _allDrivers
            : [item.Driver, .. _allDrivers];

        EditForm.LoadFrom(item.Device, choices, item.Driver);
        IsEditOpen = true;
    }

    private async Task SaveEditAsync()
    {
        if (_service is null)
            return;

        var built = EditForm.BuildDescriptor();
        if (built.IsFailure)
            return;

        var service = _service;
        var device = built.Value;
        var saved = await Task.Run(() => service.UpdateAsync(device, CancellationToken.None));
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
        var items = PendingDelete;
        PendingDelete = null;
        if (items is null || _service is null)
            return;

        var service = _service;
        var devices = items.Select(i => i.Device).ToList();
        var deleted = await Task.Run(() => service.DeleteAsync(devices, CancellationToken.None));

        // 失败时整批回滚，原因显示在窗口上方；成功后重新读取（已被别处删掉的也一并从列表消失）
        if (deleted.IsFailure)
        {
            PageError = $"删除失败：{deleted.Error.Message}";
            return;
        }

        await LoadAsync();
    }
}
