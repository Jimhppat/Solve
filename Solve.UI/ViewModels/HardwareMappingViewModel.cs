using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.UI.ViewModels;

/// <summary>
/// 硬件映射页：左边位置树，中间选中位置的接线（<see cref="WiringPanelViewModel"/>），右边总线上可映射的硬件通道。
/// </summary>
/// <remarks>
/// <para>一个位置就是拓扑里的一个节点：在树上选中位置，再在右边点某路通道的"关联"，这路通道就接到该位置上（向下生效）。
/// 右边每路通道都显示它已映射到哪个位置，点"定位"跳到那个位置，两个方向都能查。</para>
/// <para>只列出总线上有启用通道的模块：没用到的设备、在模块配置里停用的通道不出现。</para>
/// <para>位置结构在"设备拓扑"页维护，本页只读拓扑；每次进入本页时按当前拓扑刷新（<see cref="Refresh"/>）。
/// 只能在 UI 线程上使用；写库在线程池上完成。</para>
/// </remarks>
public sealed class HardwareMappingViewModel : ViewModelBase, IDisposable
{
    // 页面级的已展开节点：节点 Id 终身不变，树重建后同一节点照样展开
    private readonly HashSet<TopologyNodeId> _expanded = [];

    private ITopologyService? _topology;
    private WiringInspector? _inspector;
    private EquipmentTopology? _bound;
    private IReadOnlyList<TopologyTreeItemViewModel> _roots = [];
    private TopologyTreeItemViewModel? _selectedItem;
    private IReadOnlyList<MappingRowViewModel> _hardware = [];
    private bool _unmappedOnly;
    private string _purposeInput = string.Empty;
    private bool _busy;
    private string? _message;
    private bool _isError;
    private bool _disposed;
    private bool _isDraftMode;

    // 硬件列表中勾选的通道（按通道定位）：列表重建（接线变化、筛选）后勾选不丢
    private readonly HashSet<ChannelRef> _checkedChannels = [];

    public HardwareMappingViewModel()
    {
        Draft = new MappingDraftViewModel(CheckedChannels, () => _purposeInput, OnDraftMapped);
        SelectVisibleChannelsCommand = new RelayCommand(() => SetVisibleChannels(true));
        ClearChannelsCommand = new RelayCommand(() => SetVisibleChannels(false));
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
            foreach (var row in _hardware.OfType<MappingChannelViewModel>())
                row.IsSelectable = value;
            Draft.SetScope(_selectedItem?.Node, _bound);
        }
    }

    public bool IsWiringMode => !_isDraftMode;

    /// <summary>
    /// 勾选当前列表中可见的全部通道（配合"只看未映射"使用）
    /// </summary>
    public RelayCommand SelectVisibleChannelsCommand { get; }

    public RelayCommand ClearChannelsCommand { get; }

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
            foreach (var row in _hardware.OfType<MappingChannelViewModel>())
                row.RaiseCanMap();
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

    // ==================== 硬件通道 ====================

    /// <summary>
    /// 总线上可映射的模块与通道（模块标题行 + 通道行，扁平列表便于虚拟化）
    /// </summary>
    public IReadOnlyList<MappingRowViewModel> Hardware
    {
        get => _hardware;
        private set
        {
            if (SetProperty(ref _hardware, value))
                OnPropertyChanged(nameof(HasHardware));
        }
    }

    public bool HasHardware => _hardware.Count > 0;

    public bool HasNoHardware => IsAvailable && _hardware.Count == 0;

    /// <summary>
    /// 只显示还没映射的通道
    /// </summary>
    public bool UnmappedOnly
    {
        get => _unmappedOnly;
        set
        {
            if (SetProperty(ref _unmappedOnly, value))
                RebuildHardware();
        }
    }

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

    // ==================== 内部 ====================

    private void RebuildHardware()
    {
        if (_inspector is not { } inspector)
        {
            Hardware = [];
            return;
        }

        var map = inspector.Wiring.Current;
        var rows = new List<MappingRowViewModel>();
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

                    if (_unmappedOnly && mapped.Count > 0)
                        continue;

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

            rows.Add(new MappingModuleViewModel($"{moduleText}（地址 {module.Device.Address}）"));
            rows.AddRange(channels);
        }

        // 已不存在的通道不再算勾选（映射草稿确认后接上的通道由 OnDraftMapped 取消勾选）
        _checkedChannels.IntersectWith(available);
        foreach (var row in rows.OfType<MappingChannelViewModel>())
            row.SetChecked(_checkedChannels.Contains(row.Channel));

        Hardware = rows;
        OnPropertyChanged(nameof(HasNoHardware));
        Draft.OnExternalChanged();
    }

    /// <summary>
    /// 映射草稿用的通道：硬件列表中勾选的通道（已映射的也可以，一路通道可以接到多个位置），按列表顺序
    /// </summary>
    private IReadOnlyList<MappingChannelViewModel> CheckedChannels()
        => _hardware.OfType<MappingChannelViewModel>().Where(r => r.IsChecked).ToList();

    /// <summary>
    /// 映射草稿确认后：接上的通道取消勾选，免得下一次组装又用到它们
    /// </summary>
    private void OnDraftMapped(IReadOnlyCollection<ChannelRef> channels)
    {
        _checkedChannels.ExceptWith(channels);
        foreach (var row in _hardware.OfType<MappingChannelViewModel>().Where(r => channels.Contains(r.Channel)))
            row.SetChecked(false);
        Draft.OnExternalChanged();
    }

    /// <summary>
    /// 这路通道能关联到当前位置：选中了可接线的位置，且这路通道还没接在这个位置上
    /// </summary>
    private bool CanMapTo(MappingChannelViewModel row)
        => CanMapHere && _selectedItem?.Node is { } node && !row.IsMappedAt(node.Id);

    private void OnChannelCheckedChanged(MappingChannelViewModel row)
    {
        if (row.IsChecked)
            _checkedChannels.Add(row.Channel);
        else
            _checkedChannels.Remove(row.Channel);
        Draft.OnExternalChanged();
    }

    private void SetVisibleChannels(bool value)
    {
        foreach (var row in _hardware.OfType<MappingChannelViewModel>())
        {
            row.SetChecked(value);
            if (value)
                _checkedChannels.Add(row.Channel);
            else
                _checkedChannels.Remove(row.Channel);
        }

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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(CanMapHere));
        foreach (var row in _hardware.OfType<MappingChannelViewModel>())
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
/// 硬件列表中的一行：模块标题或一路通道
/// </summary>
public abstract class MappingRowViewModel : ViewModelBase
{
}

/// <summary>
/// 模块标题行，如"1# RS-485 · 继电器板-01（地址 3）"
/// </summary>
public sealed class MappingModuleViewModel(string header) : MappingRowViewModel
{
    public string Header { get; } = header;
}

/// <summary>
/// 一路通道：名称（别名）、功能、映射到的位置。一路通道可以接到多个位置：随时可关联到当前位置（当前位置已接过除外），
/// 已映射时可定位到第一个位置
/// </summary>
public sealed class MappingChannelViewModel : MappingRowViewModel
{
    private readonly Action<MappingChannelViewModel> _onCheckedChanged;
    private readonly IReadOnlyList<TopologyNodeId> _mappedNodes;
    private bool _isChecked;
    private bool _isSelectable;

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
        MappedText = mapped.Count == 0 ? "未映射" : Describe(mapped[0].Node, mapped[0].Wire) + (mapped.Count > 1 ? $" 等 {mapped.Count} 个位置" : string.Empty);
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
    /// 接到的第一个位置（"定位"跳到这里）；未映射为 null
    /// </summary>
    public TopologyNodeId? MappedNode { get; }

    public bool IsMapped => MappedNode is not null;

    public string MappedText { get; }

    public AsyncRelayCommand MapCommand { get; }

    public RelayCommand LocateCommand { get; }

    public void RaiseCanMap() => MapCommand.RaiseCanExecuteChanged();
}
