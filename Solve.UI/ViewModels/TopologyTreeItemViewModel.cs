using System.Collections.Generic;
using System.Linq;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 拓扑树上的一个节点。节点内容不可变；展开状态按节点 Id 记在页面共享的集合里。
/// </summary>
public sealed class TopologyTreeItemViewModel : ViewModelBase
{
    private readonly ISet<TopologyNodeId> _expanded;
    private IReadOnlyList<TopologyTreeItemViewModel>? _children;

    /// <param name="node">拓扑节点</param>
    /// <param name="expanded">页面持有的已展开节点 Id 集合，整棵树共享</param>
    public TopologyTreeItemViewModel(TopologyNode node, ISet<TopologyNodeId> expanded)
    {
        Node = node;
        _expanded = expanded;
    }

    public TopologyNode Node { get; }

    /// <summary>
    /// 树上的主文字：层级 + 位置编码；系统根显示系统名称
    /// </summary>
    public string Header => Node.Level == TopologyLevel.System
        ? $"系统 {Node.DisplayName}"
        : $"{Node.Level.DisplayName()} {Node.Address}";

    /// <summary>
    /// 配置的显示名（系统根的名称已在 <see cref="Header"/> 中）
    /// </summary>
    public string? Name => Node.Level == TopologyLevel.System ? null : Node.Name;

    public bool HasName => Name is not null;

    /// <summary>
    /// 自身或任一上级被停用
    /// </summary>
    public bool IsDisabled => !Node.IsEffectivelyEnabled;

    /// <summary>
    /// 展开状态按 Id 记录：保存后整棵树重建，同一节点（哪怕位置编码变了）保持原来的展开状态
    /// </summary>
    public bool IsExpanded
    {
        get => _expanded.Contains(Node.Id);
        set
        {
            if (value ? _expanded.Add(Node.Id) : _expanded.Remove(Node.Id))
                OnPropertyChanged();
        }
    }

    /// <summary>
    /// 按需创建：展开到哪一层才创建哪一层的视图模型，十万级节点不会一次性全部生成
    /// </summary>
    public IReadOnlyList<TopologyTreeItemViewModel> Children
        => _children ??= Node.Children.Select(child => new TopologyTreeItemViewModel(child, _expanded)).ToArray();
}
