using System.Threading.Tasks;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 拓扑页右侧的节点表单：选中节点即修改它。名称、序号、启用、下级数量一起预览、一次保存；复制与删除作用于选中节点。
/// </summary>
/// <remarks>
/// 下级数量调大时照抄最后一个下级的结构，调小时删掉序号最大的几个；要删中间的某一个，选中它后删除。
/// 表单与页面共用保存流程：需要确认时确认框出现在保存按钮旁，改动输入或换选节点即取消。
/// </remarks>
public sealed partial class TopologyViewModel
{
    private string _nodeNameInput = string.Empty;
    private bool _nodeEnabledInput = true;
    private decimal? _ordinalInput;
    private decimal? _childCountInput;

    /// <summary>
    /// 保存选中节点的全部修改
    /// </summary>
    public AsyncRelayCommand SaveNodeCommand { get; }

    /// <summary>
    /// 放弃表单上未保存的修改
    /// </summary>
    public RelayCommand RevertNodeCommand { get; }

    /// <summary>
    /// 把选中节点连同下级复制为一个同级节点
    /// </summary>
    public AsyncRelayCommand CopyCommand { get; }

    /// <summary>
    /// 删除选中节点及其下级
    /// </summary>
    public AsyncRelayCommand RemoveCommand { get; }

    // ==================== 标题 ====================

    /// <summary>
    /// 表单标题：层级与位置编码，如"柜体 C01"；系统根为"系统"
    /// </summary>
    public string? SelectedTitle => _selectedItem?.Node is not { } node
        ? null
        : node.Level == TopologyLevel.System ? "系统" : $"{node.Level.DisplayName()} {node.Address}";

    /// <summary>
    /// 节点 Id（系统用），可选中复制
    /// </summary>
    public string? SelectedIdText => _selectedItem?.Node.Id.ToString();

    /// <summary>
    /// 停用标记：自身停用为"停用"，被上级屏蔽为"上级停用"；可用时为 null
    /// </summary>
    public string? SelectedDisabledText => _selectedItem?.Node is { IsEffectivelyEnabled: false } node
        ? node.IsEnabled ? "上级停用" : "停用"
        : null;

    public bool IsSelectedDisabled => SelectedDisabledText is not null;

    // ==================== 表单 ====================

    public bool IsNodeEditable => IsEditable && _selectedItem is not null;

    public bool IsNodeInputEnabled => IsNodeEditable && IsInputPhase;

    /// <summary>
    /// 系统根没有序号、不可停用、不能复制或删除
    /// </summary>
    public bool ShowPositionInputs => _selectedItem is { Node.Level: not TopologyLevel.System };

    /// <summary>
    /// 产品位是最底层，没有下级数量
    /// </summary>
    public bool ShowChildCountInput => _selectedItem is { Node.Level: not TopologyLevel.Product };

    /// <summary>
    /// 下级数量的标签，如"层位数"
    /// </summary>
    public string? ChildCountLabel => _selectedItem is { } item && item.Node.Level.TryGetChildLevel(out var level)
        ? $"{level.DisplayName()}数"
        : null;

    /// <summary>
    /// 名称留空时界面显示位置编码，就用它做提示；系统根的名称即系统名称
    /// </summary>
    public string NodeNamePlaceholder => _selectedItem?.Node is { Level: not TopologyLevel.System } node
        ? node.Address.ToString()
        : "如 老化房 1#";

    public bool CanCopyOrRemove => IsNodeEditable && ShowPositionInputs;

    public string NodeNameInput
    {
        get => _nodeNameInput;
        set
        {
            if (SetProperty(ref _nodeNameInput, value ?? string.Empty))
                OnNodeInputChanged();
        }
    }

    public bool NodeEnabledInput
    {
        get => _nodeEnabledInput;
        set
        {
            if (SetProperty(ref _nodeEnabledInput, value))
                OnNodeInputChanged();
        }
    }

    public decimal? OrdinalInput
    {
        get => _ordinalInput;
        set
        {
            if (SetProperty(ref _ordinalInput, value))
                OnNodeInputChanged();
        }
    }

    public decimal? ChildCountInput
    {
        get => _childCountInput;
        set
        {
            if (SetProperty(ref _childCountInput, value))
                OnNodeInputChanged();
        }
    }

    /// <summary>
    /// 表单与当前保存的值不同
    /// </summary>
    public bool HasNodeChanges => _selectedItem?.Node is { } node
        && (NormalizeName(_nodeNameInput) != node.Name
            || _nodeEnabledInput != node.IsEnabled
            || (ShowPositionInputs && _ordinalInput != node.Ordinal)
            || (ShowChildCountInput && _childCountInput != node.Children.Count));

    // ==================== 命令 ====================

    private async Task SaveNodeAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !HasNodeChanges || _selectedItem is not { } item)
            return;

        var node = item.Node;
        var isRoot = node.Level == TopologyLevel.System;
        var name = NormalizeName(_nodeNameInput);
        if (isRoot && name is null)
        {
            ShowMessage(PendingOrigin.Node, "系统名称不能为空", isError: true);
            return;
        }

        int? ordinal = null;
        string? error;
        if (!isRoot)
        {
            if (!TryReadCount(_ordinalInput, "序号", MinCount, out var value, out error))
            {
                ShowMessage(PendingOrigin.Node, error, isError: true);
                return;
            }

            ordinal = value;
        }

        int? childCount = null;
        if (ChildCountLabel is { } label)
        {
            if (!TryReadCount(_childCountInput, label, MinChildCount, out var value, out error))
            {
                ShowMessage(PendingOrigin.Node, error, isError: true);
                return;
            }

            childCount = value;
        }

        var edit = new TopologyNodeEdit(name, isRoot || _nodeEnabledInput, ordinal, childCount);
        var plan = await PlanAsync(() => service.PlanNodeEdit(node.Id, edit), PendingOrigin.Node);
        if (plan is null)
            return;

        // 下级数量调大后展开它，新位置直接可见
        var grew = childCount > node.Children.Count;
        await SaveOrAwaitConfirmationAsync(service,
            new PendingSave(plan, PendingOrigin.Node, DescribeSaved("已保存", plan), Select: node.Id, Expand: grew ? node.Id : null));
    }

    private void RevertNode()
    {
        CancelPending(PendingOrigin.Node);
        ClearMessage(PendingOrigin.Node);
        ShowNodeInputs();
    }

    private async Task CopyAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanCopyOrRemove || _selectedItem is not { } item)
            return;

        var node = item.Node;
        var plan = await PlanAsync(() => service.PlanCopy(node.Id), PendingOrigin.Node);
        if (plan is null)
            return;

        // 保存后直接选中副本，方便接着改名
        var copy = FindCopy(plan, node);
        await SaveOrAwaitConfirmationAsync(service,
            new PendingSave(plan, PendingOrigin.Node, copy is null ? "已复制" : $"已复制为 {copy.Address}", Select: copy?.Id ?? node.Id));
    }

    private async Task RemoveAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanCopyOrRemove || _selectedItem is not { } item)
            return;

        var node = item.Node;

        // 删除后选中它的上级，而不是跳回系统根
        TopologyNodeId? parentId = null;
        if (Current is { } topology && node.Address.TryGetParent(out var parentAddress) && topology.TryGetNode(parentAddress, out var parent))
            parentId = parent.Id;

        var plan = await PlanAsync(() => service.PlanRemove(node.Id), PendingOrigin.Node);
        if (plan is null)
            return;

        await SaveOrAwaitConfirmationAsync(service, new PendingSave(plan, PendingOrigin.Node, $"已删除 {node.Address}", Select: parentId));
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 表单回填为选中节点的当前值。程序回填直接写字段，不算用户改动，不能触发取消确认。
    /// </summary>
    private void ShowNodeInputs()
    {
        var node = _selectedItem?.Node;
        _nodeNameInput = node?.Name ?? string.Empty;
        _nodeEnabledInput = node?.IsEnabled ?? true;
        _ordinalInput = node is { Level: not TopologyLevel.System } ? node.Ordinal : null;
        _childCountInput = node is { Level: not TopologyLevel.Product } ? node.Children.Count : null;

        OnPropertyChanged(nameof(NodeNameInput));
        OnPropertyChanged(nameof(NodeEnabledInput));
        OnPropertyChanged(nameof(OrdinalInput));
        OnPropertyChanged(nameof(ChildCountInput));
        OnPropertyChanged(nameof(HasNodeChanges));
        SaveNodeCommand.RaiseCanExecuteChanged();
        RevertNodeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 用户改了表单：等待确认的保存已不是现在的输入，取消；上一次的结果或错误提示也随之清除
    /// </summary>
    private void OnNodeInputChanged()
    {
        CancelPending(PendingOrigin.Node);
        ClearMessage(PendingOrigin.Node);
        OnPropertyChanged(nameof(HasNodeChanges));
        SaveNodeCommand.RaiseCanExecuteChanged();
        RevertNodeCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedIdText));
        OnPropertyChanged(nameof(SelectedDisabledText));
        OnPropertyChanged(nameof(IsSelectedDisabled));
        OnPropertyChanged(nameof(IsNodeEditable));
        OnPropertyChanged(nameof(IsNodeInputEnabled));
        OnPropertyChanged(nameof(ShowPositionInputs));
        OnPropertyChanged(nameof(ShowChildCountInput));
        OnPropertyChanged(nameof(ChildCountLabel));
        OnPropertyChanged(nameof(NodeNamePlaceholder));
        OnPropertyChanged(nameof(CanCopyOrRemove));
        CopyCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }


    /// <summary>
    /// 副本放在同级现有最大序号之后：按预览基准里上级的下一个序号，在变更后的拓扑中找到它
    /// </summary>
    private static TopologyNode? FindCopy(TopologyChangePlan plan, TopologyNode source)
    {
        if (plan.Base is not { } @base
            || !source.Address.TryGetParent(out var parentAddress)
            || !@base.TryGetNode(parentAddress, out var parent)
            || !parentAddress.TryGetChild(parent.NextChildOrdinal, out var copyAddress))
            return null;

        return plan.Candidate.TryGetNode(copyAddress, out var copy) ? copy : null;
    }

    private static string? NormalizeName(string input)
    {
        var trimmed = input.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
