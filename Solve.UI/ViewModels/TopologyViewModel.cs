using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 拓扑页保存流程的状态
/// </summary>
public enum TopologySaveState
{
    /// <summary>
    /// 可以保存
    /// </summary>
    Idle,

    /// <summary>
    /// 正在从配置库重新读取
    /// </summary>
    Loading,

    /// <summary>
    /// 正在计算会新增、删除、启用、停用哪些节点
    /// </summary>
    Planning,

    /// <summary>
    /// 变更会删除位置、解除停用、新增可用位置或改变位置编码，等待确认；确认前什么都没有改
    /// </summary>
    AwaitingConfirmation,

    /// <summary>
    /// 正在写入配置库
    /// </summary>
    Saving,
}

/// <summary>
/// 设备拓扑页：尚未建档时按统一布局一次生成；建档后在树上选中节点，右侧即是它的属性表单（见 TopologyViewModel.NodeEditor.cs），
/// 改完一次保存。每个节点的下级数量可以各不相同。
/// </summary>
/// <remarks>
/// <para>所有修改共用一个保存流程：先计算变更；会删除位置、解除停用、新增可用位置或改变位置编码时停在
/// <see cref="TopologySaveState.AwaitingConfirmation"/>，在保存按钮旁列出受影响的位置，确认后才写库；
/// 取消、改动输入或换选节点则什么都不改。</para>
/// <para>建档后不再提供整机统一布局：它会把规格不同的节点补齐成一样，悄悄加回有意删掉的缺位。</para>
/// <para>保存后整棵树按新拓扑重建，选中项与展开状态按节点 Id 保留。</para>
/// <para>只能在 UI 线程上使用；变更计算与磁盘 I/O 在线程池上完成。</para>
/// </remarks>
public sealed partial class TopologyViewModel : ViewModelBase
{
    private const string DefaultSystemName = "设备";

    // 页面级的已展开节点：节点 Id 终身不变，树重建后同一节点照样展开
    private readonly HashSet<TopologyNodeId> _expanded = [];

    private ITopologyService? _service;
    private TopologySaveState _state = TopologySaveState.Idle;
    private PendingSave? _pending;
    private string _systemNameInput = DefaultSystemName;
    private decimal? _cabinetsInput = 1;
    private decimal? _layersInput = 1;
    private decimal? _slotsInput = 1;
    private decimal? _fixturesInput = 1;
    private decimal? _productsInput = 1;
    private string? _saveMessage;
    private bool _isSaveError;
    private IReadOnlyList<TopologyTreeItemViewModel> _roots = [];
    private TopologyTreeItemViewModel? _selectedItem;

    public TopologyViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveLayoutAsync, () => IsEditable && !IsConfigured && _state == TopologySaveState.Idle);
        SaveNodeCommand = new AsyncRelayCommand(SaveNodeAsync, () => IsNodeEditable && HasNodeChanges && _state == TopologySaveState.Idle);
        RevertNodeCommand = new RelayCommand(RevertNode, () => HasNodeChanges && IsInputPhase);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => CanCopyOrRemove && _state == TopologySaveState.Idle);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanCopyOrRemove && _state == TopologySaveState.Idle);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => _state == TopologySaveState.AwaitingConfirmation);
        CancelCommand = new RelayCommand(Cancel, () => _state == TopologySaveState.AwaitingConfirmation);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _service is not null && _state == TopologySaveState.Idle);
    }

    public string PageTitle => "设备拓扑";

    /// <summary>
    /// 按页面上的布局首次建档（建档后不可用，结构改为按节点编辑）
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// 确认列出的删除 / 启用 / 位置编码变化并保存
    /// </summary>
    public AsyncRelayCommand ConfirmCommand { get; }

    /// <summary>
    /// 放弃这次保存，什么都不改
    /// </summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 从配置库重新读取（读取失败后重试）
    /// </summary>
    public AsyncRelayCommand ReloadCommand { get; }

    public TopologySaveState State => _state;

    public bool IsAwaitingConfirmation => _state == TopologySaveState.AwaitingConfirmation;

    // ==================== 读取状态与概况 ====================

    /// <summary>
    /// 已接入拓扑服务且读取成功（含尚未建档），可以编辑与保存
    /// </summary>
    public bool IsEditable => _service is { LoadError: null };

    /// <summary>
    /// 布局输入框是否可用：可编辑且没有正在进行的读取 / 计算 / 保存
    /// </summary>
    public bool IsInputEnabled => IsEditable && IsInputPhase;

    public string? LoadErrorText => _service?.LoadError is { } error ? $"拓扑读取失败，已禁止保存：{error.Message}" : null;

    public bool HasLoadError => LoadErrorText is not null;

    public bool IsConfigured => Current is not null;

    /// <summary>
    /// 卡片标题旁的一行概况：已建档时是各层数量；尚未建档时是按当前布局输入保存后的规模（输入有误时是原因）
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (_service is null)
                return "配置库不可用";
            if (_service.LoadError is not null)
                return "拓扑未能读取";
            if (Current is not { } topology)
                return LayoutPreviewText;

            var levels = TopologyLevels.All.Skip(1)
                .Select(level => $"{level.DisplayName()} {Format(topology.CountAt(level))}");
            return $"{string.Join(" · ", levels)}（共 {Format(topology.NodeCount)} 个节点）";
        }
    }

    public bool IsSummaryError => IsEditable && !IsConfigured && IsLayoutPreviewError;

    // ==================== 布局输入（首次建档） ====================

    public string SystemNameInput
    {
        get => _systemNameInput;
        set
        {
            if (SetProperty(ref _systemNameInput, value ?? string.Empty))
                RaiseLayoutPreviewChanged();
        }
    }

    public decimal? CabinetsInput
    {
        get => _cabinetsInput;
        set => SetCount(ref _cabinetsInput, value);
    }

    public decimal? LayersPerCabinetInput
    {
        get => _layersInput;
        set => SetCount(ref _layersInput, value);
    }

    public decimal? SlotsPerLayerInput
    {
        get => _slotsInput;
        set => SetCount(ref _slotsInput, value);
    }

    public decimal? FixturesPerSlotInput
    {
        get => _fixturesInput;
        set => SetCount(ref _fixturesInput, value);
    }

    public decimal? ProductsPerFixtureInput
    {
        get => _productsInput;
        set => SetCount(ref _productsInput, value);
    }

    // 数字输入框的范围，与 TopologyAddress.MaxOrdinal 一致；下级数量允许 0（空容器）
    public decimal MinCount => 1;

    public decimal MinChildCount => 0;

    public decimal MaxCount => TopologyAddress.MaxOrdinal;

    /// <summary>
    /// 按当前输入保存后的规模，或输入有误的原因
    /// </summary>
    public string LayoutPreviewText
    {
        get
        {
            var layout = ReadLayout();
            return layout.IsFailure
                ? layout.Error.Message
                : $"保存后共 {Format(layout.Value.TotalNodeCount)} 个节点，其中产品位 {Format(layout.Value.ProductCount)} 个";
        }
    }

    public bool IsLayoutPreviewError => ReadLayout().IsFailure;

    // ==================== 确认与结果 ====================

    /// <summary>
    /// 等待确认时的说明：每类影响一行
    /// </summary>
    public string? PendingConfirmationText => _pending is { } pending ? DescribeConfirmation(pending.Plan) : null;

    public string? SaveMessage
    {
        get => _saveMessage;
        private set
        {
            if (SetProperty(ref _saveMessage, value))
                RaiseMessageChanged();
        }
    }

    public bool HasSaveMessage => _saveMessage is not null;

    /// <summary>
    /// 尚未建档（或读取失败）时，结果显示在页面上方；建档后显示在节点表单的保存按钮旁
    /// </summary>
    public bool HasPageMessage => HasSaveMessage && !IsConfigured;

    public bool HasNodeMessage => HasSaveMessage && IsConfigured;

    public bool IsSaveError
    {
        get => _isSaveError;
        private set => SetProperty(ref _isSaveError, value);
    }

    // ==================== 节点浏览 ====================

    /// <summary>
    /// 树的根（只有系统根一个元素）；尚未建档时为空
    /// </summary>
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

            // 换了节点：表单改为显示新节点的值；等待确认的修改已不是眼前这个节点，取消；上一个节点的结果提示也不再相关
            CancelPending();
            IsSaveError = false;
            SaveMessage = null;
            ShowNodeInputs();
            RaiseSelectionChanged();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    // ==================== 接入与命令 ====================

    /// <summary>
    /// 接入拓扑服务。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void Attach(ITopologyService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (_service is not null)
            throw new InvalidOperationException("拓扑页已接入拓扑服务");

        _service = service;
        ShowCurrent();
    }

    private async Task SaveLayoutAsync()
    {
        // 只用于首次建档：建档后按统一布局保存会把规格不同的节点补齐成一样，把有意删掉的缺位悄悄加回来
        if (_service is not { } service || !IsEditable || IsConfigured || _state != TopologySaveState.Idle)
            return;

        var layout = ReadLayout();
        if (layout.IsFailure)
        {
            ReportError(layout.Error.Message);
            return;
        }

        var name = _systemNameInput.Trim();
        if (name.Length == 0)
        {
            ReportError("系统名称不能为空");
            return;
        }

        var plan = await PlanAsync(() => service.PlanLayout(name, layout.Value));
        if (plan is null)
            return;

        await SaveOrAwaitConfirmationAsync(service,
            new PendingSave(plan, $"已建档，共 {Format(plan.Candidate.NodeCount)} 个节点"));
    }

    /// <summary>
    /// 在线程池上计算变更（十万级节点的拓扑重建与比对需要数百毫秒）。失败时已显示原因并返回 null。
    /// </summary>
    private async Task<TopologyChangePlan?> PlanAsync(Func<Result<TopologyChangePlan>> plan)
    {
        SetState(TopologySaveState.Planning);
        IsSaveError = false;
        SaveMessage = null;

        Result<TopologyChangePlan> result;
        try
        {
            result = await Task.Run(plan);
        }
        catch (Exception ex)
        {
            // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
            result = Result<TopologyChangePlan>.Failure(new Error("Topology.PlanFailed", ex.Message));
        }

        SetState(TopologySaveState.Idle);
        if (result.IsSuccess)
            return result.Value;

        ReportError(result.Error.Message);
        return null;
    }

    private async Task SaveOrAwaitConfirmationAsync(ITopologyService service, PendingSave pending)
    {
        if (!pending.Plan.RequiresConfirmation)
        {
            await CommitAsync(service, pending);
            return;
        }

        // 什么都还没改：等确认。确认前改动输入或换选节点都会取消这次保存
        SetState(TopologySaveState.AwaitingConfirmation);
        _pending = pending;
        OnPropertyChanged(nameof(PendingConfirmationText));
    }

    private async Task ConfirmAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.AwaitingConfirmation || _pending is not { } pending)
            return;

        await CommitAsync(service, pending);
    }

    private void Cancel() => CancelPending();

    private async Task CommitAsync(ITopologyService service, PendingSave pending)
    {
        SetState(TopologySaveState.Saving);
        IsSaveError = false;
        try
        {
            Result result;
            try
            {
                result = await service.SaveAsync(pending.Plan, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = Result.Failure(new Error("Topology.SaveFailed", ex.Message));
            }

            if (result.IsFailure)
            {
                ReportError(result.Error.Message);
                return;
            }

            // 下级数量调大后展开它，新位置直接可见
            if (pending.Expand is { } expand)
                _expanded.Add(expand);

            ShowCurrent(pending.Select);
            SaveMessage = pending.SuccessMessage;
        }
        finally
        {
            SetState(TopologySaveState.Idle);
        }
    }

    private async Task ReloadAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle)
            return;

        SetState(TopologySaveState.Loading);
        IsSaveError = false;
        SaveMessage = null;
        try
        {
            Result result;
            try
            {
                result = await service.LoadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = Result.Failure(new Error("Topology.LoadFailed", ex.Message));
            }

            ShowCurrent();
            if (result.IsFailure)
                ReportError(result.Error.Message);
        }
        finally
        {
            SetState(TopologySaveState.Idle);
        }
    }

    // ==================== 内部 ====================

    private EquipmentTopology? Current => _service?.Current;

    private bool IsInputPhase => _state is TopologySaveState.Idle or TopologySaveState.AwaitingConfirmation;

    /// <summary>
    /// 显示当前生效的拓扑并重建树。选中 <paramref name="select"/>，未指定时按 Id 保留原选中项（节点已不存在时回到系统根），
    /// 展开状态见 <see cref="_expanded"/>。
    /// </summary>
    private void ShowCurrent(TopologyNodeId? select = null)
    {
        var keep = select ?? _selectedItem?.Node.Id;
        if (Current is { } topology)
        {
            // 第一次显示时展开系统根，直接看到全部柜体
            if (_expanded.Count == 0)
                _expanded.Add(topology.Root.Id);

            // 先换树再选中：树控件在数据源更换时会把选中项清空并回写
            Roots = [new TopologyTreeItemViewModel(topology.Root, _expanded)];
            SelectedItem = FindItem(topology, keep) ?? Roots[0];
        }
        else
        {
            Roots = [];
            SelectedItem = null;
        }

        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsInputEnabled));
        OnPropertyChanged(nameof(LoadErrorText));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsConfigured));
        RaiseLayoutPreviewChanged();
        RaiseMessageChanged();

        // 选中的还是同一个节点时 SelectedItem 不会触发变更，这里统一刷新表单（节点内容可能已变）
        ShowNodeInputs();
        RaiseSelectionChanged();
        RaiseCommandsChanged();
    }

    /// <summary>
    /// 按 Id 在新树里找到节点：沿位置编码逐层展开，祖先都标记为展开，使选中项在树上可见
    /// </summary>
    private TopologyTreeItemViewModel? FindItem(EquipmentTopology topology, TopologyNodeId? id)
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

    private void SetCount(ref decimal? field, decimal? value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
            RaiseLayoutPreviewChanged();
    }

    /// <summary>
    /// 取消等待确认的保存（若有），回到可编辑状态。被取消的确认框消失即是反馈，不另外提示。
    /// </summary>
    private void CancelPending()
    {
        if (_state != TopologySaveState.AwaitingConfirmation)
            return;

        SetState(TopologySaveState.Idle);
    }

    private Result<TopologyLayout> ReadLayout()
    {
        var counts = new[]
        {
            (_cabinetsInput, "柜体数"),
            (_layersInput, "每柜层位数"),
            (_slotsInput, "每层槽位数"),
            (_fixturesInput, "每槽治具位数"),
            (_productsInput, "每治具产品位数"),
        };

        var values = new int[counts.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            var (value, label) = counts[i];
            if (!TryReadCount(value, label, MinCount, out values[i], out var error))
                return Result<TopologyLayout>.Failure(new Error(TopologyErrors.InvalidLayoutCode, error));
        }

        var layout = new TopologyLayout(values[0], values[1], values[2], values[3], values[4]);
        var errors = layout.Validate();
        return errors.Count == 0
            ? Result<TopologyLayout>.Success(layout)
            : Result<TopologyLayout>.Failure(errors[0]);
    }

    /// <summary>
    /// 读取数量 / 序号输入框：必须填写，且是 [<paramref name="min"/>, <see cref="MaxCount"/>] 之间的整数
    /// </summary>
    private bool TryReadCount(decimal? value, string label, decimal min, out int count, [NotNullWhen(false)] out string? error)
    {
        count = 0;
        if (value is not { } number)
        {
            error = $"请填写{label}";
            return false;
        }

        // 先在 decimal 上判断范围与是否为整数，再转 int：手输的超大数直接转换会溢出
        if (number != decimal.Truncate(number) || number < min || number > MaxCount)
        {
            error = $"{label}必须是 {min:0}～{MaxCount:0} 之间的整数";
            return false;
        }

        count = (int)number;
        error = null;
        return true;
    }

    private void ReportError(string message)
    {
        IsSaveError = true;
        SaveMessage = message;
    }

    private void SetState(TopologySaveState state)
    {
        if (state != TopologySaveState.AwaitingConfirmation)
            _pending = null;

        if (_state == state)
            return;

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsAwaitingConfirmation));
        OnPropertyChanged(nameof(IsInputEnabled));
        OnPropertyChanged(nameof(IsNodeInputEnabled));
        OnPropertyChanged(nameof(PendingConfirmationText));
        RaiseCommandsChanged();
    }

    private void RaiseCommandsChanged()
    {
        SaveCommand.RaiseCanExecuteChanged();
        SaveNodeCommand.RaiseCanExecuteChanged();
        RevertNodeCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }

    private void RaiseLayoutPreviewChanged()
    {
        OnPropertyChanged(nameof(LayoutPreviewText));
        OnPropertyChanged(nameof(IsLayoutPreviewError));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsSummaryError));
    }

    private void RaiseMessageChanged()
    {
        OnPropertyChanged(nameof(HasSaveMessage));
        OnPropertyChanged(nameof(HasPageMessage));
        OnPropertyChanged(nameof(HasNodeMessage));
    }

    private static string DescribeConfirmation(TopologyChangePlan plan)
    {
        var lines = new List<string>();
        if (plan.RemovedCount > 0)
            lines.Add($"将删除 {ListPositions(plan.RemovedRoots)} 及其下级，共 {Positions(plan.RemovedCount, plan.RemovedProductCount)}");

        if (plan.AddedEnabledCount > 0)
            lines.Add($"将新增并启用 {ListPositions(plan.AddedEnabledRoots)}，共 {Positions(plan.AddedEnabledCount, plan.AddedEnabledProductCount)}");

        if (plan.ReenabledCount > 0)
            lines.Add($"将重新启用 {ListPositions(plan.ReenabledRoots)}，共 {Positions(plan.ReenabledCount, plan.ReenabledProductCount)}");

        // 位置编码改变不影响系统内部（按 Id 引用），但现场按编码贴的标牌会对不上
        if (plan.RenumberedCount > 0)
            lines.Add($"位置编码将改变 {ListPositions(plan.RenumberedRoots)}，共 {Format(plan.RenumberedCount)} 个位置，现场标牌需同步更换");

        return string.Join(Environment.NewLine, lines);

        static string Positions(int count, int products)
            => products > 0 ? $"{Format(count)} 个位置（产品位 {Format(products)} 个）" : $"{Format(count)} 个位置";
    }

    private static string ListPositions<T>(IReadOnlyList<T> positions)
    {
        var listed = string.Join("、", positions.Take(TopologyChangePlan.MaxListedPositions));
        return positions.Count > TopologyChangePlan.MaxListedPositions ? $"{listed} 等 {positions.Count} 处" : listed;
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <param name="SuccessMessage">保存成功后的提示</param>
    /// <param name="Select">保存后选中的节点；为 null 时按 Id 保留原选中项</param>
    /// <param name="Expand">保存后展开的节点</param>
    private sealed record PendingSave(
        TopologyChangePlan Plan,
        string SuccessMessage,
        TopologyNodeId? Select = null,
        TopologyNodeId? Expand = null);
}
