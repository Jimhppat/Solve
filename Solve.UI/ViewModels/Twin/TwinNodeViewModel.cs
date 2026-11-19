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
/// <para>只为当前看得见的节点创建：显示区按"焦点 + 显示数量上限"决定画到哪一层，最深一层的容器用摘要模式（只画状态条）。</para>
/// <para>系统用 <see cref="Id"/> 对应状态库中的数据，界面显示位置编码与名称。只在 UI 线程上使用。</para>
/// </remarks>
public abstract class TwinNodeViewModel : ViewModelBase
{
    private TwinStatusSummary _summary = TwinStatusSummary.None;
    private bool _isSelected;

    protected TwinNodeViewModel(TopologyNode node, TwinBoardViewModel board)
    {
        Node = node;
        Board = board;
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
    /// 停用的节点整体变淡。透明度会逐层相乘，所以只在"自身被停用"的节点上变淡，下级随上级一起淡；
    /// 若因上级停用而停用、但上级没有画出来（自己是显示区焦点），才由自己变淡。
    /// </summary>
    public double ContentOpacity => IsDisabled && (!Node.IsEnabled || IsFocusRoot) ? DisabledOpacity : 1.0;

    public const double DisabledOpacity = 0.4;

    /// <summary>
    /// 是否为显示区当前焦点（画面的根）
    /// </summary>
    public bool IsFocusRoot { get; internal set; }

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
    /// 全部计数（悬停提示）
    /// </summary>
    public string SummaryText => _summary.Total == 0 ? "没有产品位" : _summary.DescribeAll();

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    internal TwinBoardViewModel Board { get; }

    /// <summary>
    /// 点选：选中；容器节点同时进入（成为显示区的焦点）
    /// </summary>
    public void Activate() => Board.Activate(this);

    internal void Expand(IReadOnlyList<TwinNodeViewModel> children)
    {
        IsExpanded = true;
        Children = children;
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
        OnPropertyChanged(nameof(SummaryText));
    }

    internal static TwinNodeViewModel Create(TopologyNode node, TwinBoardViewModel board) => node.Level switch
    {
        TopologyLevel.System => new SystemNodeViewModel(node, board),
        TopologyLevel.Cabinet => new CabinetNodeViewModel(node, board),
        TopologyLevel.Layer => new LayerNodeViewModel(node, board),
        TopologyLevel.Slot => new SlotNodeViewModel(node, board),
        TopologyLevel.Fixture => new FixtureNodeViewModel(node, board),
        _ => new ProductNodeViewModel(node, board),
    };
}

/// <summary>
/// 系统：柜体排成一行，等分宽度
/// </summary>
public sealed class SystemNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board);

/// <summary>
/// 柜体：层位上下堆叠、等分高度，层位 1 在最上面
/// </summary>
public sealed class CabinetNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board);

/// <summary>
/// 层位：槽位从左到右排成一行、等分宽度
/// </summary>
public sealed class LayerNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board);

/// <summary>
/// 槽位：治具位上下排列、等分高度
/// </summary>
public sealed class SlotNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board);

/// <summary>
/// 治具位：产品位按网格铺满，每行最多 4 个
/// </summary>
public sealed class FixtureNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board)
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
/// 产品位：叶子，显示自身状态与序号
/// </summary>
public sealed class ProductNodeViewModel(TopologyNode node, TwinBoardViewModel board) : TwinNodeViewModel(node, board)
{
    private ProductState _state = ProductState.Unknown;
    private string? _serialNumber;

    public ProductState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>
    /// 状态名，格子放得下时显示在序号下面
    /// </summary>
    public string StateText => _state.DisplayName();

    public string? SerialNumber
    {
        get => _serialNumber;
        private set => SetProperty(ref _serialNumber, value);
    }

    public string OrdinalText => Node.Ordinal.ToString(CultureInfo.InvariantCulture);

    internal override void Refresh(TwinStateStore store)
    {
        if (!store.TryGetProduct(Id, out var snapshot))
            return;

        State = snapshot.State;
        SerialNumber = snapshot.SerialNumber;
    }
}
