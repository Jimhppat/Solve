using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 整模块接线预览的一行：通道 → 位置，以及这一行的问题。拖来的是模块的通道，所以按通道逐行列出，模块的每一路都在表里
/// </summary>
/// <param name="CodeText">通道编号，如"CH1"</param>
/// <param name="LabelText">通道别名或组名，如"负载"</param>
/// <param name="PositionText">接到的位置编码（有名称时带名称）；null 表示这路通道这次不接（选中的层级位置不够分）</param>
/// <param name="Problem">校验问题；null 表示这一行可以写入</param>
public sealed record ModuleDropRow(string CodeText, string LabelText, string? PositionText, string? Problem)
{
    public bool HasProblem => Problem is not null;

    public bool IsSkipped => PositionText is null;

    public string TargetText => PositionText ?? "不接";
}

/// <summary>
/// 把整个模块拖到硬件映射页中间后的接线预览：选层级 → 逐行校验 → 确认才写库。
/// </summary>
/// <remarks>
/// <para>层级为"本位置"时模块的全部启用通道都接到放下的位置上；选下面某一层时按位置编码顺序一路通道配一个位置，
/// 多出的通道或位置不接，并写明数量。</para>
/// <para>整批要么全部接上、要么一条不写（<see cref="IWiringService.PlanAssign"/>）；确认时按当前接线与拓扑重新校验，
/// 预览之后被别处改过也不会写入过期的配对。只能在 UI 线程上使用；校验在线程池上计算。</para>
/// </remarks>
public sealed class ModuleDropViewModel : ViewModelBase
{
    private readonly Func<string> _purpose;
    private readonly Action<TopologyNode, string> _onApplied;
    private WiringInspector? _inspector;
    private MappingModuleOption? _module;
    private TopologyNode? _node;
    private EquipmentTopology? _topology;
    private IReadOnlyList<WiringLevelOption> _levelOptions = [];
    private WiringLevelOption? _selectedLevel;
    private IReadOnlyList<WiringDraftRow> _pairs = [];
    private IReadOnlyList<ModuleDropRow> _rows = [];
    private WiringAssignPreview? _preview;
    private string? _note;
    private string? _message;
    private bool _isError;
    private bool _busy;
    private int _validation;

    /// <param name="purpose">确认时一并写入的用途（取页面上的"关联用途"），可为空串</param>
    /// <param name="onApplied">写库成功后：放下的位置与结果说明</param>
    public ModuleDropViewModel(Func<string> purpose, Action<TopologyNode, string> onApplied)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(onApplied);
        _purpose = purpose;
        _onApplied = onApplied;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !_busy && _preview?.Plan is not null);
        CancelCommand = new RelayCommand(Close, () => !_busy);
    }

    public bool IsOpen => _module is not null && _node is not null;

    /// <summary>
    /// 如"GJ DA750-4-60A-02 · 地址 2 → 层位 C01-L02"
    /// </summary>
    public string? TitleText => _module is { } module && _node is { } node
        ? $"{module.Text} → {node.Level.DisplayName()} {node.Address}"
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
            // 下拉框换列表时会先送来 null：不当作操作员换了层级
            if (value is null || !SetProperty(ref _selectedLevel, value))
                return;

            Pair();
        }
    }

    public IReadOnlyList<ModuleDropRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
                OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => _rows.Count > 0;

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

    public bool IsBusy => _busy;

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 接入接线运行视图。须在 UI 线程上调用。
    /// </summary>
    public void Attach(WiringInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>
    /// 模块放到了一个位置上：列出层级，默认选通道能分完的最近一层（都分不完时选位置最多的一层，没有下级时为本位置），生成预览
    /// </summary>
    public void Open(MappingModuleOption module, TopologyNode node, EquipmentTopology topology)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(topology);
        if (_busy || node.Level == TopologyLevel.System || module.Channels.Count == 0)
            return;

        _module = module;
        _node = node;
        _topology = topology;
        Message = null;

        var options = new List<WiringLevelOption> { new(node.Level, 1, isSelf: true) };
        for (var level = node.Level + 1; level <= TopologyLevel.Product; level++)
        {
            var count = topology.GetDescendants(node.Address, level).Count;
            if (count > 0)
                options.Add(new WiringLevelOption(level, count, isSelf: false));
        }

        var lower = options.Skip(1).ToList();
        var channels = module.Channels.Count;
        var preferred = lower.FirstOrDefault(o => o.Count >= channels)
                        ?? lower.OrderByDescending(o => o.Count).FirstOrDefault()
                        ?? options[0];

        LevelOptions = options;
        _selectedLevel = preferred;
        OnPropertyChanged(nameof(SelectedLevel));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(TitleText));
        Pair();
    }

    /// <summary>
    /// 关闭预览，不写入任何接线
    /// </summary>
    public void Close()
    {
        if (_busy)
            return;

        _validation++;
        _module = null;
        _node = null;
        _topology = null;
        _pairs = [];
        _preview = null;
        _note = null;
        LevelOptions = [];
        _selectedLevel = null;
        OnPropertyChanged(nameof(SelectedLevel));
        Rows = [];
        Message = null;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(TitleText));
        RaiseCommands();
    }

    /// <summary>
    /// 接线在别处变了：重新校验仍打开的预览
    /// </summary>
    public void OnExternalChanged()
    {
        if (IsOpen && !_busy)
            _ = ValidateAsync();
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 按选中的层级配对：本位置收下全部通道；下级按位置编码顺序一路通道一个位置
    /// </summary>
    private void Pair()
    {
        if (_module is not { } module || _node is not { } node || _topology is not { } topology || _selectedLevel is not { } level)
            return;

        var channels = module.Channels;
        var pairs = new List<WiringDraftRow>(channels.Count);
        var positions = new List<TopologyNode>(channels.Count);
        if (level.Level == node.Level)
        {
            foreach (var channel in channels)
            {
                pairs.Add(new WiringDraftRow(node.Id, channel.Channel));
                positions.Add(node);
            }

            _note = null;
        }
        else
        {
            var targets = topology.GetDescendants(node.Address, level.Level);
            var count = Math.Min(targets.Count, channels.Count);
            for (var i = 0; i < count; i++)
            {
                pairs.Add(new WiringDraftRow(targets[i].Id, channels[i].Channel));
                positions.Add(targets[i]);
            }

            _note = channels.Count > targets.Count
                ? $"模块还有 {channels.Count - targets.Count} 路通道不接。"
                : targets.Count > channels.Count
                    ? $"还有 {targets.Count - channels.Count} 个{level.Level.DisplayName()}分不到通道。"
                    : null;
        }

        // 每一路通道都列出：分不到位置的标"不接"，操作员能直接看出是哪几路没接
        _pairs = pairs;
        _preview = null;
        Rows = channels.Select((channel, i) => new ModuleDropRow(
            channel.CodeText,
            channel.LabelText,
            i < positions.Count ? Describe(positions[i]) : null,
            null)).ToList();
        RaiseCommands();
        _ = ValidateAsync();
    }

    /// <summary>
    /// 逐行校验（线程池上计算）。连续多次触发时只采用最后一次的结果。
    /// </summary>
    private async Task ValidateAsync()
    {
        if (_inspector is not { } inspector || _pairs.Count == 0)
            return;

        var version = ++_validation;
        var pairs = WithPurpose(_pairs);
        Result<WiringAssignPreview> preview;
        try
        {
            preview = await Task.Run(() => inspector.Wiring.PlanAssign(pairs));
        }
        catch (Exception ex)
        {
            // 在 UI 线程上继续：异常不能逃逸，否则会触发全站安全跳闸
            preview = Result<WiringAssignPreview>.Failure(new Error("Wiring.ModuleDropFailed", ex.Message));
        }

        if (version != _validation || !IsOpen)
            return;

        if (preview.IsFailure)
        {
            _preview = null;
            ShowMessage(preview.Error.Message, isError: true);
            RaiseCommands();
            return;
        }

        ApplyPreview(preview.Value);
    }

    private async Task ConfirmAsync()
    {
        if (_inspector is not { } inspector || _node is not { } node || _pairs.Count == 0 || _busy)
            return;

        var pairs = WithPurpose(_pairs);
        SetBusy(true);
        try
        {
            Result result;
            int added;
            try
            {
                // 确认时按当前接线与拓扑重新校验：预览之后位置可能已被删除，或同一位置已接了同一路通道
                var preview = await Task.Run(() => inspector.Wiring.PlanAssign(pairs));
                if (preview.IsFailure)
                {
                    result = Result.Failure(preview.Error);
                    added = 0;
                }
                else if (preview.Value.Plan is not { } plan)
                {
                    ApplyPreview(preview.Value);
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
                result = Result.Failure(new Error("Wiring.ModuleDropFailed", ex.Message));
                added = 0;
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return;
            }

            var text = $"已接线 {added} 路：{TitleText}";
            SetBusy(false);
            Close();
            _onApplied(node, text);
        }
        finally
        {
            if (_busy)
                SetBusy(false);
        }
    }

    private IReadOnlyList<WiringDraftRow> WithPurpose(IReadOnlyList<WiringDraftRow> pairs)
    {
        var purpose = _purpose();
        return pairs.Select(p => p with { Purpose = purpose }).ToList();
    }

    private static string Describe(TopologyNode node)
        => node.Name is { } name ? $"{node.Address}（{name}）" : node.Address.ToString();

    private void ApplyPreview(WiringAssignPreview preview)
    {
        // 校验结果按配对顺序逐行对应；表里配对行在前，"不接"的行在后、不参与校验
        if (preview.Rows.Count != _pairs.Count || _rows.Count < _pairs.Count)
        {
            _preview = null;
            RaiseCommands();
            return;
        }

        _preview = preview;
        Rows = _rows.Select((row, i) => i < preview.Rows.Count ? row with { Problem = preview.Rows[i].Problem } : row).ToList();
        var problems = preview.ProblemCount;
        var summary = problems == 0
            ? $"接 {_pairs.Count} 路通道，可以确认。"
            : $"接 {_pairs.Count} 路通道，其中 {problems} 行有问题，不能确认。";
        ShowMessage(_note is null ? summary : $"{summary} {_note}", isError: problems > 0);
        RaiseCommands();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommands();
    }

    private void ShowMessage(string message, bool isError = false)
    {
        IsError = isError;
        Message = message;
    }

    private void RaiseCommands()
    {
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
