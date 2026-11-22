using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.UI.ViewModels;

/// <summary>
/// 硬件映射页：左边位置树，中间选中位置的接线（<see cref="WiringPanelViewModel"/>），右边硬件通道（按总线 → 模块逐级选择）。
/// </summary>
/// <remarks>
/// <para>一个位置就是拓扑里的一个节点：在树上选中位置，再在右边点某路通道的"关联"，这路通道就接到该位置上（向下生效）。
/// 右边每路通道都显示它的使用状态（未接 / 已接 / 共用）与映射到的位置，点"定位"跳到那个位置；中间的接线点"定位通道"跳到右边的通道。</para>
/// <para>硬件多时不平铺：一次只列出一个模块的通道。勾选按通道记录，切换总线、模块、筛选后不丢（映射草稿可以跨模块取通道）。</para>
/// <para>只列出总线上有启用通道的模块：没用到的设备、在模块配置里停用的通道不出现。</para>
/// <para>位置结构在"设备拓扑"页维护，本页只读拓扑；每次进入本页时按当前拓扑刷新（<see cref="Refresh"/>）。
/// 只能在 UI 线程上使用；写库在线程池上完成。</para>
/// </remarks>
public sealed class HardwareMappingViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 左栏（位置树）与右栏（硬件通道）的默认宽度与可拖动范围；中间接线区占满其余宽度
    /// </summary>
    public const double DefaultTreeWidth = 280, MinTreeWidth = 220, MaxTreeWidth = 420;

    public const double DefaultHardwareWidth = 400, MinHardwareWidth = 340, MaxHardwareWidth = 560;

    /// <summary>
    /// 两侧栏宽的设置键，值为"左栏宽,右栏宽"（整数像素）
    /// </summary>
    public const string LayoutSettingKey = "ui.hardware_mapping.columns";

    // 页面级的已展开节点：节点 Id 终身不变，树重建后同一节点照样展开
    private readonly HashSet<TopologyNodeId> _expanded = [];

    // 硬件列表中勾选的通道（按通道定位）：列表重建（接线变化、切换模块、筛选）后勾选不丢
    private readonly HashSet<ChannelRef> _checkedChannels = [];

    // 每条总线上次看的模块：切回这条总线时回到它
    private readonly Dictionary<BusId, DeviceId> _lastModuleByBus = [];

    private ITopologyService? _topology;
    private WiringInspector? _inspector;
    private IAppSettingsStore? _layoutStore;
    private EquipmentTopology? _bound;
    private IReadOnlyList<TopologyTreeItemViewModel> _roots = [];
    private TopologyTreeItemViewModel? _selectedItem;
    private IReadOnlyList<MappingChannelViewModel> _allChannels = [];
    private IReadOnlyList<MappingModuleOption> _allModules = [];
    private IReadOnlyList<MappingBusOption> _buses = [];
    private MappingBusOption? _selectedBus;
    private BusId? _selectedBusId;
    private IReadOnlyList<MappingModuleOption> _modules = [];
    private MappingModuleOption? _selectedModule;
    private string _moduleSearch = string.Empty;
    private ChannelFilterOption _channelFilter = ChannelFilterOption.All[0];
    private IReadOnlyList<MappingChannelViewModel> _hardware = [];
    private MappingChannelViewModel? _selectedChannel;
    private double _treeWidth = DefaultTreeWidth;
    private double _hardwareWidth = DefaultHardwareWidth;
    private string _purposeInput = string.Empty;
    private bool _busy;
    private string? _message;
    private bool _isError;
    private bool _disposed;
    private bool _isDraftMode;

    public HardwareMappingViewModel()
    {
        Draft = new MappingDraftViewModel(CheckedChannels, () => _purposeInput, OnDraftMapped);
        SelectVisibleChannelsCommand = new RelayCommand(() => SetVisibleChannels(true));
        ClearChannelsCommand = new RelayCommand(ClearCheckedChannels);
        Wiring.LocateChannelRequested += OnLocateChannelRequested;
    }

    public string PageTitle => "硬件映射";

    /// <summary>
    /// 选中位置的接线：查看、批量接线、修改、拆除、下发命令
    /// </summary>
    public WiringPanelViewModel Wiring { get; } = new();

    /// <summary>
    /// 映射草稿：取位置 → 取通道 → 组装预览 → 确认
    /// </summary>
    public MappingDraftViewModel Draft { get; }

    /// <summary>
    /// 中栏显示映射草稿（否则显示选中位置的接线）；草稿模式下硬件通道可以勾选
    /// </summary>
    public bool IsDraftMode
    {
        get => _isDraftMode;
        set
        {
            if (!SetProperty(ref _isDraftMode, value))
                return;

            OnPropertyChanged(nameof(IsWiringMode));
            foreach (var row in _allChannels)
                row.IsSelectable = value;
            Draft.SetScope(_selectedItem?.Node, _bound);
        }
    }

    public bool IsWiringMode => !_isDraftMode;

    /// <summary>
    /// 勾选当前列表中可见的全部通道（配合筛选使用）
    /// </summary>
    public RelayCommand SelectVisibleChannelsCommand { get; }

    /// <summary>
    /// 取消全部勾选（包括其他模块上的）
    /// </summary>
    public RelayCommand ClearChannelsCommand { get; }

    // ==================== 栏宽 ====================

    /// <summary>
    /// 左栏（位置树）宽度
    /// </summary>
    public double TreeWidth
    {
        get => _treeWidth;
        private set => SetProperty(ref _treeWidth, value);
    }

    /// <summary>
    /// 右栏（硬件通道）宽度
    /// </summary>
    public double HardwareWidth
    {
        get => _hardwareWidth;
        private set => SetProperty(ref _hardwareWidth, value);
    }

    /// <summary>
    /// 拖动分隔条后记下两侧栏宽（限制在可拖动范围内），并写入设置，下次启动沿用
    /// </summary>
    public void SetColumnWidths(double treeWidth, double hardwareWidth)
    {
        if (!ApplyColumnWidths(treeWidth, hardwareWidth) || _layoutStore is not { } store)
            return;

        var value = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_treeWidth)},{Math.Round(_hardwareWidth)}");
        _ = SaveLayoutAsync(store, value);
    }

    /// <summary>
    /// 接入设置存储：读取上次的栏宽。须在 UI 线程上调用。
    /// </summary>
    public void AttachLayoutSettings(IAppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _layoutStore = store;
        _ = LoadLayoutAsync(store);
    }

    // ==================== 位置树 ====================

    public IReadOnlyList<TopologyTreeItemViewModel> Roots
    {
        get => _roots;
        private set => SetProperty(ref _roots, value);
    }

    public TopologyTreeItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            Wiring.ShowNode(value?.Node);
            if (_isDraftMode)
                Draft.SetScope(value?.Node, _bound);
            Message = null;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(CanMapHere));
            MarkHere();
        }
    }

    /// <summary>
    /// 当前位置：层级与位置编码，如"层位 C01-L02"；未选中或选中系统根时为 null
    /// </summary>
    public string? SelectedTitle => _selectedItem?.Node is { Level: not TopologyLevel.System } node
        ? $"{node.Level.DisplayName()} {node.Address}{(node.Name is { } name ? $"（{name}）" : string.Empty)}"
        : null;

    /// <summary>
    /// 选中了可接线的位置（系统根不接线）
    /// </summary>
    public bool CanMapHere => SelectedTitle is not null && !_busy;

    public bool IsConfigured => _bound is not null;

    public bool IsAvailable => _topology is not null && _inspector is not null;

    public string EmptyText => _topology is null || _inspector is null
        ? "配置库不可用或尚未就绪"
        : "尚未建档：请先在「设备拓扑」页建立位置结构";

    public bool ShowEmpty => !IsAvailable || !IsConfigured;

    // ==================== 硬件通道：总线 → 模块 → 通道 ====================

    /// <summary>
    /// 有可映射通道的总线，如"新设备链路 · COM3（2 个模块）"
    /// </summary>
    public IReadOnlyList<MappingBusOption> Buses
    {
        get => _buses;
        private set => SetProperty(ref _buses, value);
    }

    public MappingBusOption? SelectedBus
    {
        get => _selectedBus;
        set
        {
            if (!SetProperty(ref _selectedBus, value))
                return;

            // 下拉框换列表时会先送来 null：不当作操作员换了总线，记住的选择不变
            if (value is not null)
                _selectedBusId = value.Id;
            RebuildModules();
        }
    }

    /// <summary>
    /// 按模块名或地址筛选当前总线上的模块
    /// </summary>
    public string ModuleSearch
    {
        get => _moduleSearch;
        set
        {
            if (SetProperty(ref _moduleSearch, value ?? string.Empty))
                RebuildModules();
        }
    }

    /// <summary>
    /// 当前总线上（按搜索筛选后）的模块，每行带"已接 x / y"
    /// </summary>
    public IReadOnlyList<MappingModuleOption> Modules
    {
        get => _modules;
        private set => SetProperty(ref _modules, value);
    }

    public MappingModuleOption? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (!SetProperty(ref _selectedModule, value))
                return;

            if (value is not null)
                _lastModuleByBus[value.Bus] = value.Id;
            OnPropertyChanged(nameof(HasSelectedModule));
            RebuildVisible();
        }
    }

    public bool HasSelectedModule => _selectedModule is not null;

    public IReadOnlyList<ChannelFilterOption> ChannelFilters => ChannelFilterOption.All;

    /// <summary>
    /// 通道筛选：全部 / 未接 / 已接（1 个位置）/ 共用（多个位置）
    /// </summary>
    public ChannelFilterOption ChannelFilter
    {
        get => _channelFilter;
        set
        {
            if (SetProperty(ref _channelFilter, value ?? ChannelFilterOption.All[0]))
                RebuildVisible();
        }
    }

    /// <summary>
    /// 选中模块上（按筛选）的通道
    /// </summary>
    public IReadOnlyList<MappingChannelViewModel> Hardware
    {
        get => _hardware;
        private set
        {
            if (SetProperty(ref _hardware, value))
            {
                OnPropertyChanged(nameof(HasHardware));
                OnPropertyChanged(nameof(ShowNoMatch));
            }
        }
    }

    /// <summary>
    /// 列表中选中（定位到）的通道：列表自动滚动到它
    /// </summary>
    public MappingChannelViewModel? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    /// <summary>
    /// 全部总线、全部模块的通道（勾选、定位按它查找）
    /// </summary>
    public IReadOnlyList<MappingChannelViewModel> AllChannels => _allChannels;

    public bool HasHardware => _hardware.Count > 0;

    public bool HasNoHardware => IsAvailable && _allChannels.Count == 0;

    /// <summary>
    /// 选中的模块上没有符合筛选的通道
    /// </summary>
    public bool ShowNoMatch => _selectedModule is not null && _hardware.Count == 0;

    public string CheckedCountText => $"已选 {_checkedChannels.Count} 路";

    /// <summary>
    /// 点"关联"时一并写入的用途（如 VBAT），可不填
    /// </summary>
    public string PurposeInput
    {
        get => _purposeInput;
        set => SetProperty(ref _purposeInput, value ?? string.Empty);
    }

    public int MaxPurposeLength => Wire.MaxPurposeLength;

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message is not null;

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    /// <summary>
    /// 在右边显示这路通道：切到它的总线与模块、筛选恢复为全部并选中它。不在硬件列表里（模块已删除或通道已停用）时返回 null
    /// </summary>
    public MappingChannelViewModel? RevealChannel(ChannelRef channel)
    {
        var row = _allChannels.FirstOrDefault(r => r.Channel == channel);
        if (row is null || _allModules.FirstOrDefault(m => m.Id == channel.Device) is not { } module)
            return null;

        _lastModuleByBus[module.Bus] = module.Id;
        _channelFilter = ChannelFilterOption.All[0];
        OnPropertyChanged(nameof(ChannelFilter));
        _moduleSearch = string.Empty;
        OnPropertyChanged(nameof(ModuleSearch));
        ShowBus(_buses.FirstOrDefault(b => b.Id == module.Bus));
        SelectedChannel = row;
        return row;
    }

    // ==================== 接入与刷新 ====================

    /// <summary>
    /// 接入拓扑服务（只读位置结构）。须在 UI 线程上调用。
    /// </summary>
    public void AttachTopology(ITopologyService topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        _topology = topology;
        Refresh();
    }

    /// <summary>
    /// 接入接线运行视图与命令服务。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachWiring(WiringInspector inspector, DeviceCommandService? commands = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (_inspector is not null)
            throw new InvalidOperationException("硬件映射页已接入接线服务");

        _inspector = inspector;
        Wiring.Attach(inspector, commands);
        Draft.Attach(inspector);
        inspector.Wiring.Changed += OnWiringChanged;
        Refresh();
    }

    /// <summary>
    /// 按当前拓扑与接线刷新（进入本页时调用）：拓扑变了重建树（选中与展开按节点 Id 保留），硬件列表重新读取。
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
            return;

        var topology = _topology?.Current;
        if (!ReferenceEquals(topology, _bound))
        {
            var keep = _selectedItem?.Node.Id;
            _bound = topology;
            if (topology is null)
            {
                Roots = [];
                SelectedItem = null;
            }
            else
            {
                if (_expanded.Count == 0)
                    _expanded.Add(topology.Root.Id);
                Roots = [new TopologyTreeItemViewModel(topology.Root, _expanded)];
                SelectedItem = Find(topology, keep) ?? Roots[0];
            }

            OnPropertyChanged(nameof(IsConfigured));
        }
        else
        {
            // 同一份拓扑：接线可能已在别处改过，接线面板跟着刷新
            Wiring.ShowNode(_selectedItem?.Node);
        }

        if (_isDraftMode)
            Draft.SetScope(_selectedItem?.Node, _bound);
        RebuildHardware();
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasNoHardware));
    }

    public void Dispose()
    {
        _disposed = true;
        Wiring.LocateChannelRequested -= OnLocateChannelRequested;
        if (_inspector is not null)
            _inspector.Wiring.Changed -= OnWiringChanged;
        Wiring.Dispose();
    }

    // ==================== 关联与定位 ====================

    private async Task MapAsync(MappingChannelViewModel row)
    {
        if (_inspector is not { } inspector || _selectedItem?.Node is not { Level: not TopologyLevel.System } node || _busy || row.IsMappedAt(node.Id))
            return;

        var channel = row.Channel;
        var purpose = _purposeInput;
        SetBusy(true);
        Message = null;
        try
        {
            Result result;
            try
            {
                var plan = await Task.Run(() => inspector.Wiring.PlanAdd(node.Id, channel, purpose, null));
                result = plan.IsSuccess
                    ? await inspector.Wiring.ApplyAsync(plan.Value, CancellationToken.None)
                    : Result.Failure(plan.Error);
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
                result = Result.Failure(new Error("Wiring.MapFailed", ex.Message));
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return;
            }

            ShowMessage($"已将 {row.NameText} 关联到 {node.Address}");
            RebuildHardware();
            Wiring.ShowNode(node);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 在树上选中这路通道映射到的位置（展开它的全部上级）
    /// </summary>
    private void Locate(MappingChannelViewModel row)
    {
        if (row.MappedNode is not { } id || _bound is not { } topology)
            return;

        SelectedItem = Find(topology, id) ?? _selectedItem;
    }

    /// <summary>
    /// 中间接线的"定位通道"：在右边显示它接的那路通道
    /// </summary>
    private void OnLocateChannelRequested(ChannelRef channel)
    {
        if (RevealChannel(channel) is null)
            ShowMessage("这路通道不在硬件列表里：模块可能已删除，或通道已在模块配置里停用。", isError: true);
    }

    // ==================== 内部 ====================

    private void RebuildHardware()
    {
        if (_inspector is not { } inspector)
        {
            _allChannels = [];
            _allModules = [];
            Buses = [];
            ShowBus(null);
            return;
        }

        var map = inspector.Wiring.Current;
        var all = new List<MappingChannelViewModel>();
        var modules = new List<MappingModuleOption>();
        var buses = new List<(BusId Id, string Name, string? Link, int Modules)>();
        var available = new HashSet<ChannelRef>();
        foreach (var module in inspector.Wiring.ListModules())
        {
            var moduleText = $"{module.BusName} · {module.Device.Name}";
            var channels = new List<MappingChannelViewModel>();
            foreach (var group in module.Channels)
            {
                foreach (var index in group.Enabled())
                {
                    // 一路通道可以接到多个位置：已映射的通道照样能再关联、能勾选进映射草稿
                    var channel = new ChannelRef(module.Device.Id, group.Key, index);
                    available.Add(channel);
                    var mapped = new List<(TopologyNode Node, Wire Wire)>();
                    foreach (var wire in map.GetByChannel(channel))
                    {
                        if (map.Topology is { } topology && topology.TryGetNode(wire.Node, out var node))
                            mapped.Add((node, wire));
                    }

                    channels.Add(new MappingChannelViewModel(channel, moduleText, group.ChannelName(index), group.Kind.DisplayName(), mapped,
                        MapAsync, Locate, CanMapTo, OnChannelCheckedChanged)
                    {
                        IsSelectable = _isDraftMode,
                        Kind = group.Kind,
                    });
                }
            }

            if (channels.Count == 0)
                continue;

            all.AddRange(channels);
            modules.Add(new MappingModuleOption(module.Device.Id, module.BusId, $"{module.Device.Name} · 地址 {module.Device.Address}", channels));
            var busIndex = buses.FindIndex(b => b.Id == module.BusId);
            if (busIndex < 0)
                buses.Add((module.BusId, module.BusName, module.BusLink, 1));
            else
                buses[busIndex] = buses[busIndex] with { Modules = buses[busIndex].Modules + 1 };
        }

        // 已不存在的通道不再算勾选（映射草稿确认后接上的通道由 OnDraftMapped 取消勾选）
        _checkedChannels.IntersectWith(available);
        foreach (var row in all)
            row.SetChecked(_checkedChannels.Contains(row.Channel));

        _allChannels = all;
        _allModules = modules;
        MarkHere();
        OnPropertyChanged(nameof(AllChannels));
        OnPropertyChanged(nameof(HasNoHardware));
        OnPropertyChanged(nameof(CheckedCountText));

        // 下拉框换列表时可能先把选中项置空：随后按记住的总线恢复
        var keepBus = _selectedBusId;
        Buses = buses.Select(b => new MappingBusOption(b.Id, $"{b.Name}{(b.Link is { } link ? $" · {link}" : string.Empty)}（{b.Modules} 个模块）")).ToList();
        _selectedBusId = keepBus;
        ShowBus(_buses.FirstOrDefault(b => b.Id == keepBus) ?? _buses.FirstOrDefault());
        Draft.OnExternalChanged();
    }

    /// <summary>
    /// 切到总线并重建它的模块列表。不经过属性的相等判断：重建后的总线选项与旧的按值相等，但模块与通道是新的
    /// </summary>
    private void ShowBus(MappingBusOption? bus)
    {
        _selectedBus = bus;
        if (bus is not null)
            _selectedBusId = bus.Id;
        OnPropertyChanged(nameof(SelectedBus));
        RebuildModules();
    }

    private void RebuildModules()
    {
        var search = _moduleSearch.Trim();
        var modules = _selectedBus is { } bus
            ? _allModules.Where(m => m.Bus == bus.Id && (search.Length == 0 || m.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToList()
            : [];

        var keep = _selectedBus is { } current && _lastModuleByBus.TryGetValue(current.Id, out var last) ? last : (DeviceId?)null;
        Modules = modules;
        var selected = modules.FirstOrDefault(m => m.Id == keep) ?? modules.FirstOrDefault();
        if (ReferenceEquals(selected, _selectedModule))
            RebuildVisible();
        else
            SelectedModule = selected;
    }

    private void RebuildVisible()
    {
        Hardware = _selectedModule is { } module
            ? module.Channels.Where(_channelFilter.Accepts).ToList()
            : [];
        if (_selectedChannel is not null && !_hardware.Contains(_selectedChannel))
            SelectedChannel = null;
    }

    /// <summary>
    /// 映射草稿用的通道：勾选的通道（可以跨总线、跨模块；已映射的也可以，一路通道可以接到多个位置），按总线、模块、通道顺序
    /// </summary>
    private IReadOnlyList<MappingChannelViewModel> CheckedChannels()
        => _allChannels.Where(r => r.IsChecked).ToList();

    /// <summary>
    /// 映射草稿确认后：接上的通道取消勾选，免得下一次组装又用到它们
    /// </summary>
    private void OnDraftMapped(IReadOnlyCollection<ChannelRef> channels)
    {
        _checkedChannels.ExceptWith(channels);
        foreach (var row in _allChannels.Where(r => channels.Contains(r.Channel)))
            row.SetChecked(false);
        OnPropertyChanged(nameof(CheckedCountText));
        Draft.OnExternalChanged();
    }

    /// <summary>
    /// 这路通道能关联到当前位置：选中了可接线的位置，且这路通道还没接在这个位置上
    /// </summary>
    private bool CanMapTo(MappingChannelViewModel row)
        => CanMapHere && _selectedItem?.Node is { } node && !row.IsMappedAt(node.Id);

    /// <summary>
    /// 标出接在当前选中位置上的通道，并刷新"关联"能否点
    /// </summary>
    private void MarkHere()
    {
        var node = _selectedItem?.Node.Id;
        foreach (var row in _allChannels)
        {
            row.IsHere = node is { } id && row.IsMappedAt(id);
            row.RaiseCanMap();
        }
    }

    private void OnChannelCheckedChanged(MappingChannelViewModel row)
    {
        if (row.IsChecked)
            _checkedChannels.Add(row.Channel);
        else
            _checkedChannels.Remove(row.Channel);
        OnPropertyChanged(nameof(CheckedCountText));
        Draft.OnExternalChanged();
    }

    private void SetVisibleChannels(bool value)
    {
        foreach (var row in _hardware)
        {
            row.SetChecked(value);
            if (value)
                _checkedChannels.Add(row.Channel);
            else
                _checkedChannels.Remove(row.Channel);
        }

        OnPropertyChanged(nameof(CheckedCountText));
        Draft.OnExternalChanged();
    }

    private void ClearCheckedChannels()
    {
        _checkedChannels.Clear();
        foreach (var row in _allChannels)
            row.SetChecked(false);
        OnPropertyChanged(nameof(CheckedCountText));
        Draft.OnExternalChanged();
    }

    private TopologyTreeItemViewModel? Find(EquipmentTopology topology, TopologyNodeId? id)
    {
        if (id is not { } nodeId || !topology.TryGetNode(nodeId, out var node) || _roots.Count == 0)
            return null;

        var item = _roots[0];
        for (var level = TopologyLevel.Cabinet; level <= node.Level; level++)
        {
            item.IsExpanded = true;
            var ordinal = node.Address.GetOrdinal(level);
            item = item.Children.First(child => child.Node.Ordinal == ordinal);
        }

        return item;
    }

    private bool ApplyColumnWidths(double treeWidth, double hardwareWidth)
    {
        if (!double.IsFinite(treeWidth) || !double.IsFinite(hardwareWidth))
            return false;

        TreeWidth = Math.Clamp(treeWidth, MinTreeWidth, MaxTreeWidth);
        HardwareWidth = Math.Clamp(hardwareWidth, MinHardwareWidth, MaxHardwareWidth);
        return true;
    }

    private async Task LoadLayoutAsync(IAppSettingsStore store)
    {
        Result<string?> loaded;
        try
        {
            // 存储是同步磁盘 I/O：离开 UI 线程
            loaded = await Task.Run(() => store.GetAsync(LayoutSettingKey, CancellationToken.None));
        }
        catch (Exception ex)
        {
            ShowMessage($"读取页面布局失败，使用默认栏宽：{ex.Message}", isError: true);
            return;
        }

        if (loaded.IsFailure)
        {
            ShowMessage($"读取页面布局失败，使用默认栏宽：{loaded.Error.Message}", isError: true);
            return;
        }

        // 值不合法（被手工改过）时用默认栏宽，不报错：只是界面偏好
        if (loaded.Value?.Split(',') is [var tree, var hardware]
            && double.TryParse(tree, NumberStyles.Float, CultureInfo.InvariantCulture, out var treeWidth)
            && double.TryParse(hardware, NumberStyles.Float, CultureInfo.InvariantCulture, out var hardwareWidth))
        {
            ApplyColumnWidths(treeWidth, hardwareWidth);
        }
    }

    private async Task SaveLayoutAsync(IAppSettingsStore store, string value)
    {
        Result saved;
        try
        {
            saved = await Task.Run(() => store.SetAsync(LayoutSettingKey, value, CancellationToken.None));
        }
        catch (Exception ex)
        {
            saved = Result.Failure(new Error("Settings.SaveFailed", ex.Message));
        }

        if (saved.IsFailure)
            ShowMessage($"页面布局没有保存，下次启动会恢复默认栏宽：{saved.Error.Message}", isError: true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(CanMapHere));
        foreach (var row in _allChannels)
            row.RaiseCanMap();
    }

    private void ShowMessage(string message, bool isError = false)
    {
        IsError = isError;
        Message = message;
    }

    // 后台线程：只投递
    private void OnWiringChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            RebuildHardware();
    }, DispatcherPriority.Background);
}

/// <summary>
/// 硬件通道的总线选项，如"新设备链路 · COM3（2 个模块）"：带物理链路，同名总线也能区分
/// </summary>
public sealed record MappingBusOption(BusId Id, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 一路通道的使用状态
/// </summary>
public enum ChannelUse
{
    /// <summary>
    /// 未接
    /// </summary>
    Free = 1,

    /// <summary>
    /// 已接到 1 个位置
    /// </summary>
    Used = 2,

    /// <summary>
    /// 共用：接到多个位置
    /// </summary>
    Shared = 3,
}

/// <summary>
/// 通道筛选：全部，或只看某一种使用状态
/// </summary>
public sealed record ChannelFilterOption(ChannelUse? Use, string Text)
{
    public static IReadOnlyList<ChannelFilterOption> All { get; } =
    [
        new(null, "全部"),
        new(ChannelUse.Free, "未接"),
        new(ChannelUse.Used, "已接"),
        new(ChannelUse.Shared, "共用"),
    ];

    public bool Accepts(MappingChannelViewModel row) => Use is not { } use || row.Use == use;

    public override string ToString() => Text;
}

/// <summary>
/// 硬件通道的模块选项：一行一个模块，如"Chroma 62015B · 地址 1"，带"已接 x / y"
/// </summary>
public sealed class MappingModuleOption(DeviceId id, BusId bus, string text, IReadOnlyList<MappingChannelViewModel> channels)
{
    public DeviceId Id { get; } = id;

    public BusId Bus { get; } = bus;

    /// <summary>
    /// 模块名与地址（不含系统 Id）
    /// </summary>
    public string Text { get; } = text;

    /// <summary>
    /// 这个模块上启用的通道，按通道组、通道号顺序
    /// </summary>
    public IReadOnlyList<MappingChannelViewModel> Channels { get; } = channels;

    public string UsageText { get; } = $"已接 {channels.Count(c => c.IsMapped)} / {channels.Count}";

    public override string ToString() => Text;
}

/// <summary>
/// 一路通道：名称（别名）、功能、使用状态与映射到的位置。一路通道可以接到多个位置：随时可关联到当前位置（当前位置已接过除外），
/// 已映射时可定位到第一个位置
/// </summary>
public sealed class MappingChannelViewModel : ViewModelBase
{
    private readonly Action<MappingChannelViewModel> _onCheckedChanged;
    private readonly IReadOnlyList<TopologyNodeId> _mappedNodes;
    private bool _isChecked;
    private bool _isSelectable;
    private bool _isHere;

    /// <param name="moduleText">"总线 · 模块"，映射草稿里显示</param>
    /// <param name="mapped">这路通道接到的位置（按接线顺序）及接线</param>
    /// <param name="canMapTo">能否关联到当前位置</param>
    /// <param name="onCheckedChanged">勾选变化（映射草稿取通道）</param>
    public MappingChannelViewModel(
        ChannelRef channel,
        string moduleText,
        string name,
        string kindText,
        IReadOnlyList<(TopologyNode Node, Wire Wire)> mapped,
        Func<MappingChannelViewModel, Task> onMap,
        Action<MappingChannelViewModel> onLocate,
        Func<MappingChannelViewModel, bool> canMapTo,
        Action<MappingChannelViewModel> onCheckedChanged)
    {
        Channel = channel;
        ModuleText = moduleText;
        _onCheckedChanged = onCheckedChanged;
        NameText = name;
        KindText = kindText;
        _mappedNodes = mapped.Select(m => m.Node.Id).ToList();
        MappedNode = _mappedNodes.Count > 0 ? _mappedNodes[0] : null;
        MapCount = mapped.Count;
        Use = mapped.Count switch
        {
            0 => ChannelUse.Free,
            1 => ChannelUse.Used,
            _ => ChannelUse.Shared,
        };
        MappedText = mapped.Count == 0 ? string.Empty : Describe(mapped[0].Node, mapped[0].Wire) + (mapped.Count > 1 ? $" 等 {mapped.Count} 个位置" : string.Empty);
        MapCommand = new AsyncRelayCommand(() => onMap(this), () => canMapTo(this));
        LocateCommand = new RelayCommand(() => onLocate(this), () => IsMapped);
    }

    private static string Describe(TopologyNode node, Wire wire)
        => $"{node.Level.DisplayName()} {node.Address}{(node.Name is { } n ? $"（{n}）" : string.Empty)}" +
           $"{(wire.Purpose is { } purpose ? $" · {purpose}" : string.Empty)}";

    /// <summary>
    /// 这路通道已接在 <paramref name="node"/> 上
    /// </summary>
    public bool IsMappedAt(TopologyNodeId node) => _mappedNodes.Contains(node);

    public ChannelRef Channel { get; }

    /// <summary>
    /// 通道功能：映射草稿按接点配对时优先配同功能的接点
    /// </summary>
    public ChannelKind? Kind { get; init; }

    public string ModuleText { get; }

    /// <summary>
    /// 映射草稿模式下通道可以勾选
    /// </summary>
    public bool IsSelectable
    {
        get => _isSelectable;
        set
        {
            if (SetProperty(ref _isSelectable, value))
                OnPropertyChanged(nameof(ShowCheckBox));
        }
    }

    public bool ShowCheckBox => _isSelectable;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _onCheckedChanged(this);
        }
    }

    /// <summary>
    /// 程序设置勾选（列表重建、全选）：不回调
    /// </summary>
    internal void SetChecked(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public string NameText { get; }

    public string KindText { get; }

    /// <summary>
    /// 接到的位置数
    /// </summary>
    public int MapCount { get; }

    public ChannelUse Use { get; }

    public bool IsFree => Use == ChannelUse.Free;

    public bool IsUsed => Use == ChannelUse.Used;

    public bool IsShared => Use == ChannelUse.Shared;

    /// <summary>
    /// 状态标签：未接 / 已接 / 共用 ×N
    /// </summary>
    public string UseText => Use switch
    {
        ChannelUse.Free => "未接",
        ChannelUse.Used => "已接",
        _ => $"共用 ×{MapCount}",
    };

    /// <summary>
    /// 接在当前选中的位置上
    /// </summary>
    public bool IsHere
    {
        get => _isHere;
        internal set => SetProperty(ref _isHere, value);
    }

    /// <summary>
    /// 接到的第一个位置（"定位"跳到这里）；未映射为 null
    /// </summary>
    public TopologyNodeId? MappedNode { get; }

    public bool IsMapped => MappedNode is not null;

    /// <summary>
    /// 接到的位置，如"层位 C01-L01 · VBAT 等 3 个位置"；未接为空串
    /// </summary>
    public string MappedText { get; }

    public AsyncRelayCommand MapCommand { get; }

    public RelayCommand LocateCommand { get; }

    public void RaiseCanMap() => MapCommand.RaiseCanExecuteChanged();
}
