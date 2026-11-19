using System;
using System.Collections.Generic;
using System.Globalization;
using Solve.Core.Application.Twin;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Twin;

namespace Solve.UI.ViewModels.Twin;

/// <summary>
/// 主控台上一个六层节点的视图模型基类。容器节点显示其下产品位的汇总，产品位显示自身状态。
/// </summary>
/// <remarks>
/// <para>只为当前画出的节点创建：系统与全部柜体（切换胶囊）总会创建，当前柜体按显示数量上限决定画到哪一层，
/// 最深一层的容器用摘要模式（只画状态条）。</para>
/// <para>容器（系统、柜体、层位、槽位、治具位）只做展示，不能被选中；只有 <see cref="ProductNodeViewModel"/> 可选中。</para>
/// <para>系统用 <see cref="Id"/> 对应状态库中的数据，界面显示位置编码与名称。只在 UI 线程上使用。</para>
/// </remarks>
public abstract class TwinNodeViewModel : ViewModelBase
{
    private TwinStatusSummary _summary = TwinStatusSummary.None;

    protected TwinNodeViewModel(TopologyNode node)
    {
        Node = node;
    }

    public TopologyNode Node { get; }

    public TopologyNodeId Id => Node.Id;

    public TopologyLevel Level => Node.Level;

    /// <summary>
    /// 在上级容器里显示的短编码，如 C01、L02、S03；系统根为系统名称
    /// </summary>
    public string ShortCode
    {
        get
        {
            if (Level == TopologyLevel.System)
                return Node.DisplayName;

            var code = Node.Address.ToString();
            return code[(code.LastIndexOf('-') + 1)..];
        }
    }

    /// <summary>
    /// 层级 + 完整位置编码，如"槽位 C01-L02-S03"；系统根为"系统 名称"
    /// </summary>
    public string Title => Level == TopologyLevel.System
        ? $"系统 {Node.DisplayName}"
        : $"{Level.DisplayName()} {Node.Address}";

    /// <summary>
    /// 配置的显示名（系统根的名称已在 <see cref="Title"/> 中）
    /// </summary>
    public string? Name => Level == TopologyLevel.System ? null : Node.Name;

    public bool HasName => Name is not null;

    /// <summary>
    /// 自身或任一上级被停用（维护屏蔽）
    /// </summary>
    public bool IsDisabled => !Node.IsEffectivelyEnabled;

    /// <summary>
    /// 停用的节点整体变淡。透明度会逐层相乘，所以只在"自身被停用"的节点上变淡，下级随上级一起淡
    /// （显示区总是从系统根画起、当前柜体画在系统里面，停用节点的上级都在画面上）。
    /// </summary>
    public double ContentOpacity => Node.IsEnabled ? 1.0 : DisabledOpacity;

    public const double DisabledOpacity = 0.4;

    /// <summary>
    /// 展开模式：画出下级；否则为摘要模式，只画状态条
    /// </summary>
    public bool IsExpanded { get; private set; }

    public bool IsSummary => !IsExpanded;

    public IReadOnlyList<TwinNodeViewModel> Children { get; private set; } = [];

    /// <summary>
    /// 画出的下级个数，决定等分网格的行数或列数
    /// </summary>
    public int ChildCount => Children.Count;

    /// <summary>
    /// 其下产品位的状态统计（产品位为只含自身的统计）
    /// </summary>
    public TwinStatusSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>
    /// 最严重的状态，决定容器边框颜色；没有启用的产品位时为 null（按停用色显示）
    /// </summary>
    public ProductState? Worst => _summary.Worst;

    /// <summary>
    /// 告警类计数（红字），如"故障 1 · 报警 2"
    /// </summary>
    public string AlarmText => _summary.DescribeAlarms();

    public bool HasAlarm => AlarmText.Length > 0;

    /// <summary>
    /// 告警类产品位数（柜体切换胶囊上的红色计数）
    /// </summary>
    public int AlarmCount => _summary.AlarmCount;

    /// <summary>
    /// 全部计数（悬停提示）
    /// </summary>
    public string SummaryText => _summary.Total == 0 ? "没有产品位" : _summary.DescribeAll();

    /// <summary>
    /// 画出下级。柜体在切换时会反复展开、收起，所以要发通知
    /// </summary>
    internal void Expand(IReadOnlyList<TwinNodeViewModel> children)
    {
        IsExpanded = true;
        Children = children;
        RaiseExpansionChanged();
    }

    /// <summary>
    /// 收起为摘要模式，放掉下级的视图模型（切走的柜体不再占用显示数量与内存）
    /// </summary>
    internal void Collapse()
    {
        if (!IsExpanded)
            return;

        IsExpanded = false;
        Children = [];
        RaiseExpansionChanged();
    }

    private void RaiseExpansionChanged()
    {
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsSummary));
        OnPropertyChanged(nameof(Children));
        OnPropertyChanged(nameof(ChildCount));
    }

    /// <summary>
    /// 从状态库读取最新统计；没有变化时不发通知，避免无谓的界面重绘
    /// </summary>
    internal virtual void Refresh(TwinStateStore store)
    {
        var summary = store.GetSummary(Id);
        if (summary.SameAs(_summary))
            return;

        Summary = summary;
        OnPropertyChanged(nameof(Worst));
        OnPropertyChanged(nameof(AlarmText));
        OnPropertyChanged(nameof(HasAlarm));
        OnPropertyChanged(nameof(AlarmCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    internal static TwinNodeViewModel Create(TopologyNode node, TwinBoardViewModel board) => node.Level switch
    {
        TopologyLevel.System => new SystemNodeViewModel(node, board),
        TopologyLevel.Cabinet => new CabinetNodeViewModel(node),
        TopologyLevel.Layer => new LayerNodeViewModel(node),
        TopologyLevel.Slot => new SlotNodeViewModel(node),
        TopologyLevel.Fixture => new FixtureNodeViewModel(node),
        _ => new ProductNodeViewModel(node, board),
    };
}

/// <summary>
/// 系统：一次只画一个柜体（<see cref="CurrentCabinet"/>），用标题行的柜体切换胶囊选择。
/// <see cref="TwinNodeViewModel.Children"/> 是全部柜体（胶囊），只有当前柜体是展开的。
/// </summary>
public sealed class SystemNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node)
{
    private TwinNodeViewModel? _currentCabinet;

    /// <summary>
    /// 显示区上画出的柜体；也是切换胶囊的选中项（双向绑定）。
    /// 设置时交给显示区切换：收起旧柜体、展开新柜体；传入 null 或不是本系统的柜体时忽略
    /// （切换胶囊在重建过程中可能短暂回传 null，不能因此把画面清空）。
    /// </summary>
    public TwinNodeViewModel? CurrentCabinet
    {
        get => _currentCabinet;
        set
        {
            if (value is not null)
                board.ShowCabinet(value.Id);
        }
    }

    /// <summary>
    /// 只有一个柜体时不显示切换胶囊：只有一个选项的切换没有意义，柜体卡片自己的标题已写明柜号
    /// </summary>
    public bool HasCabinetChoice => ChildCount > 1;

    internal void SetCurrentCabinet(TwinNodeViewModel? cabinet)
        => SetProperty(ref _currentCabinet, cabinet, nameof(CurrentCabinet));
}

/// <summary>
/// 柜体：层位上下堆叠、等分高度，层位 1 在最上面。没有被选中显示时只用作切换胶囊（摘要模式）
/// </summary>
public sealed class CabinetNodeViewModel(TopologyNode node) : TwinNodeViewModel(node);

/// <summary>
/// 层位：槽位从左到右排成一行、等分宽度
/// </summary>
public sealed class LayerNodeViewModel(TopologyNode node) : TwinNodeViewModel(node);

/// <summary>
/// 槽位：治具位上下排列、等分高度
/// </summary>
public sealed class SlotNodeViewModel(TopologyNode node) : TwinNodeViewModel(node);

/// <summary>
/// 治具位：产品位按网格铺满，每行最多 4 个
/// </summary>
public sealed class FixtureNodeViewModel(TopologyNode node) : TwinNodeViewModel(node)
{
    /// <summary>
    /// 每行最多的产品位数（默认值，按现场治具调整）
    /// </summary>
    public const int MaxProductColumns = 4;

    /// <summary>
    /// 每行产品位数：不足 4 个时按实际个数，格子铺满整行而不是留空
    /// </summary>
    public int ProductColumns => Math.Clamp(ChildCount, 1, MaxProductColumns);
}

/// <summary>
/// 产品位：叶子，格子只显示序号，状态用格子颜色表示（状态名在点选后弹出的产品信息卡片里查看）；显示区中唯一可选中的节点
/// </summary>
public sealed class ProductNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node)
{
    private ProductState _state = ProductState.Idle;
    private string? _serialNumber;
    private bool _isSelected;

    public ProductState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string? SerialNumber
    {
        get => _serialNumber;
        private set => SetProperty(ref _serialNumber, value);
    }

    public string OrdinalText => Node.Ordinal.ToString(CultureInfo.InvariantCulture);

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// 点选：选中这个产品位，产品信息卡片显示它的信息
    /// </summary>
    public void Select() => board.Select(Id);

    internal override void Refresh(TwinStateStore store)
    {
        if (!store.TryGetProduct(Id, out var snapshot))
            return;

        State = snapshot.State;
        SerialNumber = snapshot.SerialNumber;
    }
}
