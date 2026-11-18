using System;
using System.Collections.Generic;
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
    /// 变更会删除位置或解除停用，等待确认；确认前什么都没有改
    /// </summary>
    AwaitingConfirmation,

    /// <summary>
    /// 正在写入配置库
    /// </summary>
    Saving,
}

/// <summary>
/// 设备拓扑页：按统一布局建档 / 扩容 / 缩容，单个节点改名与停用，并浏览每个节点的两套身份（系统用 Id，人看位置编码与名称）。
/// </summary>
/// <remarks>
/// <para>两处修改共用一个保存流程：先计算变更；会删除位置或解除停用时停在 <see cref="TopologySaveState.AwaitingConfirmation"/>，
/// 列出受影响的位置，确认后才写库；取消或改动对应的输入则什么都不改。</para>
/// <para>保存后整棵树按新拓扑重建，选中项与展开状态按节点 Id 保留。</para>
/// <para>只能在 UI 线程上使用；变更计算与磁盘 I/O 在线程池上完成。</para>
/// </remarks>
public sealed class TopologyViewModel : ViewModelBase
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
    private string _nodeNameInput = string.Empty;
    private bool _nodeEnabledInput = true;
    private string? _saveMessage;
    private bool _isSaveError;
    private bool _isNonUniform;
    private IReadOnlyList<TopologyTreeItemViewModel> _roots = [];
    private TopologyTreeItemViewModel? _selectedItem;

    public TopologyViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveLayoutAsync, () => IsEditable && _state == TopologySaveState.Idle);
        SaveNodeCommand = new AsyncRelayCommand(SaveNodeAsync, () => IsNodeEditable && HasNodeChanges && _state == TopologySaveState.Idle);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => _state == TopologySaveState.AwaitingConfirmation);
        CancelCommand = new RelayCommand(Cancel, () => _state == TopologySaveState.AwaitingConfirmation);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _service is not null && _state == TopologySaveState.Idle);
    }

    public string PageTitle => "设备拓扑";

    /// <summary>
    /// 按页面上的布局保存
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// 保存选中节点的名称与启用状态
    /// </summary>
    public AsyncRelayCommand SaveNodeCommand { get; }

    /// <summary>
    /// 确认列出的删除 / 重新启用并保存
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

    public string? LoadErrorText => _service?.LoadError is { } error
        ? $"拓扑未能从配置库读取，已禁止保存，以免覆盖待修复的数据并丢失节点原有的 Id。{error.Message}"
        : null;

    public bool HasLoadError => LoadErrorText is not null;

    public bool IsConfigured => Current is not null;

    public string SummaryText
    {
        get
        {
            if (_service is null)
                return "配置库不可用或尚未就绪，拓扑功能暂不可用";
            if (_service.LoadError is not null)
                return "拓扑未能读取";
            if (Current is not { } topology)
                return "尚未建档：填写下方布局并保存，即生成全部节点，并为每个节点分配 Id";

            var levels = TopologyLevels.All.Skip(1)
                .Select(level => $"{level.DisplayName()} {Format(topology.CountAt(level))}");
            return $"{string.Join(" · ", levels)}（共 {Format(topology.NodeCount)} 个节点）";
        }
    }

    /// <summary>
    /// 系统根节点的 Id
    /// </summary>
    public string? SystemIdText => Current?.Root.Id.ToString();

    /// <summary>
    /// 当前拓扑不是统一布局（柜体规格不同或有缺位）：输入框预填的是各层最大数量，保存会改成统一布局
    /// </summary>
    public bool IsNonUniform
    {
        get => _isNonUniform;
        private set => SetProperty(ref _isNonUniform, value);
    }

    // ==================== 布局输入 ====================

    public string SystemNameInput
    {
        get => _systemNameInput;
        set
        {
            if (SetProperty(ref _systemNameInput, value ?? string.Empty))
                OnLayoutInputChanged();
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

    // 数字输入框的范围，与 TopologyAddress.MaxOrdinal 一致
    public decimal MinCount => 1;

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
    /// 等待确认时的说明：会删除哪些位置、重新启用哪些位置
    /// </summary>
    public string? PendingConfirmationText => _pending is { } pending ? DescribeConfirmation(pending.Plan) : null;

    public string? SaveMessage
    {
        get => _saveMessage;
        private set
        {
            if (SetProperty(ref _saveMessage, value))
            {
                OnPropertyChanged(nameof(HasSaveMessage));
                OnPropertyChanged(nameof(HasPageNotice));
            }
        }
    }

    public bool HasSaveMessage => _saveMessage is not null;

    /// <summary>
    /// 页面顶部的提示区有内容（等待确认或有保存结果）：布局与节点两处的保存共用这一处提示
    /// </summary>
    public bool HasPageNotice => IsAwaitingConfirmation || HasSaveMessage;

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

            // 换了节点：节点输入框改为显示新节点的值；正在等待确认的节点修改已不是眼前这个节点，取消
            CancelPending(PendingOrigin.Node, "已切换节点，修改未保存");
            ShowNodeInputs();
            RaiseSelectionChanged();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    public string? SelectedLevelText => _selectedItem?.Node.Level.DisplayName();

    /// <summary>
    /// 位置编码（人用）
    /// </summary>
    public string? SelectedAddressText => _selectedItem?.Node.Address.ToString();

    /// <summary>
    /// 节点 Id（系统用）
    /// </summary>
    public string? SelectedIdText => _selectedItem?.Node.Id.ToString();

    public string? SelectedStatusText
    {
        get
        {
            if (_selectedItem is not { } item || Current is not { } topology)
                return null;

            var check = topology.CheckEnabled(item.Node.Id);
            return check.IsSuccess ? "启用" : check.Error.Message;
        }
    }

    public string? SelectedChildrenText => _selectedItem?.Node is { } node
        ? node.Level.TryGetChildLevel(out var childLevel)
            ? $"{Format(node.Children.Count)} 个{childLevel.DisplayName()}"
            : "无（产品位是最底层）"
        : null;

    // ==================== 节点编辑 ====================

    /// <summary>
    /// 选中的节点可以改名与停用（系统根除外：名称在设备布局中修改，且不可停用）
    /// </summary>
    public bool IsNodeEditable => IsEditable && _selectedItem is { Node.Level: not TopologyLevel.System };

    public bool IsNodeInputEnabled => IsNodeEditable && IsInputPhase;

    /// <summary>
    /// 选中系统根时的说明
    /// </summary>
    public string? NodeEditHint => _selectedItem is { Node.Level: TopologyLevel.System }
        ? "系统名称请在上方「设备布局」中修改；系统根不可停用，需要整机停止时请使用紧急停止"
        : null;

    public bool HasNodeEditHint => NodeEditHint is not null;

    /// <summary>
    /// 选中节点的显示名；留空表示不命名，界面显示位置编码
    /// </summary>
    public string NodeNameInput
    {
        get => _nodeNameInput;
        set
        {
            if (SetProperty(ref _nodeNameInput, value ?? string.Empty))
                OnNodeInputChanged();
        }
    }

    /// <summary>
    /// 选中节点是否启用；停用后它和全部下级都不再接受命令
    /// </summary>
    public bool NodeEnabledInput
    {
        get => _nodeEnabledInput;
        set
        {
            if (SetProperty(ref _nodeEnabledInput, value))
                OnNodeInputChanged();
        }
    }

    /// <summary>
    /// 节点输入与当前保存的值不同
    /// </summary>
    public bool HasNodeChanges => _selectedItem is { } item
        && (NormalizeName(_nodeNameInput) != item.Node.Name || _nodeEnabledInput != item.Node.IsEnabled);

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
        RaiseCommandsChanged();
    }

    private async Task SaveLayoutAsync()
    {
        if (_service is not { } service || !IsEditable || _state != TopologySaveState.Idle)
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

        if (!plan.HasChanges)
        {
            SaveMessage = "布局与系统名称都没有变化，无需保存";
            return;
        }

        var successMessage = plan.IsFirstCreation
            ? $"已建档：生成 {Format(plan.Candidate.NodeCount)} 个节点，每个节点都已分配 Id"
            : $"已保存：新增 {Format(plan.AddedCount)}、修改 {Format(plan.ChangedCount)}、删除 {Format(plan.RemovedCount)} 个节点";
        await SaveOrAwaitConfirmationAsync(service, new PendingSave(plan, PendingOrigin.Layout, successMessage));
    }

    private async Task SaveNodeAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !IsNodeEditable || !HasNodeChanges
            || _selectedItem is not { } item)
            return;

        var node = item.Node;
        var name = NormalizeName(_nodeNameInput);
        var enabled = _nodeEnabledInput;

        var plan = await PlanAsync(() => service.PlanNodeSettings(node.Id, name, enabled));
        if (plan is null)
            return;

        await SaveOrAwaitConfirmationAsync(service, new PendingSave(plan, PendingOrigin.Node, DescribeNodeSave(node, name, enabled, plan)));
    }

    /// <summary>
    /// 在线程池上计算变更（十万级节点的拓扑重建与比对需要数百毫秒）。失败时已显示原因并返回 null。
    /// </summary>
    private async Task<TopologyChangePlan?> PlanAsync(Func<Result<TopologyChangePlan>> plan)
    {
        SetState(TopologySaveState.Planning);
        IsSaveError = false;
        SaveMessage = "正在计算变更…";

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

        // 什么都还没改：等确认。确认前改动对应的输入都会取消这次保存
        SaveMessage = null;
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

    private void Cancel()
    {
        if (_state != TopologySaveState.AwaitingConfirmation)
            return;

        SetState(TopologySaveState.Idle);
        IsSaveError = false;
        SaveMessage = "已取消，拓扑未保存";
    }

    private async Task CommitAsync(ITopologyService service, PendingSave pending)
    {
        SetState(TopologySaveState.Saving);
        IsSaveError = false;
        SaveMessage = "正在保存…";
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

            ShowCurrent();
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
        SaveMessage = "正在从配置库读取…";
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
            else
                SaveMessage = "已重新读取";
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
    /// 显示当前生效的拓扑并重建树。选中项按节点 Id 保留（节点已不存在时回到系统根），展开状态见 <see cref="_expanded"/>。
    /// 输入框直接写字段：这是程序回填，不算用户改动，不能触发取消确认。
    /// </summary>
    private void ShowCurrent()
    {
        var keep = _selectedItem?.Node.Id;
        if (Current is { } topology)
        {
            IsNonUniform = !TopologyLayout.TryInfer(topology, out var layout);
            _systemNameInput = topology.Name;
            _cabinetsInput = Math.Max(1, layout.Cabinets);
            _layersInput = Math.Max(1, layout.LayersPerCabinet);
            _slotsInput = Math.Max(1, layout.SlotsPerLayer);
            _fixturesInput = Math.Max(1, layout.FixturesPerSlot);
            _productsInput = Math.Max(1, layout.ProductsPerFixture);

            // 第一次显示时展开系统根，直接看到全部柜体
            if (_expanded.Count == 0)
                _expanded.Add(topology.Root.Id);

            // 先换树再选中：树控件在数据源更换时会把选中项清空并回写
            Roots = [new TopologyTreeItemViewModel(topology.Root, _expanded)];
            SelectedItem = FindItem(topology, keep) ?? Roots[0];
        }
        else
        {
            IsNonUniform = false;
            Roots = [];
            SelectedItem = null;
        }

        OnPropertyChanged(nameof(SystemNameInput));
        OnPropertyChanged(nameof(CabinetsInput));
        OnPropertyChanged(nameof(LayersPerCabinetInput));
        OnPropertyChanged(nameof(SlotsPerLayerInput));
        OnPropertyChanged(nameof(FixturesPerSlotInput));
        OnPropertyChanged(nameof(ProductsPerFixtureInput));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsInputEnabled));
        OnPropertyChanged(nameof(LoadErrorText));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SystemIdText));
        RaiseLayoutPreviewChanged();

        // 选中的还是同一个节点时 SelectedItem 不会触发变更，这里统一刷新节点详情与输入
        ShowNodeInputs();
        RaiseSelectionChanged();
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

    private void ShowNodeInputs()
    {
        _nodeNameInput = _selectedItem?.Node.Name ?? string.Empty;
        _nodeEnabledInput = _selectedItem?.Node.IsEnabled ?? true;
        OnPropertyChanged(nameof(NodeNameInput));
        OnPropertyChanged(nameof(NodeEnabledInput));
        OnPropertyChanged(nameof(HasNodeChanges));
        SaveNodeCommand.RaiseCanExecuteChanged();
    }

    private void SetCount(ref decimal? field, decimal? value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
            OnLayoutInputChanged();
    }

    private void OnLayoutInputChanged()
    {
        RaiseLayoutPreviewChanged();
        CancelPending(PendingOrigin.Layout, "布局有改动，请重新保存");
    }

    private void OnNodeInputChanged()
    {
        OnPropertyChanged(nameof(HasNodeChanges));
        SaveNodeCommand.RaiseCanExecuteChanged();
        CancelPending(PendingOrigin.Node, "节点有改动，请重新保存");
    }

    // 等待确认时改了对应的输入：确认的内容已不是现在的输入，取消这次保存。另一处的输入与它无关，不取消
    private void CancelPending(PendingOrigin origin, string message)
    {
        if (_state != TopologySaveState.AwaitingConfirmation || _pending?.Origin != origin)
            return;

        SetState(TopologySaveState.Idle);
        IsSaveError = false;
        SaveMessage = message;
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
            if (value is not { } number)
                return Result<TopologyLayout>.Failure(new Error(TopologyErrors.InvalidLayoutCode, $"请填写{label}"));

            // 先在 decimal 上判断范围与是否为整数，再转 int：手输的超大数直接转换会溢出
            if (number != decimal.Truncate(number) || number < MinCount || number > MaxCount)
            {
                return Result<TopologyLayout>.Failure(new Error(TopologyErrors.InvalidLayoutCode,
                    $"{label}必须是 {MinCount:0}～{MaxCount:0} 之间的整数"));
            }

            values[i] = (int)number;
        }

        var layout = new TopologyLayout(values[0], values[1], values[2], values[3], values[4]);
        var errors = layout.Validate();
        return errors.Count == 0
            ? Result<TopologyLayout>.Success(layout)
            : Result<TopologyLayout>.Failure(errors[0]);
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
        OnPropertyChanged(nameof(HasPageNotice));
        OnPropertyChanged(nameof(IsInputEnabled));
        OnPropertyChanged(nameof(IsNodeInputEnabled));
        OnPropertyChanged(nameof(PendingConfirmationText));
        RaiseCommandsChanged();
    }

    private void RaiseCommandsChanged()
    {
        SaveCommand.RaiseCanExecuteChanged();
        SaveNodeCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }

    private void RaiseLayoutPreviewChanged()
    {
        OnPropertyChanged(nameof(LayoutPreviewText));
        OnPropertyChanged(nameof(IsLayoutPreviewError));
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedLevelText));
        OnPropertyChanged(nameof(SelectedAddressText));
        OnPropertyChanged(nameof(SelectedIdText));
        OnPropertyChanged(nameof(SelectedStatusText));
        OnPropertyChanged(nameof(SelectedChildrenText));
        OnPropertyChanged(nameof(IsNodeEditable));
        OnPropertyChanged(nameof(IsNodeInputEnabled));
        OnPropertyChanged(nameof(NodeEditHint));
        OnPropertyChanged(nameof(HasNodeEditHint));
    }

    private static string? NormalizeName(string input)
    {
        var trimmed = input.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string DescribeNodeSave(TopologyNode node, string? name, bool enabled, TopologyChangePlan plan)
    {
        var parts = new List<string>();
        if (name != node.Name)
            parts.Add(name is null ? "已清除名称" : $"已命名为「{name}」");

        if (enabled != node.IsEnabled)
        {
            parts.Add(enabled
                ? $"已启用，{Format(plan.ReenabledCount)} 个位置恢复可用"
                : $"已停用，{Format(plan.NewlyDisabledCount)} 个位置不再接受命令");
        }

        return $"{node.Level.DisplayName()} {node.Address}：{string.Join("；", parts)}";
    }

    private static string DescribeConfirmation(TopologyChangePlan plan)
    {
        var parts = new List<string>();
        if (plan.RemovedCount > 0)
        {
            parts.Add($"保存后将删除 {Format(plan.RemovedCount)} 个位置（其中产品位 {Format(plan.RemovedProductCount)} 个）：" +
                      $"{ListPositions(plan.RemovedRoots)}。这些位置的 Id 将作废，引用它们的历史数据只能按原 Id 追溯，删除后无法恢复。");
        }

        if (plan.ReenabledCount > 0)
        {
            parts.Add($"保存后将重新启用 {Format(plan.ReenabledCount)} 个位置（其中产品位 {Format(plan.ReenabledProductCount)} 个）：" +
                      $"{ListPositions(plan.ReenabledRoots)}。启用后联锁不再拦截发往这些位置的命令，请确认维护已结束、现场已具备运行条件。");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string ListPositions(IReadOnlyList<TopologyAddress> positions)
    {
        var listed = string.Join("、", positions.Take(TopologyChangePlan.MaxListedPositions));
        return positions.Count > TopologyChangePlan.MaxListedPositions ? $"{listed} 等 {positions.Count} 处" : listed;
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private enum PendingOrigin
    {
        Layout,
        Node,
    }

    private sealed record PendingSave(TopologyChangePlan Plan, PendingOrigin Origin, string SuccessMessage);
}
