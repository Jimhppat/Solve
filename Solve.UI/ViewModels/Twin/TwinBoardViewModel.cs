using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.UI.ViewModels.Twin;

/// <summary>
/// 主控台显示区：纯展示面板，从系统根开始，一次只画一个柜体（用切换胶囊选择），按显示数量上限逐层展开它的产品状态。
/// </summary>
/// <remarks>
/// <para>柜体切换：系统下的全部柜体都有视图模型，作为切换胶囊显示各自最严重的状态与告警数，看不见的柜体出了故障也能发现；
/// 只有当前柜体展开到下级。切换时收起旧柜体、放掉它下级的视图模型，再展开新柜体。默认显示第一个柜体。</para>
/// <para>只有产品位可以被点选：选中后在控制区显示产品信息，画面不切换、不进入下一级；
/// 柜体、层位等容器只做展示。切到别的柜体时选中保留，控制区继续显示那个产品位，切回来时仍高亮。</para>
/// <para>显示数量上限：只计算当前柜体（加上系统与全部柜体胶囊），往下逐层展开，超过 <see cref="ElementBudget"/> 时停在上一层，
/// 最深一层的容器用摘要模式（只画状态条）。当前柜体至少展开到层位。</para>
/// <para>刷新：视图以 <see cref="RefreshInterval"/> 周期调用 <see cref="Refresh"/>，只更新状态有变化且当前看得见的节点；
/// 每秒做一次过期检查。只在显示区可见时刷新，主控台不在前台时不占 UI 线程。</para>
/// <para>拓扑被修改时整体重建，选中的产品位与当前柜体都按节点 Id 保留；产品位已被删除时清除选中，
/// 当前柜体已被删除时回到第一个柜体。只在 UI 线程上使用。</para>
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
    private TopologyNodeId? _selectedId;
    private TopologyNodeId? _cabinetId;
    private TwinNodeViewModel? _root;
    private string? _refreshError;
    private int _refreshCount;

    public TwinBoardViewModel() : this(DefaultElementBudget)
    {
    }

    public TwinBoardViewModel(int elementBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elementBudget, 1);
        ElementBudget = elementBudget;
    }

    public int ElementBudget { get; }

    public bool IsAttached => _store is not null;

    public bool HasTopology => _bound is not null;

    public bool ShowEmpty => !HasTopology;

    public string EmptyText => _store is null
        ? "配置库不可用或尚未就绪，暂无设备状态"
        : "尚未建档：请先在「设备拓扑」页保存布局";

    /// <summary>
    /// 系统根节点（显示区画面的根）；尚未建档时为 null
    /// </summary>
    public TwinNodeViewModel? Root
    {
        get => _root;
        private set => SetProperty(ref _root, value);
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
    /// 当前画出的节点数（含系统根与全部柜体胶囊）
    /// </summary>
    public int VisibleCount => _visible.Count;

    /// <summary>
    /// 当前显示的柜体；尚未建档或系统下没有柜体时为 null
    /// </summary>
    public TwinNodeViewModel? CurrentCabinet => (Root as SystemNodeViewModel)?.CurrentCabinet;

    // ==================== 选中产品位的信息（主控台控制区） ====================
    // 只有产品位能被选中；柜体、层位等容器的情况在显示区上已经看得到（边框颜色、圆点、告警红字），不单独显示

    public bool HasSelection => SelectedProduct is not null;

    /// <summary>
    /// 产品位编码（人用），如 C01-L01-S01-F01-P01
    /// </summary>
    public string? SelectedAddressText => SelectedProduct?.Address.ToString();

    /// <summary>
    /// 产品位配置的名称；未命名为 null，界面不显示这一行
    /// </summary>
    public string? SelectedNameText => SelectedProduct?.Name;

    public bool HasSelectedName => SelectedNameText is not null;

    /// <summary>
    /// 节点 Id（系统用）
    /// </summary>
    public string? SelectedIdText => SelectedProduct?.Id.ToString();

    public string? SelectedStateText
    {
        get
        {
            if (SelectedProduct is not { } node || _store is not { } store)
                return null;

            if (!node.IsEffectivelyEnabled)
                return "停用";

            return store.TryGetProduct(node.Id, out var product) ? product.State.DisplayName() : ProductState.Unknown.DisplayName();
        }
    }

    public string? SelectedSerialText => SelectedProduct is { } node && _store is { } store && store.TryGetProduct(node.Id, out var product)
        ? product.SerialNumber ?? "—"
        : null;

    public string? SelectedUpdatedText => SelectedProduct is { } node && _store is { } store && store.TryGetProduct(node.Id, out var product)
        ? product.UpdatedAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "从未收到"
        : null;

    private TopologyNode? SelectedProduct
        => _selectedId is { } id && _bound is { } topology && topology.TryGetNode(id, out var node) && node.Level == TopologyLevel.Product
            ? node
            : null;

    // ==================== 接入、刷新与选中 ====================

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
    /// 选中一个产品位，在控制区显示产品信息。画面不变：显示区是展示面板，不进入下一级。
    /// 只接受产品位：传入容器或不存在的 Id 时忽略，当前选中保持不变。
    /// </summary>
    public void Select(TopologyNodeId productId)
    {
        if (_bound is not { } topology || !topology.TryGetNode(productId, out var node) || node.Level != TopologyLevel.Product)
            return;

        if (_selectedId is { } previous && _visible.TryGetValue(previous, out var oldItem) && oldItem is ProductNodeViewModel oldProduct)
            oldProduct.IsSelected = false;

        _selectedId = productId;
        if (_visible.TryGetValue(productId, out var newItem) && newItem is ProductNodeViewModel newProduct)
            newProduct.IsSelected = true;

        RaiseSelectionChanged();
    }

    /// <summary>
    /// 切换显示的柜体（切换胶囊）。收起当前柜体、展开指定柜体；选中的产品位不变。
    /// 只接受本系统的柜体：传入其他层级或不存在的 Id、或已经是当前柜体时忽略。
    /// </summary>
    public void ShowCabinet(TopologyNodeId cabinetId)
    {
        if (Root is not SystemNodeViewModel system || _store is not { } store || system.CurrentCabinet?.Id == cabinetId)
            return;

        var target = system.Children.FirstOrDefault(cabinet => cabinet.Id == cabinetId);
        if (target is null)
            return;

        try
        {
            if (system.CurrentCabinet is { } previous)
            {
                Release(previous);
                previous.Collapse();
            }

            ExpandCabinet(target, store);
            _cabinetId = cabinetId;
            system.SetCurrentCabinet(target);
            OnPropertyChanged(nameof(CurrentCabinet));
            OnPropertyChanged(nameof(VisibleCount));
            RefreshError = null;
        }
        catch (Exception ex)
        {
            // 由切换胶囊的绑定在 UI 线程上调用：与周期刷新一样，不让异常逃逸，但必须明确告诉操作员画面已不可信
            RefreshError = $"切换柜体失败，画面可能不完整：{ex.GetType().Name}：{ex.Message}";
        }
    }

    // ==================== 内部 ====================

    private void Bind(EquipmentTopology? topology)
    {
        _bound = topology;
        _store?.DrainChanges(); // 重建会读取全部最新值，之前的变化标记作废
        _visible.Clear();

        // 选中项按 Id 保留；节点已被删除时清除，详情不再显示一个不存在的位置
        if (_selectedId is { } selected && (topology is null || !topology.TryGetNode(selected, out _)))
            _selectedId = null;

        Root = topology is null || _store is not { } store ? null : BuildSystem(topology.Root, store);

        OnPropertyChanged(nameof(CurrentCabinet));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasTopology));
        OnPropertyChanged(nameof(ShowEmpty));
        RaiseSelectionChanged();
    }

    /// <summary>
    /// 系统与全部柜体（胶囊，摘要模式），再展开当前柜体：按 Id 保留上次显示的柜体，它已被删除时回到第一个
    /// </summary>
    private SystemNodeViewModel BuildSystem(TopologyNode root, TwinStateStore store)
    {
        var system = (SystemNodeViewModel)Register(root, store);
        var cabinets = root.Children.Select(cabinet => Register(cabinet, store)).ToArray();
        system.Expand(cabinets);

        var current = cabinets.FirstOrDefault(cabinet => cabinet.Id == _cabinetId) ?? cabinets.FirstOrDefault();
        _cabinetId = current?.Id;
        if (current is not null)
            ExpandCabinet(current, store);

        system.SetCurrentCabinet(current);
        return system;
    }

    private void ExpandCabinet(TwinNodeViewModel cabinet, TwinStateStore store)
    {
        if (cabinet.Node.Children.Count == 0)
            return;

        var deepest = DeepestLevel(cabinet.Node);
        cabinet.Expand(cabinet.Node.Children.Select(child => Build(child, deepest, store)).ToArray());

        if (_selectedId is { } id && _visible.TryGetValue(id, out var item) && item is ProductNodeViewModel product)
            product.IsSelected = true;
    }

    /// <summary>
    /// 只算当前柜体：系统与全部柜体胶囊已经画出，从层位往下逐层累加节点数，超过上限就停在上一层（至少展开到层位）
    /// </summary>
    private TopologyLevel DeepestLevel(TopologyNode cabinet)
    {
        Span<int> counts = stackalloc int[(int)TopologyLevel.Product + 1];
        CountDescendants(cabinet, counts);

        var deepest = TopologyLevel.Cabinet;
        var total = _visible.Count;
        for (var level = TopologyLevel.Layer; level <= TopologyLevel.Product; level++)
        {
            var count = counts[(int)level];
            if (count == 0 || (deepest != TopologyLevel.Cabinet && total + count > ElementBudget))
                break;

            total += count;
            deepest = level;
        }

        return deepest;
    }

    private static void CountDescendants(TopologyNode node, Span<int> counts)
    {
        foreach (var child in node.Children)
        {
            counts[(int)child.Level]++;
            CountDescendants(child, counts);
        }
    }

    private TwinNodeViewModel Build(TopologyNode node, TopologyLevel deepest, TwinStateStore store)
    {
        var item = Register(node, store);
        if (node.Level < deepest && node.Children.Count > 0)
            item.Expand(node.Children.Select(child => Build(child, deepest, store)).ToArray());

        return item;
    }

    private TwinNodeViewModel Register(TopologyNode node, TwinStateStore store)
    {
        var item = TwinNodeViewModel.Create(node, this);
        _visible[node.Id] = item;
        item.Refresh(store);
        return item;
    }

    /// <summary>
    /// 切走的柜体：它下级的视图模型不再刷新（柜体自身仍是胶囊，保留）
    /// </summary>
    private void Release(TwinNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            _visible.Remove(child.Id);
            Release(child);
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedAddressText));
        OnPropertyChanged(nameof(SelectedNameText));
        OnPropertyChanged(nameof(HasSelectedName));
        OnPropertyChanged(nameof(SelectedIdText));
        OnPropertyChanged(nameof(SelectedStateText));
        OnPropertyChanged(nameof(SelectedSerialText));
        OnPropertyChanged(nameof(SelectedUpdatedText));
    }
}
