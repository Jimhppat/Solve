using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.UI.ViewModels.Twin;

/// <summary>
/// 主控台显示区：纯展示面板，始终显示整套设备（从系统根开始），按显示数量上限逐层展开六层节点的产品状态。
/// </summary>
/// <remarks>
/// <para>只有产品位可以被点选：选中后在控制区显示产品信息，画面不切换、不进入下一级；
/// 柜体、层位等容器只做展示。</para>
/// <para>显示数量上限：从系统根往下逐层展开，可见节点超过 <see cref="ElementBudget"/> 时停在上一层，
/// 最深一层的容器用摘要模式（只画状态条）。至少展开一层，所以柜体总会画出来。</para>
/// <para>刷新：视图以 <see cref="RefreshInterval"/> 周期调用 <see cref="Refresh"/>，只更新状态有变化且当前看得见的节点；
/// 每秒做一次过期检查。只在显示区可见时刷新，主控台不在前台时不占 UI 线程。</para>
/// <para>拓扑被修改时整体重建，选中项按节点 Id 保留；节点已被删除时清除选中。只在 UI 线程上使用。</para>
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
    /// 当前画出的节点数（含系统根）
    /// </summary>
    public int VisibleCount => _visible.Count;

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

    // ==================== 内部 ====================

    private void Bind(EquipmentTopology? topology)
    {
        _bound = topology;
        _store?.DrainChanges(); // 重建会读取全部最新值，之前的变化标记作废
        _visible.Clear();

        // 选中项按 Id 保留；节点已被删除时清除，详情不再显示一个不存在的位置
        if (_selectedId is { } selected && (topology is null || !topology.TryGetNode(selected, out _)))
            _selectedId = null;

        Root = topology is null || _store is not { } store ? null : Build(topology.Root, DeepestLevel(topology), store);

        if (_selectedId is { } id && _visible.TryGetValue(id, out var item) && item is ProductNodeViewModel product)
            product.IsSelected = true;

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasTopology));
        OnPropertyChanged(nameof(ShowEmpty));
        RaiseSelectionChanged();
    }

    /// <summary>
    /// 从系统根往下逐层累加节点数，超过上限就停在上一层（至少展开到柜体）
    /// </summary>
    private TopologyLevel DeepestLevel(EquipmentTopology topology)
    {
        var deepest = TopologyLevel.System;
        var total = 1;
        for (var level = TopologyLevel.Cabinet; level <= TopologyLevel.Product; level++)
        {
            var count = topology.CountAt(level);
            if (count == 0 || (deepest != TopologyLevel.System && total + count > ElementBudget))
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
