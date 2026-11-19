using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.UI.ViewModels.Twin;

/// <summary>
/// 路径导航上的一级
/// </summary>
/// <param name="IsCurrent">当前焦点：显示为普通文字，不是可点的按钮</param>
public sealed record TwinBreadcrumbItem(string Text, RelayCommand Command, bool IsCurrent)
{
    public bool IsLink => !IsCurrent;
}

/// <summary>
/// 主控台显示区：以一个节点为焦点，按显示数量上限逐层展开，显示六层节点的产品状态。
/// </summary>
/// <remarks>
/// <para>焦点与上限：从焦点往下逐层展开，可见节点超过 <see cref="ElementBudget"/> 时停在上一层，最深一层的容器用摘要模式。
/// 至少展开一层，所以焦点的直接下级总会画出来。</para>
/// <para>刷新：视图以 <see cref="RefreshInterval"/> 周期调用 <see cref="Refresh"/>，只更新状态有变化且当前看得见的节点；
/// 每秒做一次过期检查。只在显示区可见时刷新，主控台不在前台时不占 UI 线程。</para>
/// <para>拓扑被修改时整体重建，焦点与选中项按节点 Id 保留。只在 UI 线程上使用。</para>
/// </remarks>
public sealed class TwinBoardViewModel : ViewModelBase
{
    /// <summary>
    /// 显示区同时画出的节点数上限（默认值，需在现场机型上实测调整）
    /// </summary>
    public const int DefaultElementBudget = 2500;

    /// <summary>
    /// 界面刷新周期：4 Hz 对人眼已经足够及时，再快只会占用 UI 线程
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    // 每 4 次刷新（约 1 s）做一次过期检查：它要遍历全部产品位，没必要每次都做
    private const int ExpireEveryRefreshes = 4;

    private readonly Dictionary<TopologyNodeId, TwinNodeViewModel> _visible = [];
    private TwinStateStore? _store;
    private EquipmentTopology? _bound;
    private TopologyNodeId? _focusId;
    private TopologyNodeId? _selectedId;
    private TwinNodeViewModel? _focus;
    private IReadOnlyList<TwinBreadcrumbItem> _breadcrumb = [];
    private string? _refreshError;
    private int _refreshCount;

    public TwinBoardViewModel() : this(DefaultElementBudget)
    {
    }

    public TwinBoardViewModel(int elementBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elementBudget, 1);
        ElementBudget = elementBudget;
        FocusParentCommand = new RelayCommand(FocusParent, () => _focus is { Level: not TopologyLevel.System });
    }

    public int ElementBudget { get; }

    /// <summary>
    /// 返回上一级
    /// </summary>
    public RelayCommand FocusParentCommand { get; }

    public bool IsAttached => _store is not null;

    public bool HasTopology => _bound is not null;

    public bool ShowEmpty => !HasTopology;

    public string EmptyText => _store is null
        ? "配置库不可用或尚未就绪，暂无设备状态"
        : "尚未建档：请先在「设备拓扑」页保存布局";

    /// <summary>
    /// 当前焦点节点（显示区的根）
    /// </summary>
    public TwinNodeViewModel? Focus
    {
        get => _focus;
        private set => SetProperty(ref _focus, value);
    }

    public IReadOnlyList<TwinBreadcrumbItem> Breadcrumb
    {
        get => _breadcrumb;
        private set => SetProperty(ref _breadcrumb, value);
    }

    /// <summary>
    /// 刷新出错的原因。显示区出错时明确告诉操作员"画面已不可信"，而不是静默停在旧画面
    /// </summary>
    public string? RefreshError
    {
        get => _refreshError;
        private set
        {
            if (SetProperty(ref _refreshError, value))
                OnPropertyChanged(nameof(HasRefreshError));
        }
    }

    public bool HasRefreshError => _refreshError is not null;

    /// <summary>
    /// 当前画出的节点数（含焦点）
    /// </summary>
    public int VisibleCount => _visible.Count;

    // ==================== 选中节点详情（主控台控制区） ====================

    public bool HasSelection => SelectedNode is not null;

    public bool IsProductSelected => SelectedNode is { Level: TopologyLevel.Product };

    public string? SelectedTitle => SelectedNode is { } node
        ? node.Level == TopologyLevel.System ? $"系统 {node.DisplayName}" : $"{node.Level.DisplayName()} {node.Address}"
        : null;

    public string? SelectedNameText => SelectedNode is { } node ? node.Name ?? "（未命名）" : null;

    public string? SelectedIdText => SelectedNode?.Id.ToString();

    /// <summary>
    /// 产品位：自身状态；容器：最严重的状态
    /// </summary>
    public string? SelectedStateText
    {
        get
        {
            if (SelectedNode is not { } node || _store is not { } store)
                return null;

            if (!node.IsEffectivelyEnabled)
                return "停用";

            if (node.Level == TopologyLevel.Product)
                return store.TryGetProduct(node.Id, out var product) ? product.State.DisplayName() : ProductState.Unknown.DisplayName();

            return store.GetSummary(node.Id).Worst?.DisplayName() ?? "停用";
        }
    }

    public string? SelectedSerialText => IsProductSelected && _store is { } store && store.TryGetProduct(SelectedNode!.Id, out var product)
        ? product.SerialNumber ?? "—"
        : null;

    public string? SelectedUpdatedText => IsProductSelected && _store is { } store && store.TryGetProduct(SelectedNode!.Id, out var product)
        ? product.UpdatedAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "从未收到"
        : null;

    /// <summary>
    /// 其下全部产品位的统计
    /// </summary>
    public string? SelectedSummaryText => SelectedNode is { } node && _store is { } store
        ? store.GetSummary(node.Id) is { Total: > 0 } summary ? summary.DescribeAll() : "没有产品位"
        : null;

    private TopologyNode? SelectedNode
        => _selectedId is { } id && _bound is { } topology && topology.TryGetNode(id, out var node) ? node : null;

    // ==================== 接入与刷新 ====================

    /// <summary>
    /// 接入孪生状态库。须在 UI 线程上调用，只能调用一次。
    /// </summary>
    public void Attach(TwinStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (_store is not null)
            throw new InvalidOperationException("显示区已接入孪生状态库");

        _store = store;
        OnPropertyChanged(nameof(IsAttached));
        OnPropertyChanged(nameof(EmptyText));
        Refresh();
    }

    /// <summary>
    /// 周期刷新（UI 线程）：必要时做过期检查；拓扑变了就重建，否则只更新有变化的可见节点。
    /// </summary>
    public void Refresh()
    {
        if (_store is not { } store)
            return;

        try
        {
            if (_refreshCount++ % ExpireEveryRefreshes == 0)
                store.ExpireStale();

            var topology = store.Topology;
            if (!ReferenceEquals(topology, _bound))
            {
                Bind(topology);
            }
            else
            {
                var selectionChanged = false;
                foreach (var id in store.DrainChanges())
                {
                    if (_visible.TryGetValue(id, out var node))
                        node.Refresh(store);
                    selectionChanged |= id == _selectedId;
                }

                if (selectionChanged)
                    RaiseSelectionChanged();
            }

            RefreshError = null;
        }
        catch (Exception ex)
        {
            // 定时器在 UI 线程上：异常逃逸会触发全站安全跳闸。显示区故障不值得停机，但必须明确告诉操作员画面已不可信
            RefreshError = $"状态显示已停止更新，画面可能已过时：{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>
    /// 把某个节点设为焦点；产品位不能作焦点，改为它所在的治具位
    /// </summary>
    public void FocusOn(TopologyNodeId id)
    {
        if (_bound is not { } topology || !topology.TryGetNode(id, out var node))
            return;

        if (node.Level == TopologyLevel.Product && node.Address.TryGetParent(out var parentAddress)
            && topology.TryGetNode(parentAddress, out var parent))
        {
            node = parent;
        }

        _focusId = node.Id;
        BuildVisible();
    }

    internal void Activate(TwinNodeViewModel node)
    {
        Select(node.Id);
        if (node is not ProductNodeViewModel && node.Id != _focusId)
            FocusOn(node.Id);
    }

    public void Select(TopologyNodeId id)
    {
        if (_selectedId is { } previous && _visible.TryGetValue(previous, out var oldItem))
            oldItem.IsSelected = false;

        _selectedId = id;
        if (_visible.TryGetValue(id, out var newItem))
            newItem.IsSelected = true;

        RaiseSelectionChanged();
    }

    // ==================== 内部 ====================

    private void FocusParent()
    {
        if (_focus?.Node.Address.TryGetParent(out var parentAddress) == true
            && _bound is { } topology && topology.TryGetNode(parentAddress, out var parent))
        {
            FocusOn(parent.Id);
        }
    }

    private void Bind(EquipmentTopology? topology)
    {
        _bound = topology;
        _store?.DrainChanges(); // 重建会读取全部最新值，之前的变化标记作废

        if (topology is null)
        {
            _visible.Clear();
            Focus = null;
            Breadcrumb = [];
        }
        else
        {
            // 焦点按 Id 保留；节点已被删除时回到系统根
            if (_focusId is not { } focusId || !topology.TryGetNode(focusId, out _))
                _focusId = topology.Root.Id;

            BuildVisible();
        }

        OnPropertyChanged(nameof(HasTopology));
        OnPropertyChanged(nameof(ShowEmpty));
        RaiseSelectionChanged();
    }

    private void BuildVisible()
    {
        if (_bound is not { } topology || _store is not { } store || _focusId is not { } focusId
            || !topology.TryGetNode(focusId, out var focus))
            return;

        _visible.Clear();
        var deepest = DeepestLevel(topology, focus);
        var root = Build(focus, deepest, store);
        root.IsFocusRoot = true;
        Focus = root;
        Breadcrumb = BuildBreadcrumb(topology, focus);
        OnPropertyChanged(nameof(VisibleCount));
        FocusParentCommand.RaiseCanExecuteChanged();

        if (_selectedId is { } selected && _visible.TryGetValue(selected, out var item))
            item.IsSelected = true;
    }

    /// <summary>
    /// 从焦点往下逐层累加节点数，超过上限就停在上一层（至少展开一层）
    /// </summary>
    private TopologyLevel DeepestLevel(EquipmentTopology topology, TopologyNode focus)
    {
        var deepest = focus.Level;
        var total = 1;
        for (var level = focus.Level + 1; level <= TopologyLevel.Product; level++)
        {
            var count = topology.GetDescendants(focus.Address, level).Count;
            if (count == 0 || (deepest != focus.Level && total + count > ElementBudget))
                break;

            total += count;
            deepest = level;
        }

        return deepest;
    }

    private TwinNodeViewModel Build(TopologyNode node, TopologyLevel deepest, TwinStateStore store)
    {
        var item = TwinNodeViewModel.Create(node, this);
        _visible[node.Id] = item;

        if (node.Level < deepest && node.Children.Count > 0)
            item.Expand(node.Children.Select(child => Build(child, deepest, store)).ToArray());

        item.Refresh(store);
        return item;
    }

    private IReadOnlyList<TwinBreadcrumbItem> BuildBreadcrumb(EquipmentTopology topology, TopologyNode focus)
    {
        var items = new List<TwinBreadcrumbItem>();
        for (var level = TopologyLevel.System; level <= focus.Level; level++)
        {
            if (!topology.TryGetNode(focus.Address.AncestorAt(level), out var node))
                continue;

            var text = level == TopologyLevel.System
                ? $"系统 {node.DisplayName}"
                : node.Name is null ? node.Address.ToString() : $"{node.Address} {node.Name}";
            var id = node.Id;
            items.Add(new TwinBreadcrumbItem(text, new RelayCommand(() => FocusOn(id), () => id != _focusId), IsCurrent: level == focus.Level));
        }

        return items;
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsProductSelected));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedNameText));
        OnPropertyChanged(nameof(SelectedIdText));
        OnPropertyChanged(nameof(SelectedStateText));
        OnPropertyChanged(nameof(SelectedSerialText));
        OnPropertyChanged(nameof(SelectedUpdatedText));
        OnPropertyChanged(nameof(SelectedSummaryText));
    }
}
