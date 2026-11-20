using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring;
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
/// 设备拓扑页：顶部的一键拓扑按统一布局生成整机或选中节点的下级（见 TopologyViewModel.Layout.cs）；
/// 下方在树上选中节点，右侧即是它的属性表单（见 TopologyViewModel.NodeEditor.cs）。
/// </summary>
/// <remarks>
/// <para>两处修改共用一个保存流程：先计算变更；会删除位置、解除停用、新增可用位置或改变位置编码时停在
/// <see cref="TopologySaveState.AwaitingConfirmation"/>，在发起操作的那一处列出受影响的位置，确认后才写库；
/// 取消或改动那一处的输入则什么都不改。结果与错误也显示在发起操作的那一处。</para>
/// <para>保存后整棵树按新拓扑重建，选中项与展开状态按节点 Id 保留。</para>
/// <para>只能在 UI 线程上使用；变更计算与磁盘 I/O 在线程池上完成。</para>
/// </remarks>
public sealed partial class TopologyViewModel : ViewModelBase, IDisposable
{
    // 页面级的已展开节点：节点 Id 终身不变，树重建后同一节点照样展开
    private readonly HashSet<TopologyNodeId> _expanded = [];

    private ITopologyService? _service;
    private TopologySaveState _state = TopologySaveState.Idle;
    private PendingSave? _pending;
    private string? _saveMessage;
    private PendingOrigin _messageOrigin;
    private bool _isSaveError;
    private IReadOnlyList<TopologyTreeItemViewModel> _roots = [];
    private TopologyTreeItemViewModel? _selectedItem;

    public TopologyViewModel()
    {
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => IsEditable && _state == TopologySaveState.Idle);
        SaveNodeCommand = new AsyncRelayCommand(SaveNodeAsync, () => IsNodeEditable && HasNodeChanges && _state == TopologySaveState.Idle);
        RevertNodeCommand = new RelayCommand(RevertNode, () => HasNodeChanges && IsInputPhase);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => CanCopyOrRemove && _state == TopologySaveState.Idle);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanCopyOrRemove && _state == TopologySaveState.Idle);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => _state == TopologySaveState.AwaitingConfirmation);
        CancelCommand = new RelayCommand(Cancel, () => _state == TopologySaveState.AwaitingConfirmation);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _service is not null && _state == TopologySaveState.Idle);
        ShowLayoutInputs();
    }

    public string PageTitle => "设备拓扑";

    /// <summary>
    /// 选中位置的接线面板；接入接线服务前不显示
    /// </summary>
    public WiringPanelViewModel Wiring { get; } = new();

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

    /// <summary>
    /// 一键拓扑发起的保存在等待确认：确认框显示在一键拓扑卡里
    /// </summary>
    public bool IsLayoutAwaitingConfirmation => IsAwaitingConfirmation && _pending?.Origin == PendingOrigin.Layout;

    /// <summary>
    /// 节点表单发起的保存在等待确认：确认框显示在保存按钮的位置
    /// </summary>
    public bool IsNodeAwaitingConfirmation => IsAwaitingConfirmation && _pending?.Origin == PendingOrigin.Node;

    // ==================== 读取状态与概况 ====================

    /// <summary>
    /// 已接入拓扑服务且读取成功（含尚未建档），可以编辑与保存
    /// </summary>
    public bool IsEditable => _service is { LoadError: null };

    public string? LoadErrorText => _service?.LoadError is { } error ? $"拓扑读取失败，已禁止保存：{error.Message}" : null;

    public bool HasLoadError => LoadErrorText is not null;

    public bool IsConfigured => Current is not null;

    // 数字输入框的范围，与 TopologyAddress.MaxOrdinal 一致；下级数量允许 0（空容器）
    public decimal MinCount => 1;

    public decimal MinChildCount => 0;

    public decimal MaxCount => TopologyAddress.MaxOrdinal;

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
    /// 结果显示在一键拓扑卡里（建档、一键生成、重新读取）
    /// </summary>
    public bool HasLayoutMessage => HasSaveMessage && _messageOrigin == PendingOrigin.Layout;

    /// <summary>
    /// 结果显示在节点表单的保存按钮旁
    /// </summary>
    public bool HasNodeMessage => HasSaveMessage && _messageOrigin == PendingOrigin.Node;

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

            // 换了节点：表单改为显示新节点的值；表单上等待确认的修改与结果提示都已不是眼前这个节点的，取消
            CancelPending(PendingOrigin.Node);
            ClearMessage(PendingOrigin.Node);
            ShowNodeInputs();
            OnSelectionChangedForLayout();
            RaiseSelectionChanged();
            Wiring.ShowNode(value?.Node);
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

    /// <summary>
    /// 接入接线运行视图（选中位置的接线面板；删除位置前提示会一并拆除的接线数）。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void AttachWiring(WiringInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        Wiring.Attach(inspector);
        Wiring.ShowNode(_selectedItem?.Node);
    }

    public void Dispose() => Wiring.Dispose();

    /// <summary>
    /// 在线程池上计算变更（十万级节点的拓扑重建与比对需要数百毫秒）。失败时已在 <paramref name="origin"/> 处显示原因并返回 null。
    /// </summary>
    private async Task<TopologyChangePlan?> PlanAsync(Func<Result<TopologyChangePlan>> plan, PendingOrigin origin)
    {
        SetState(TopologySaveState.Planning);
        ClearMessage();

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

        ShowMessage(origin, result.Error.Message, isError: true);
        return null;
    }

    private async Task SaveOrAwaitConfirmationAsync(ITopologyService service, PendingSave pending)
    {
        if (!pending.Plan.RequiresConfirmation)
        {
            await CommitAsync(service, pending);
            return;
        }

        // 什么都还没改：等确认。确认前改动发起处的输入都会取消这次保存
        SetState(TopologySaveState.AwaitingConfirmation);
        _pending = pending;
        RaisePendingChanged();
    }

    private async Task ConfirmAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.AwaitingConfirmation || _pending is not { } pending)
            return;

        await CommitAsync(service, pending);
    }

    private void Cancel() => CancelPending(_pending?.Origin);

    private async Task CommitAsync(ITopologyService service, PendingSave pending)
    {
        SetState(TopologySaveState.Saving);
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
                ShowMessage(pending.Origin, result.Error.Message, isError: true);
                return;
            }

            // 新增了下级的节点展开，新位置直接可见
            if (pending.Expand is { } expand)
                _expanded.Add(expand);

            ShowCurrent(pending.Select);
            ShowMessage(pending.Origin, pending.SuccessMessage);
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
        ClearMessage();
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
                ShowMessage(PendingOrigin.Layout, result.Error.Message, isError: true);
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
        OnPropertyChanged(nameof(LoadErrorText));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsConfigured));
        RaiseMessageChanged();

        // 选中的还是同一个节点时 SelectedItem 不会触发变更，这里统一刷新两处输入（节点内容可能已变）
        ShowNodeInputs();
        ShowLayoutInputs();
        RaiseSelectionChanged();
        RaiseCommandsChanged();

        // 节点内容可能已变（改名、停用、下级增减），接线面板按新节点重新显示
        Wiring.ShowNode(_selectedItem?.Node);
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

    /// <summary>
    /// 取消 <paramref name="origin"/> 处等待确认的保存（若有）。被取消的确认框消失即是反馈，不另外提示。
    /// </summary>
    private void CancelPending(PendingOrigin? origin)
    {
        if (_state != TopologySaveState.AwaitingConfirmation || _pending?.Origin != origin)
            return;

        SetState(TopologySaveState.Idle);
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

    private void ShowMessage(PendingOrigin origin, string message, bool isError = false)
    {
        _messageOrigin = origin;
        IsSaveError = isError;
        SaveMessage = message;
        RaiseMessageChanged();
    }

    /// <summary>
    /// 清除 <paramref name="origin"/> 处的结果提示；不指定时清除任何一处的
    /// </summary>
    private void ClearMessage(PendingOrigin? origin = null)
    {
        if (_saveMessage is null || (origin is { } only && _messageOrigin != only))
            return;

        IsSaveError = false;
        SaveMessage = null;
    }

    private void SetState(TopologySaveState state)
    {
        if (state != TopologySaveState.AwaitingConfirmation)
            _pending = null;

        if (_state == state)
            return;

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsLayoutInputEnabled));
        OnPropertyChanged(nameof(IsNodeInputEnabled));
        RaisePendingChanged();
        RaiseCommandsChanged();
    }

    private void RaisePendingChanged()
    {
        OnPropertyChanged(nameof(IsAwaitingConfirmation));
        OnPropertyChanged(nameof(IsLayoutAwaitingConfirmation));
        OnPropertyChanged(nameof(IsNodeAwaitingConfirmation));
        OnPropertyChanged(nameof(PendingConfirmationText));
    }

    private void RaiseCommandsChanged()
    {
        GenerateCommand.RaiseCanExecuteChanged();
        SaveNodeCommand.RaiseCanExecuteChanged();
        RevertNodeCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }

    private void RaiseMessageChanged()
    {
        OnPropertyChanged(nameof(HasSaveMessage));
        OnPropertyChanged(nameof(HasLayoutMessage));
        OnPropertyChanged(nameof(HasNodeMessage));
    }

    private string DescribeConfirmation(TopologyChangePlan plan)
    {
        var lines = new List<string>();
        if (plan.RemovedCount > 0)
        {
            lines.Add($"将删除 {ListPositions(plan.RemovedRoots)} 及其下级，共 {Positions(plan.RemovedCount, plan.RemovedProductCount)}");

            // 接线随位置一起删除（同一事务），现场的线不受影响但系统里的关联会消失
            var wires = Wiring.CountRemovedBy(plan.Candidate);
            if (wires > 0)
                lines.Add($"这些位置上的 {Format(wires)} 路接线将一并拆除");
        }

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

    /// <summary>
    /// 保存成功的提示：只在结构有增删时补一句数量
    /// </summary>
    private static string DescribeSaved(string done, TopologyChangePlan plan)
    {
        if (plan.AddedCount > 0)
            done += $"，新增 {Format(plan.AddedCount)} 个位置";
        if (plan.RemovedCount > 0)
            done += $"，删除 {Format(plan.RemovedCount)} 个位置";
        return done;
    }

    private static string ListPositions<T>(IReadOnlyList<T> positions)
    {
        var listed = string.Join("、", positions.Take(TopologyChangePlan.MaxListedPositions));
        return positions.Count > TopologyChangePlan.MaxListedPositions ? $"{listed} 等 {positions.Count} 处" : listed;
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 保存从哪一处发起：确认框、结果与错误都显示在那一处
    /// </summary>
    private enum PendingOrigin
    {
        Layout,
        Node,
    }

    /// <param name="SuccessMessage">保存成功后的提示</param>
    /// <param name="Select">保存后选中的节点；为 null 时按 Id 保留原选中项</param>
    /// <param name="Expand">保存后展开的节点</param>
    private sealed record PendingSave(
        TopologyChangePlan Plan,
        PendingOrigin Origin,
        string SuccessMessage,
        TopologyNodeId? Select = null,
        TopologyNodeId? Expand = null);
}
