using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.UI.ViewModels;

/// <summary>
/// 位置筛选：全部位置，或只看还缺某种功能接线的位置（如"还没有采集的产品位"）
/// </summary>
public sealed record PositionFilterOption(ChannelKind? Missing)
{
    public static IReadOnlyList<PositionFilterOption> All { get; } =
        [new((ChannelKind?)null), .. ChannelKinds.All.Select(kind => new PositionFilterOption(kind))];

    public override string ToString() => Missing is { } kind ? $"缺少{kind.DisplayName()}" : "全部位置";
}

/// <summary>
/// 映射草稿里的一个候选位置（勾选后参与组装）
/// </summary>
public sealed class DraftPositionViewModel(TopologyNode node, Action onCheckedChanged) : ViewModelBase
{
    private bool _isChecked;

    public TopologyNode Node { get; } = node;

    public string Text { get; } = node.Name is { } name ? $"{node.Address}（{name}）" : node.Address.ToString();

    public bool IsDisabled => !Node.IsEffectivelyEnabled;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                onCheckedChanged();
        }
    }

    /// <summary>
    /// 程序批量勾选：不逐个触发计数刷新
    /// </summary>
    internal void SetChecked(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// 映射草稿里的一行："位置 ← 模块 通道"，带这一行的校验结果
/// </summary>
public sealed class DraftRowViewModel : ViewModelBase
{
    private MappingChannelViewModel _channel;
    private readonly string? _purpose;
    private string? _problem;

    /// <param name="purpose">按接点配对时为接点名（写入接线，供产品位接点核对匹配；界面不显示）；按位置配对时为 null</param>
    public DraftRowViewModel(TopologyNode position, MappingChannelViewModel channel, string? purpose,
        Action<DraftRowViewModel, int> onMove, Action<DraftRowViewModel> onRemove)
    {
        Position = position;
        _channel = channel;
        _purpose = purpose;
        PositionText = position.Name is { } name ? $"{position.Address}（{name}）" : position.Address.ToString();
        MoveUpCommand = new RelayCommand(() => onMove(this, -1));
        MoveDownCommand = new RelayCommand(() => onMove(this, +1));
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }

    public TopologyNode Position { get; }

    public string PositionText { get; }

    /// <summary>
    /// 分配给这个位置的通道；上移 / 下移是在相邻两行之间交换通道，位置顺序不变
    /// </summary>
    public MappingChannelViewModel Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, value))
                OnPropertyChanged(nameof(ChannelText));
        }
    }

    public string ChannelText => $"{_channel.ModuleText}  {_channel.NameText}（{_channel.KindText}）";

    public string? Purpose => _purpose;

    public string? Problem
    {
        get => _problem;
        set
        {
            if (SetProperty(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// 映射草稿：① 取位置（在树上选中的范围内按层级勾选）→ ② 取通道（硬件通道列表勾选）→ ③ 组装成"位置 ← 通道"配对，
/// 逐行校验、可调整 → 确认后一次写入。
/// </summary>
/// <remarks>
/// <para>组装规则：勾选的位置声明了接点且还有没接的时，按接点配对——每路勾选的通道（按硬件列表顺序）优先取同功能的空接点，
/// 没有同功能的就按顺序填进剩下的空接点（不按功能拦截），用途填接点名；否则勾选的位置按位置编码排序，与勾选的通道一一对应。
/// 两种方式都明确提示没配上的数量。已映射的通道也可以勾选：一路通道可以接到多个位置。</para>
/// <para>草稿只在内存里，确认前不写库；任何一行有问题都不能确认（整批要么全接上、要么一条不写）。
/// 建立映射只是写配置，不会让任何输出动作。</para>
/// <para>只能在 UI 线程上使用；校验与写库在线程池上完成。</para>
/// </remarks>
public sealed class MappingDraftViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<MappingChannelViewModel>> _checkedChannels;
    private readonly Action<IReadOnlyCollection<ChannelRef>>? _onMapped;
    private WiringInspector? _inspector;
    private TopologyNode? _scope;
    private EquipmentTopology? _topology;
    private IReadOnlyList<WiringLevelOption> _levelOptions = [];
    private WiringLevelOption? _selectedLevel;
    private PositionFilterOption _filter = PositionFilterOption.All[0];
    private IReadOnlyList<DraftPositionViewModel> _positions = [];
    private WiringAssignPreview? _preview;
    private int _validation;
    private bool _busy;
    private string? _message;
    private bool _isError;

    /// <param name="checkedChannels">硬件通道列表中当前勾选的通道（按列表顺序）</param>
    /// <param name="onMapped">确认写入后通知接上的通道（硬件列表据此取消勾选）</param>
    public MappingDraftViewModel(Func<IReadOnlyList<MappingChannelViewModel>> checkedChannels,
        Action<IReadOnlyCollection<ChannelRef>>? onMapped = null)
    {
        _checkedChannels = checkedChannels;
        _onMapped = onMapped;
        SelectAllPositionsCommand = new RelayCommand(() => SetAllPositions(true));
        ClearPositionsCommand = new RelayCommand(() => SetAllPositions(false));
        AssembleCommand = new AsyncRelayCommand(AssembleAsync, () => !_busy && SelectedPositionCount > 0 && _checkedChannels().Count > 0);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !_busy && Rows.Count > 0 && _preview?.Plan is not null);
        ClearCommand = new RelayCommand(Clear, () => !_busy && Rows.Count > 0);
    }

    // ==================== ① 取位置 ====================

    /// <summary>
    /// 取位置的范围：树上选中的位置，如"层位 C01-L02"
    /// </summary>
    public string? ScopeText => _scope is { } node
        ? node.Level == TopologyLevel.System ? "整机" : $"{node.Level.DisplayName()} {node.Address}"
        : null;

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
                RebuildPositions();
        }
    }

    public IReadOnlyList<PositionFilterOption> FilterOptions => PositionFilterOption.All;

    public PositionFilterOption Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? PositionFilterOption.All[0]))
                RebuildPositions();
        }
    }

    public IReadOnlyList<DraftPositionViewModel> Positions
    {
        get => _positions;
        private set
        {
            if (SetProperty(ref _positions, value))
                OnPositionCheckedChanged();
        }
    }

    public int SelectedPositionCount => _positions.Count(p => p.IsChecked);

    public string PositionCountText => $"已选 {SelectedPositionCount} / {_positions.Count} 个位置";

    public RelayCommand SelectAllPositionsCommand { get; }

    public RelayCommand ClearPositionsCommand { get; }

    // ==================== ③ 组装预览 ====================

    public string ChannelCountText => $"已选 {_checkedChannels().Count} 路通道";

    public ObservableCollection<DraftRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public AsyncRelayCommand AssembleCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand ClearCommand { get; }

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

    // ==================== 接入与范围 ====================

    public void Attach(WiringInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>
    /// 树上选中的位置变了（或拓扑被替换）：重建层级选项与候选位置。已组装的配对不受影响，确认时按当前拓扑重新校验。
    /// </summary>
    public void SetScope(TopologyNode? scope, EquipmentTopology? topology)
    {
        _scope = scope;
        _topology = topology;

        var options = new List<WiringLevelOption>();
        if (scope is not null && topology is not null)
        {
            if (scope.Level != TopologyLevel.System)
                options.Add(new WiringLevelOption(scope.Level, 1, isSelf: true));
            for (var level = scope.Level + 1; level <= TopologyLevel.Product; level++)
            {
                var count = topology.GetDescendants(scope.Address, level).Count;
                if (count > 0)
                    options.Add(new WiringLevelOption(level, count, isSelf: false));
            }
        }

        // 换了范围后沿用原来选的层级（如一直在给产品位接线），没有这一层时选最深一层
        var keep = _selectedLevel?.Level;
        _levelOptions = options;
        _selectedLevel = options.FirstOrDefault(o => o.Level == keep) ?? options.LastOrDefault();
        OnPropertyChanged(nameof(LevelOptions));
        OnPropertyChanged(nameof(SelectedLevel));
        OnPropertyChanged(nameof(ScopeText));
        RebuildPositions();
    }

    /// <summary>
    /// 接线或硬件勾选有变化：刷新计数，已组装的配对重新校验
    /// </summary>
    public void OnExternalChanged()
    {
        OnPropertyChanged(nameof(ChannelCountText));
        RebuildPositions();
        RaiseCommands();
        if (Rows.Count > 0)
            _ = ValidateAsync();
    }

    // ==================== 命令 ====================

    private async Task AssembleAsync()
    {
        var positions = _positions.Where(p => p.IsChecked).Select(p => p.Node).OrderBy(n => n.Address).ToList();
        var channels = _checkedChannels();
        if (positions.Count == 0 || channels.Count == 0)
            return;

        // 选中的位置声明了接点且还有没接的：按接点配对，用途就是接点名；否则按位置一一对应
        var openPorts = OpenPorts(positions);
        if (openPorts.Count > 0)
        {
            await AssembleByPortsAsync(openPorts, channels);
            return;
        }

        var count = Math.Min(positions.Count, channels.Count);
        Rows.Clear();
        for (var i = 0; i < count; i++)
            Rows.Add(new DraftRowViewModel(positions[i], channels[i], null, Move, Remove));
        OnPropertyChanged(nameof(HasRows));

        var extra = positions.Count != channels.Count
            ? positions.Count > channels.Count
                ? $"多出的 {positions.Count - count} 个位置没有分配通道。"
                : $"多出的 {channels.Count - count} 路通道没有分配位置。"
            : null;
        await ValidateAsync(extra);
    }

    /// <summary>
    /// 选中位置上还没接的接点，按位置编码、接点声明顺序
    /// </summary>
    private List<(TopologyNode Node, Port Port)> OpenPorts(IReadOnlyList<TopologyNode> positions)
    {
        var open = new List<(TopologyNode, Port)>();
        if (_inspector?.Wiring.Current is not { } map)
            return open;

        foreach (var node in positions)
        {
            foreach (var coverage in map.GetPortCoverage(node.Id))
            {
                if (!coverage.IsConnected)
                    open.Add((node, coverage.Required.Port));
            }
        }

        return open;
    }

    /// <summary>
    /// 按接点配对（不按功能拦截）：先让每路勾选的通道（按列表顺序）取第一个同功能、还没配上的接点；
    /// 剩下的通道再按顺序填进剩下的空接点。行按位置、接点顺序排列，用途填接点名
    /// </summary>
    private async Task AssembleByPortsAsync(List<(TopologyNode Node, Port Port)> openPorts, IReadOnlyList<MappingChannelViewModel> channels)
    {
        var assigned = new MappingChannelViewModel?[openPorts.Count];
        var leftover = new List<MappingChannelViewModel>();
        foreach (var channel in channels)
        {
            var slot = FirstOpen(port => port.Kind == channel.Kind);
            if (slot < 0)
                leftover.Add(channel);
            else
                assigned[slot] = channel;
        }

        var unmatched = 0;
        foreach (var channel in leftover)
        {
            var slot = FirstOpen(static _ => true);
            if (slot < 0)
                unmatched++;
            else
                assigned[slot] = channel;
        }

        Rows.Clear();
        for (var i = 0; i < openPorts.Count; i++)
        {
            if (assigned[i] is { } channel)
                Rows.Add(new DraftRowViewModel(openPorts[i].Node, channel, openPorts[i].Port.Purpose, Move, Remove));
        }

        OnPropertyChanged(nameof(HasRows));

        var notes = new List<string>();
        var empty = openPorts.Count - Rows.Count;
        if (empty > 0)
            notes.Add($"还有 {empty} 个接点没有分配通道。");
        if (unmatched > 0)
            notes.Add($"多出的 {unmatched} 路通道没有空接点，未使用。");
        await ValidateAsync(notes.Count == 0 ? null : string.Join("", notes));

        int FirstOpen(Func<Port, bool> accepts)
        {
            for (var i = 0; i < openPorts.Count; i++)
            {
                if (assigned[i] is null && accepts(openPorts[i].Port))
                    return i;
            }

            return -1;
        }
    }

    private async Task ConfirmAsync()
    {
        if (_inspector is not { } inspector || Rows.Count == 0)
            return;

        SetBusy(true);
        try
        {
            Result result;
            int added;
            var rows = SnapshotRows();
            try
            {
                // 确认时按当前接线与拓扑重新校验：组装之后位置可能已被删除，或同一位置已接了同一路通道
                var preview = await Task.Run(() => inspector.Wiring.PlanAssign(rows));
                if (preview.IsFailure)
                {
                    result = Result.Failure(preview.Error);
                    added = 0;
                }
                else if (preview.Value.Plan is not { } plan)
                {
                    ApplyPreview(preview.Value);
                    ShowMessage($"有 {preview.Value.ProblemCount} 行有问题，未保存，请调整后再确认。", isError: true);
                    return;
                }
                else
                {
                    result = await inspector.Wiring.ApplyAsync(plan, CancellationToken.None);
                    added = plan.Added.Count;
                }
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
                result = Result.Failure(new Error("Wiring.DraftFailed", ex.Message));
                added = 0;
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return;
            }

            Rows.Clear();
            _preview = null;
            OnPropertyChanged(nameof(HasRows));
            ShowMessage($"已映射 {added} 路");
            _onMapped?.Invoke(rows.Select(r => r.Channel).ToHashSet());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Clear()
    {
        Rows.Clear();
        _preview = null;
        Message = null;
        OnPropertyChanged(nameof(HasRows));
        RaiseCommands();
    }

    /// <summary>
    /// 与相邻一行交换通道（位置顺序不变）
    /// </summary>
    private void Move(DraftRowViewModel row, int offset)
    {
        var index = Rows.IndexOf(row);
        var other = index + offset;
        if (index < 0 || other < 0 || other >= Rows.Count)
            return;

        (Rows[index].Channel, Rows[other].Channel) = (Rows[other].Channel, Rows[index].Channel);
        _ = ValidateAsync();
    }

    private void Remove(DraftRowViewModel row)
    {
        Rows.Remove(row);
        OnPropertyChanged(nameof(HasRows));
        _ = ValidateAsync();
    }


    // ==================== 内部 ====================

    /// <summary>
    /// 逐行校验（线程池上计算）。连续多次触发时只采用最后一次的结果。
    /// </summary>
    private async Task ValidateAsync(string? note = null)
    {
        if (_inspector is not { } inspector)
            return;

        var version = ++_validation;
        var rows = SnapshotRows();
        Result<WiringAssignPreview> preview;
        try
        {
            preview = await Task.Run(() => inspector.Wiring.PlanAssign(rows));
        }
        catch (Exception ex)
        {
            preview = Result<WiringAssignPreview>.Failure(new Error("Wiring.DraftFailed", ex.Message));
        }

        if (version != _validation)
            return;

        if (preview.IsFailure)
        {
            _preview = null;
            ShowMessage(preview.Error.Message, isError: true);
            RaiseCommands();
            return;
        }

        ApplyPreview(preview.Value);
        var problems = preview.Value.ProblemCount;
        var summary = Rows.Count == 0
            ? null
            : problems == 0 ? $"共 {Rows.Count} 路，可以确认。" : $"共 {Rows.Count} 路，其中 {problems} 行有问题。";
        if (summary is null && note is null)
            Message = null;
        else
            ShowMessage(string.Join(" ", new[] { summary, note }.Where(s => s is not null)), isError: problems > 0);
    }

    private IReadOnlyList<WiringDraftRow> SnapshotRows()
        => Rows.Select(r => new WiringDraftRow(r.Position.Id, r.Channel.Channel) { Purpose = r.Purpose }).ToList();

    private void ApplyPreview(WiringAssignPreview preview)
    {
        _preview = preview.Rows.Count == Rows.Count ? preview : null;
        if (_preview is not null)
        {
            for (var i = 0; i < Rows.Count; i++)
                Rows[i].Problem = preview.Rows[i].Problem;
        }

        RaiseCommands();
    }

    private void RebuildPositions()
    {
        if (_scope is not { } scope || _topology is not { } topology || _selectedLevel is not { } level)
        {
            Positions = [];
            return;
        }

        // 勾选按节点保留：换筛选、刷新接线后已勾的位置不丢
        var checkedIds = _positions.Where(p => p.IsChecked).Select(p => p.Node.Id).ToHashSet();
        var map = _inspector?.Wiring.Current;
        IReadOnlyList<TopologyNode> nodes = level.Level == scope.Level ? [scope] : topology.GetDescendants(scope.Address, level.Level);
        var list = new List<DraftPositionViewModel>(nodes.Count);
        foreach (var node in nodes)
        {
            // "缺少某功能"：没有这种功能的接线，或声明的这种功能的接点还有没接的
            if (_filter.Missing is { } kind && map is not null
                && map.GetEffective(node.Id).Any(e => e.Wire.Kind == kind)
                && !map.GetPortCoverage(node.Id).Any(p => !p.IsConnected && p.Required.Port.Kind == kind))
                continue;

            var item = new DraftPositionViewModel(node, OnPositionCheckedChanged);
            if (checkedIds.Contains(node.Id))
                item.SetChecked(true);
            list.Add(item);
        }

        Positions = list;
    }

    private void SetAllPositions(bool value)
    {
        foreach (var position in _positions)
            position.SetChecked(value);
        OnPositionCheckedChanged();
    }

    private void OnPositionCheckedChanged()
    {
        OnPropertyChanged(nameof(SelectedPositionCount));
        OnPropertyChanged(nameof(PositionCountText));
        AssembleCommand.RaiseCanExecuteChanged();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        AssembleCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void ShowMessage(string message, bool isError = false)
    {
        IsError = isError;
        Message = message;
    }
}
