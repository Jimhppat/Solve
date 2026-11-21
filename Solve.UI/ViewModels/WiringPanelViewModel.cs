using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Device.Sdk;

namespace Solve.UI.ViewModels;

/// <summary>
/// 接线面板的编辑方式
/// </summary>
public enum WiringEditorMode
{
    None,

    /// <summary>
    /// 在选中位置上接一路通道
    /// </summary>
    Single,

    /// <summary>
    /// 把选中位置下面某一层的全部位置依次接到连续通道上
    /// </summary>
    Batch,

    /// <summary>
    /// 修改一路接线的用途与线号
    /// </summary>
    Edit,
}

/// <summary>
/// 拓扑页的接线面板：选中位置的有效接线（本位置的在后，继承自上级的在前）及其运行状态，
/// 以及单路接线、批量接线、修改与拆除。
/// </summary>
/// <remarks>
/// <para>批量接线先预览（列出前几路与最后一路），确认后才写库；拆除先确认。新增单路接线不改变任何输出，直接保存。</para>
/// <para>健康度与读数按 <see cref="RefreshInterval"/> 从内存快照刷新，接线列表不变时只更新数值，不重建行。</para>
/// <para>只能在 UI 线程上使用；预览计算与磁盘 I/O 在线程池上完成。</para>
/// </remarks>
public sealed class WiringPanelViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 批量预览中列出的接线数（其余以省略号表示，最后一路总会列出）
    /// </summary>
    private const int PreviewLines = 5;

    private readonly CancellationTokenSource _lifetime = new();
    private WiringInspector? _inspector;
    private DeviceCommandService? _commands;
    private DeviceCommandPlan? _pendingCommand;
    private TopologyNode? _node;
    private DispatcherTimer? _timer;
    private bool _disposed;
    private bool _busy;
    private WiringEditorMode _mode;
    private IReadOnlyList<WireRowViewModel> _rows = [];
    private IReadOnlyList<WiringModule> _modules = [];
    private IReadOnlyList<WiringModuleOption> _moduleOptions = [];
    private WiringModuleOption? _selectedModule;
    private IReadOnlyList<WiringGroupOption> _groupOptions = [];
    private WiringGroupOption? _selectedGroup;
    private IReadOnlyList<WiringLevelOption> _levelOptions = [];
    private WiringLevelOption? _selectedLevel;
    private decimal? _channelInput;
    private bool _continueNext = true;
    private string _purposeInput = string.Empty;
    private string _labelInput = string.Empty;
    private WireRowViewModel? _editing;
    private WireRowViewModel? _pendingRemove;
    private WiringPlan? _pendingBatch;
    private string? _batchPreview;
    private string? _message;
    private bool _isError;

    public WiringPanelViewModel()
    {
        OpenSingleCommand = new RelayCommand(() => Open(WiringEditorMode.Single), () => CanEdit);
        OpenBatchCommand = new RelayCommand(() => Open(WiringEditorMode.Batch), () => CanEdit);
        CloseEditorCommand = new RelayCommand(CloseEditor);
        AddCommand = new AsyncRelayCommand(AddAsync, () => CanEdit && _mode == WiringEditorMode.Single && _selectedGroup is not null);
        PreviewBatchCommand = new AsyncRelayCommand(PreviewBatchAsync,
            () => CanEdit && _mode == WiringEditorMode.Batch && _selectedGroup is not null && _selectedLevel is not null);
        ConfirmBatchCommand = new AsyncRelayCommand(ConfirmBatchAsync, () => CanEdit && _pendingBatch is not null);
        CancelBatchCommand = new RelayCommand(() => SetPendingBatch(null, null));
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync, () => CanEdit && _editing is not null);
        ConfirmRemoveCommand = new AsyncRelayCommand(ConfirmRemoveAsync, () => CanEdit && _pendingRemove is not null);
        CancelRemoveCommand = new RelayCommand(() => PendingRemove = null);
        ConfirmCommandCommand = new AsyncRelayCommand(ConfirmCommandAsync, () => CanCommand && _pendingCommand is not null);
        CancelCommandCommand = new RelayCommand(() => PendingCommand = null);
    }

    // ==================== 显示 ====================

    /// <summary>
    /// 已接入接线服务、选中了可接线的位置（系统根不接线）
    /// </summary>
    public bool IsAvailable => _inspector is not null && _node is { Level: not TopologyLevel.System };

    public string? LoadErrorText => _inspector?.Wiring.LoadError is { } error ? WiringErrors.LoadFailed(error).Message : null;

    public bool HasLoadError => LoadErrorText is not null;

    public IReadOnlyList<WireRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(IsUnwired));
            }
        }
    }

    public bool HasRows => _rows.Count > 0;

    /// <summary>
    /// 本位置与上级都没有接线
    /// </summary>
    public bool IsUnwired => IsAvailable && _rows.Count == 0;

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

    // ==================== 编辑方式 ====================

    public WiringEditorMode Mode => _mode;

    public bool IsEditorClosed => _mode == WiringEditorMode.None;

    public bool IsSingle => _mode == WiringEditorMode.Single;

    public bool IsBatch => _mode == WiringEditorMode.Batch;

    public bool IsEditing => _mode == WiringEditorMode.Edit;

    /// <summary>
    /// 单路与批量表单共用的模块、通道输入
    /// </summary>
    public bool ShowChannelInputs => _mode is WiringEditorMode.Single or WiringEditorMode.Batch;

    public bool CanEdit => IsAvailable && !_busy && _inspector?.Wiring.LoadError is null;

    public RelayCommand OpenSingleCommand { get; }

    public RelayCommand OpenBatchCommand { get; }

    public RelayCommand CloseEditorCommand { get; }

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand PreviewBatchCommand { get; }

    public AsyncRelayCommand ConfirmBatchCommand { get; }

    public RelayCommand CancelBatchCommand { get; }

    public AsyncRelayCommand SaveEditCommand { get; }

    public AsyncRelayCommand ConfirmRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    // ==================== 表单输入 ====================

    public IReadOnlyList<WiringModuleOption> ModuleOptions
    {
        get => _moduleOptions;
        private set
        {
            if (SetProperty(ref _moduleOptions, value))
                OnPropertyChanged(nameof(HasModules));
        }
    }

    /// <summary>
    /// 有可以接线的模块（驱动声明了通道）
    /// </summary>
    public bool HasModules => _moduleOptions.Count > 0;

    public bool HasNoModules => ShowChannelInputs && !HasModules;

    public WiringModuleOption? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (!SetProperty(ref _selectedModule, value))
                return;

            GroupOptions = value?.Module.Channels.Select(g => new WiringGroupOption(g)).ToList() ?? [];
            SelectedGroup = GroupOptions.FirstOrDefault();
            OnInputChanged();
        }
    }

    public IReadOnlyList<WiringGroupOption> GroupOptions
    {
        get => _groupOptions;
        private set => SetProperty(ref _groupOptions, value);
    }

    public WiringGroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
                return;

            // 默认第一路空闲通道；全部已接时停在 1，保存时由服务报出占用情况
            _channelInput = FirstFreeChannel() ?? (value is null ? null : 1);
            OnPropertyChanged(nameof(ChannelInput));
            OnPropertyChanged(nameof(ChannelMax));
            OnPropertyChanged(nameof(ShowChannelIndex));
            OnInputChanged();
        }
    }

    /// <summary>
    /// 单路组（如单输出电源）不显示通道号
    /// </summary>
    public bool ShowChannelIndex => _selectedGroup is { Group.Count: > 1 };

    public decimal ChannelMax => _selectedGroup?.Group.Count ?? 1;

    /// <summary>
    /// 单路接线的通道号；批量接线的起始通道号
    /// </summary>
    public decimal? ChannelInput
    {
        get => _channelInput;
        set
        {
            if (SetProperty(ref _channelInput, value))
                OnInputChanged();
        }
    }

    public string ChannelLabel => IsBatch ? "起始通道" : "通道号";

    public IReadOnlyList<WiringLevelOption> LevelOptions
    {
        get => _levelOptions;
        private set => SetProperty(ref _levelOptions, value);
    }

    public WiringLevelOption? SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (SetProperty(ref _selectedLevel, value))
                OnInputChanged();
        }
    }

    /// <summary>
    /// 通道不够时续接同一总线上同型号、地址更大的模块
    /// </summary>
    public bool ContinueNextInput
    {
        get => _continueNext;
        set
        {
            if (SetProperty(ref _continueNext, value))
                OnInputChanged();
        }
    }

    public string PurposeInput
    {
        get => _purposeInput;
        set
        {
            if (SetProperty(ref _purposeInput, value ?? string.Empty))
                OnInputChanged();
        }
    }

    public string LabelInput
    {
        get => _labelInput;
        set
        {
            if (SetProperty(ref _labelInput, value ?? string.Empty))
                OnInputChanged();
        }
    }

    public int MaxPurposeLength => Wire.MaxPurposeLength;

    public int MaxLabelLength => Wire.MaxLabelLength;

    /// <summary>
    /// 正在修改的接线，如"供电 · 电源-01 输出"
    /// </summary>
    public string? EditingTitle => _editing is { } row ? $"{row.KindText} · {row.HardwareText}" : null;

    // ==================== 批量预览与拆除确认 ====================

    public string? BatchPreviewText
    {
        get => _batchPreview;
        private set
        {
            if (SetProperty(ref _batchPreview, value))
                OnPropertyChanged(nameof(HasBatchPreview));
        }
    }

    public bool HasBatchPreview => _batchPreview is not null;

    public WireRowViewModel? PendingRemove
    {
        get => _pendingRemove;
        private set
        {
            if (!SetProperty(ref _pendingRemove, value))
                return;

            OnPropertyChanged(nameof(IsRemoveConfirmOpen));
            OnPropertyChanged(nameof(RemoveConfirmText));
            RaiseCommandsChanged();
        }
    }

    public bool IsRemoveConfirmOpen => _pendingRemove is not null;

    public string? RemoveConfirmText => _pendingRemove is { } row
        ? $"拆除「{row.KindText}」{row.HardwareText} 的接线？拆除后这路通道与位置不再关联，现场的线不受影响。"
        : null;

    // ==================== 命令确认 ====================

    /// <summary>
    /// 能否从接线行下发命令：已接入命令服务、面板空闲（执行期间禁用全部命令按钮，防止重入）
    /// </summary>
    public bool CanCommand => IsAvailable && !_busy && _commands is not null;

    public AsyncRelayCommand ConfirmCommandCommand { get; }

    public RelayCommand CancelCommandCommand { get; }

    /// <summary>
    /// 等待操作员确认的命令（高危命令；实机模式下的输出动作）
    /// </summary>
    public DeviceCommandPlan? PendingCommand
    {
        get => _pendingCommand;
        private set
        {
            if (!SetProperty(ref _pendingCommand, value))
                return;

            OnPropertyChanged(nameof(IsCommandConfirmOpen));
            OnPropertyChanged(nameof(CommandConfirmText));
            OnPropertyChanged(nameof(IsPendingHazardous));
            RaiseCommandsChanged();
        }
    }

    public bool IsCommandConfirmOpen => _pendingCommand is not null;

    public string? CommandConfirmText => _pendingCommand?.Describe();

    public bool IsPendingHazardous => _pendingCommand?.Safety == CommandSafety.Hazardous;

    // ==================== 接入与显示 ====================

    /// <summary>
    /// 接入接线运行视图。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    /// <param name="commands">按位置下发命令；为 null 时接线行不显示命令按钮</param>
    public void Attach(WiringInspector inspector, DeviceCommandService? commands = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (_inspector is not null)
            throw new InvalidOperationException("接线面板已接入接线服务");

        _inspector = inspector;
        _commands = commands;
        inspector.Wiring.Changed += OnWiringChanged;

        // 健康度与读数按固定频率从内存快照刷新，限制界面重绘频率
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => RefreshStatuses());
        _timer.Start();
        Reload();
    }

    /// <summary>
    /// 显示一个位置的接线（拓扑页选中节点变化、拓扑保存后调用）。换了位置时关闭表单。须在 UI 线程上调用。
    /// </summary>
    public void ShowNode(TopologyNode? node)
    {
        var changed = _node?.Id != node?.Id;
        _node = node;
        if (changed)
        {
            CloseEditor();
            PendingRemove = null;
            PendingCommand = null;
            Message = null;
        }

        Reload();
    }

    /// <summary>
    /// 拓扑变更会一并拆除的接线数（删除位置时），供拓扑页的确认提示
    /// </summary>
    public int CountRemovedBy(EquipmentTopology candidate) => _inspector?.Wiring.Current.CountRemovedBy(candidate) ?? 0;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 在途命令：总线事务随之取消（已发出的报文无法撤回，命令服务记为结果未知）
        _lifetime.Cancel();
        _lifetime.Dispose();
        _timer?.Stop();
        _timer = null;
        if (_inspector is not null)
            _inspector.Wiring.Changed -= OnWiringChanged;
    }

    // ==================== 命令 ====================

    private void Open(WiringEditorMode mode)
    {
        PendingRemove = null;
        PendingCommand = null;
        SetPendingBatch(null, null);
        Message = null;
        _editing = null;
        _purposeInput = string.Empty;
        _labelInput = string.Empty;
        OnPropertyChanged(nameof(PurposeInput));
        OnPropertyChanged(nameof(LabelInput));
        SetMode(mode);

        // 沿用上次选的模块（连续给几个位置接同一台模块的不同通道时不用每次重选），它已不存在时选第一个
        var keep = _selectedModule?.Module.Device.Id;
        ModuleOptions = _modules.Select(m => new WiringModuleOption(m)).ToList();
        SelectedModule = ModuleOptions.FirstOrDefault(o => o.Module.Device.Id == keep) ?? ModuleOptions.FirstOrDefault();

        LevelOptions = BuildLevelOptions();
        SelectedLevel = LevelOptions.LastOrDefault();
    }

    private void CloseEditor()
    {
        SetPendingBatch(null, null);
        _editing = null;
        SetMode(WiringEditorMode.None);
    }

    private void Edit(WireRowViewModel row)
    {
        if (!CanEdit || !row.IsOwn)
            return;

        PendingRemove = null;
        PendingCommand = null;
        SetPendingBatch(null, null);
        Message = null;
        _editing = row;
        _purposeInput = row.Wire.Purpose ?? string.Empty;
        _labelInput = row.Wire.Label ?? string.Empty;
        OnPropertyChanged(nameof(PurposeInput));
        OnPropertyChanged(nameof(LabelInput));
        OnPropertyChanged(nameof(EditingTitle));
        SetMode(WiringEditorMode.Edit);
    }

    private void Remove(WireRowViewModel row)
    {
        if (!CanEdit || !row.IsOwn)
            return;

        CloseEditor();
        Message = null;
        PendingCommand = null;
        PendingRemove = row;
    }

    /// <summary>
    /// 点了接线行上的命令：解析并过联锁；需要确认的先显示确认，其余直接执行。联锁不满足时显示原因。
    /// </summary>
    private async void RequestCommand(WireRowViewModel row, DeviceCommandOption option)
    {
        if (!CanCommand || _commands is not { } commands || _node is not { } node)
            return;

        CloseEditor();
        PendingRemove = null;
        PendingCommand = null;
        Message = null;
        var request = new DeviceCommandRequest(node.Id, row.Wire.Kind, row.Wire.Purpose, option.Key);

        SetBusy(true);
        Result<DeviceCommandPlan> plan;
        try
        {
            // 解析要遍历接线所在位置的子树（十万级节点时需要数十毫秒），放到线程池
            plan = await Task.Run(() => commands.Prepare(request));
        }
        catch (Exception ex)
        {
            // async void 的边界：异常不能逃逸，否则会触发全站安全跳闸
            plan = Result<DeviceCommandPlan>.Failure(new Error("Command.Failed", ex.Message));
        }
        finally
        {
            SetBusy(false);
        }

        // 期间换了位置或面板已关闭：作废
        if (_disposed || _node?.Id != node.Id)
            return;

        if (plan.IsFailure)
        {
            ShowMessage(plan.Error.Message, isError: true);
            return;
        }

        if (plan.Value.RequiresConfirmation)
        {
            PendingCommand = plan.Value;
            return;
        }

        await ExecuteCommandAsync(plan.Value, confirmed: false);
    }

    private async Task ConfirmCommandAsync()
    {
        if (_pendingCommand is not { } plan)
            return;

        PendingCommand = null;
        await ExecuteCommandAsync(plan, confirmed: true);
    }

    private async Task ExecuteCommandAsync(DeviceCommandPlan plan, bool confirmed)
    {
        if (_commands is not { } commands || _disposed)
            return;

        SetBusy(true);
        Message = null;
        try
        {
            Result<DeviceCommandResult> result;
            try
            {
                var token = _lifetime.Token;
                result = await Task.Run(() => commands.ExecuteAsync(plan, confirmed, token), token);
            }
            catch (OperationCanceledException)
            {
                // 面板关闭（程序退出）：不再更新界面
                return;
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上发起：异常不能逃逸，否则会触发全站安全跳闸
                result = Result<DeviceCommandResult>.Failure(new Error("Command.Failed", ex.Message));
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return;
            }

            ShowMessage($"已执行「{plan.CommandName}」：{plan.ScopeText}，耗时 {result.Value.Elapsed.TotalMilliseconds:0} ms");
        }
        finally
        {
            if (!_disposed)
                SetBusy(false);
        }
    }

    private IReadOnlyList<DeviceCommandOption> ListCommands(Wire wire)
    {
        // 写参数类命令需要输入设定值，SDK 尚未声明命令参数，界面暂不提供
        return _commands?.ListCommands(wire).Where(c => c.Safety != CommandSafety.Parameter).ToList() ?? [];
    }

    private async Task AddAsync()
    {
        if (_inspector is not { } inspector || _node is not { } node || _selectedModule is not { } module || _selectedGroup is not { } group)
            return;
        if (!TryReadChannel(group, out var index))
            return;

        var channel = new ChannelRef(module.Module.Device.Id, group.Group.Key, index);
        var purpose = _purposeInput;
        var label = _labelInput;
        await RunAsync(async () =>
        {
            var plan = await Task.Run(() => inspector.Wiring.PlanAdd(node.Id, channel, purpose, label));
            if (plan.IsFailure)
                return Result.Failure(plan.Error);

            return await inspector.Wiring.ApplyAsync(plan.Value, CancellationToken.None);
        }, success: $"已接线：{inspector.Wiring.DescribeChannel(channel)}");
    }

    private async Task PreviewBatchAsync()
    {
        if (_inspector is not { } inspector || _node is not { } node || _selectedModule is not { } module
            || _selectedGroup is not { } group || _selectedLevel is not { } level)
            return;
        if (!TryReadChannel(group, out var start))
            return;

        var sources = new List<ChannelSource> { new(module.Module.Device.Id, group.Group) };
        if (_continueNext)
        {
            // 续接：同一总线上同型号、地址更大的模块，按地址顺序
            sources.AddRange(_modules
                .Where(m => m.BusId == module.Module.BusId && m.Device.Driver == module.Module.Device.Driver
                            && m.Device.Address > module.Module.Device.Address)
                .OrderBy(m => m.Device.Address)
                .Select(m => new ChannelSource(m.Device.Id, group.Group)));
        }

        var request = new WiringBatchRequest(node.Id, level.Level, sources, start) { Purpose = _purposeInput };
        SetBusy(true);
        Message = null;
        try
        {
            Result<WiringPlan> plan;
            try
            {
                plan = await Task.Run(() => inspector.Wiring.PlanBatch(request));
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
                plan = Result<WiringPlan>.Failure(new Error("Wiring.PlanFailed", ex.Message));
            }

            if (plan.IsFailure)
            {
                ShowMessage(plan.Error.Message, isError: true);
                return;
            }

            SetPendingBatch(plan.Value, DescribeBatch(inspector, plan.Value));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConfirmBatchAsync()
    {
        if (_inspector is not { } inspector || _pendingBatch is not { } plan)
            return;

        SetPendingBatch(null, null);
        var ok = await RunAsync(() => inspector.Wiring.ApplyAsync(plan, CancellationToken.None), success: $"已接线 {plan.Added.Count} 路");
        if (ok)
            CloseEditor();
    }

    private async Task SaveEditAsync()
    {
        if (_inspector is not { } inspector || _editing is not { } row)
            return;

        var purpose = _purposeInput;
        var label = _labelInput;
        var ok = await RunAsync(() => inspector.Wiring.UpdateAsync(row.Id, purpose, label, CancellationToken.None), success: "已保存");
        if (ok)
            CloseEditor();
    }

    private async Task ConfirmRemoveAsync()
    {
        if (_inspector is not { } inspector || _pendingRemove is not { } row)
            return;

        PendingRemove = null;
        await RunAsync(async () =>
        {
            var removed = await inspector.Wiring.RemoveAsync([row.Id], CancellationToken.None);
            return removed.IsSuccess ? Result.Success() : Result.Failure(removed.Error);
        }, success: $"已拆除「{row.KindText}」{row.HardwareText}");
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 执行一次写操作：期间禁用表单；结果显示在面板里。返回是否成功。
    /// </summary>
    private async Task<bool> RunAsync(Func<Task<Result>> action, string success)
    {
        SetBusy(true);
        Message = null;
        try
        {
            Result result;
            try
            {
                result = await action();
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
                result = Result.Failure(new Error("Wiring.Failed", ex.Message));
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return false;
            }

            Reload();
            ShowMessage(success);
            return true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryReadChannel(WiringGroupOption group, out int index)
    {
        index = 0;
        if (group.Group.Count == 1)
        {
            index = 1;
            return true;
        }

        if (_channelInput is not { } value || value != decimal.Truncate(value) || value < 1 || value > group.Group.Count)
        {
            ShowMessage($"{ChannelLabel}必须是 1～{group.Group.Count} 之间的整数", isError: true);
            return false;
        }

        index = (int)value;
        return true;
    }

    /// <summary>
    /// 重新取模块列表与本位置的接线（接线有变化、换了位置、写操作之后）
    /// </summary>
    private void Reload()
    {
        if (_disposed)
            return;

        _modules = _inspector?.Wiring.ListModules() ?? [];
        if (_mode is WiringEditorMode.Single or WiringEditorMode.Batch)
        {
            // 模块被删除或驱动被停用后，表单不能还指着它
            var keep = _selectedModule?.Module.Device.Id;
            ModuleOptions = _modules.Select(m => new WiringModuleOption(m)).ToList();
            SelectedModule = ModuleOptions.FirstOrDefault(o => o.Module.Device.Id == keep) ?? ModuleOptions.FirstOrDefault();
        }

        RebuildRows();
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsUnwired));
        OnPropertyChanged(nameof(LoadErrorText));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(HasNoModules));
        RaiseCommandsChanged();
    }

    private void RebuildRows()
    {
        if (!IsAvailable || _inspector is not { } inspector || _node is not { } node)
        {
            Rows = [];
            return;
        }

        Rows = inspector.Inspect(node.Id)
            .Select(s => new WireRowViewModel(s, Edit, Remove, ListCommands(s.Wire), RequestCommand, () => CanCommand))
            .ToList();
    }

    /// <summary>
    /// 定时刷新：接线列表不变时只更新健康度与读数，变了（如其他位置拆了上级的线）才重建
    /// </summary>
    private void RefreshStatuses()
    {
        if (_disposed || !IsAvailable || _inspector is not { } inspector || _node is not { } node)
            return;

        try
        {
            var statuses = inspector.Inspect(node.Id);
            if (statuses.Count != _rows.Count
                || statuses.Where((s, i) => s.Wire.Id != _rows[i].Id || !_rows[i].HasSameCommands(ListCommands(s.Wire))).Any())
            {
                RebuildRows();
                return;
            }

            for (var i = 0; i < statuses.Count; i++)
                _rows[i].Apply(statuses[i]);
        }
        catch (Exception ex)
        {
            // 定时器在 UI 线程上：异常逃逸会触发全站安全跳闸。只停这一次刷新并显示原因
            ShowMessage($"接线状态刷新失败：{ex.Message}", isError: true);
        }
    }

    private int? FirstFreeChannel()
    {
        if (_inspector is not { } inspector || _selectedModule is not { } module || _selectedGroup is not { } group)
            return null;

        var map = inspector.Wiring.Current;
        // 只在模块上启用的通道里找：停用的端子没有接线，不能接
        foreach (var index in group.Group.Enabled())
        {
            if (!map.TryGetByChannel(new ChannelRef(module.Module.Device.Id, group.Group.Key, index), out _))
                return index;
        }

        return null;
    }

    private IReadOnlyList<WiringLevelOption> BuildLevelOptions()
    {
        if (_node is not { } node || _inspector?.Wiring.Current.Topology is not { } topology)
            return [];

        var options = new List<WiringLevelOption> { new(node.Level, 1, isSelf: true) };
        for (var level = node.Level + 1; level <= TopologyLevel.Product; level++)
        {
            var count = topology.GetDescendants(node.Address, level).Count;
            if (count > 0)
                options.Add(new WiringLevelOption(level, count, isSelf: false));
        }

        return options;
    }

    private static string DescribeBatch(WiringInspector inspector, WiringPlan plan)
    {
        var topology = plan.Candidate.Topology;
        string Line(Wire wire)
            => $"{(topology is not null && topology.TryGetNode(wire.Node, out var node) ? node.Address.ToString() : "?")} ← {inspector.Wiring.DescribeChannel(wire.Channel)}";

        var added = plan.Added;
        var lines = new List<string> { $"将接线 {added.Count} 路「{added[0].KindText}」：" };
        lines.AddRange(added.Take(PreviewLines).Select(Line));
        if (added.Count > PreviewLines + 1)
            lines.Add("…");
        if (added.Count > PreviewLines)
            lines.Add(Line(added[^1]));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 输入变了：之前的预览已不是现在的输入，作废
    /// </summary>
    private void OnInputChanged()
    {
        SetPendingBatch(null, null);
        RaiseCommandsChanged();
    }

    private void SetPendingBatch(WiringPlan? plan, string? preview)
    {
        _pendingBatch = plan;
        BatchPreviewText = preview;
        ConfirmBatchCommand.RaiseCanExecuteChanged();
    }

    private void SetMode(WiringEditorMode mode)
    {
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsEditorClosed));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsBatch));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowChannelInputs));
        OnPropertyChanged(nameof(HasNoModules));
        OnPropertyChanged(nameof(ChannelLabel));
        OnPropertyChanged(nameof(EditingTitle));
        RaiseCommandsChanged();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCommand));
        RaiseCommandsChanged();
    }

    private void ShowMessage(string message, bool isError = false)
    {
        IsError = isError;
        Message = message;
    }

    private void RaiseCommandsChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OpenSingleCommand.RaiseCanExecuteChanged();
        OpenBatchCommand.RaiseCanExecuteChanged();
        AddCommand.RaiseCanExecuteChanged();
        PreviewBatchCommand.RaiseCanExecuteChanged();
        ConfirmBatchCommand.RaiseCanExecuteChanged();
        SaveEditCommand.RaiseCanExecuteChanged();
        ConfirmRemoveCommand.RaiseCanExecuteChanged();
        ConfirmCommandCommand.RaiseCanExecuteChanged();
        foreach (var row in _rows)
            row.RaiseCommandsChanged();
    }

    // 后台线程：只投递
    private void OnWiringChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload, DispatcherPriority.Background);
}
