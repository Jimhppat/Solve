using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solve.Core.Application.Topology;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 拓扑页的结构编辑：对选中节点新增下级、修改序号、复制为同级、删除，使每个节点的下级数量可以各不相同。
/// </summary>
/// <remarks>
/// <para>每次操作单独计算变更并保存，与改名 / 停用共用页面的保存流程与确认提示。结构输入属于选中节点：
/// 换选节点或改动结构输入，都会取消等待确认的结构修改。</para>
/// <para>新增与复制出的位置默认停用（故障安全方向），现场调试完成后由操作员启用；勾选"立即启用"时保存前要求确认。</para>
/// </remarks>
public sealed partial class TopologyViewModel
{
    private decimal? _addCountInput = 1;
    private IReadOnlyList<TopologyTemplateInputViewModel> _addTemplateInputs = [];
    private bool _newPositionsEnabledInput;
    private decimal? _ordinalInput;

    /// <summary>
    /// 在选中节点下新增下级
    /// </summary>
    public AsyncRelayCommand AddChildrenCommand { get; }

    /// <summary>
    /// 修改选中节点在上级中的序号（位置编码随之改变，保存前要求确认）
    /// </summary>
    public AsyncRelayCommand RenumberCommand { get; }

    /// <summary>
    /// 把选中节点连同下级复制为一个同级节点
    /// </summary>
    public AsyncRelayCommand CopyCommand { get; }

    /// <summary>
    /// 删除选中节点及其下级（保存前要求确认）
    /// </summary>
    public AsyncRelayCommand RemoveCommand { get; }

    /// <summary>
    /// 已建档、读取正常且选中了节点，可以编辑结构
    /// </summary>
    public bool IsStructureEditable => IsEditable && IsConfigured && _selectedItem is not null;

    public bool IsStructureInputEnabled => IsStructureEditable && IsInputPhase;

    /// <summary>
    /// 选中节点可以新增下级（产品位是最底层，不能）
    /// </summary>
    public bool CanAddChildren => IsStructureEditable && _selectedItem is { Node.Level: not TopologyLevel.Product };

    /// <summary>
    /// 选中节点可以修改序号、复制、删除（系统根不能）
    /// </summary>
    public bool CanEditPosition => IsStructureEditable && _selectedItem is { Node.Level: not TopologyLevel.System };

    /// <summary>
    /// 数量输入框后的单位，如"个层位"
    /// </summary>
    public string? AddChildLevelText => _selectedItem is { } item && item.Node.Level.TryGetChildLevel(out var level)
        ? $"个{level.DisplayName()}"
        : null;

    public decimal? AddCountInput
    {
        get => _addCountInput;
        set
        {
            if (SetProperty(ref _addCountInput, value))
                OnStructureInputChanged();
        }
    }

    /// <summary>
    /// 每个新节点下各层的数量，层数随选中节点的层级变化（新增产品位时为空）。预填同级第一个节点的结构：同一上级下的位置多半同规格
    /// </summary>
    public IReadOnlyList<TopologyTemplateInputViewModel> AddTemplateInputs
    {
        get => _addTemplateInputs;
        private set => SetProperty(ref _addTemplateInputs, value);
    }

    public bool HasAddTemplate => _addTemplateInputs.Count > 0;

    /// <summary>
    /// 新增与复制出的位置是否立即启用。默认否（停用），每次换选节点都回到默认值
    /// </summary>
    public bool NewPositionsEnabledInput
    {
        get => _newPositionsEnabledInput;
        set
        {
            if (SetProperty(ref _newPositionsEnabledInput, value))
                OnStructureInputChanged();
        }
    }

    /// <summary>
    /// 按当前输入新增后的位置与规模，或输入有误的原因
    /// </summary>
    public string? AddPreviewText
    {
        get
        {
            if (!CanAddChildren)
                return null;

            var request = ReadAddRequest();
            if (request.IsFailure)
                return request.Error.Message;

            var (parent, level, count, template) = request.Value;
            var first = parent.Address.Child(parent.NextChildOrdinal);
            var range = count == 1 ? first.ToString() : $"{first}～{parent.Address.Child(parent.NextChildOrdinal + count - 1)}";
            var nodes = count * template.NodeCountPerInstance;
            var products = count * template.ProductCountPerInstance(level);
            var state = _newPositionsEnabledInput ? "保存后直接启用（需确认）" : "新位置为停用状态";
            return $"将新增 {range}，共 {Format(nodes)} 个节点（其中产品位 {Format(products)} 个），{state}";
        }
    }

    public bool IsAddPreviewError => CanAddChildren && ReadAddRequest().IsFailure;

    /// <summary>
    /// 选中节点的新序号；系统根没有序号
    /// </summary>
    public decimal? OrdinalInput
    {
        get => _ordinalInput;
        set
        {
            if (SetProperty(ref _ordinalInput, value))
                OnStructureInputChanged();
        }
    }

    /// <summary>
    /// 序号输入与当前序号不同
    /// </summary>
    public bool HasOrdinalChange => _selectedItem is { Node: { Level: not TopologyLevel.System } node }
        && _ordinalInput is { } ordinal
        && ordinal != node.Ordinal;

    /// <summary>
    /// 删除按钮旁的说明：会连带删除多少个下级
    /// </summary>
    public string? RemoveHintText
    {
        get
        {
            if (!CanEditPosition || Current is not { } topology || _selectedItem is not { } item)
                return null;

            var node = item.Node;
            long below = 0;
            for (var level = node.Level + 1; level <= TopologyLevel.Product; level++)
                below += topology.GetDescendants(node.Address, level).Count;

            if (below == 0)
                return "删除后它的 Id 作废，无法恢复";

            var products = topology.GetDescendants(node.Address, TopologyLevel.Product).Count;
            return $"删除会连同 {Format(below)} 个下级一起删除（其中产品位 {Format(products)} 个），Id 作废，无法恢复";
        }
    }

    // ==================== 命令 ====================

    private async Task AddChildrenAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanAddChildren)
            return;

        var request = ReadAddRequest();
        if (request.IsFailure)
        {
            ReportError(request.Error.Message);
            return;
        }

        var (parent, level, count, template) = request.Value;
        var enabled = _newPositionsEnabledInput;
        var plan = await PlanAsync(() => service.PlanAddChildren(parent.Id, count, template, enabled));
        if (plan is null)
            return;

        var message = $"已在 {Describe(parent)} 下新增 {Format(count)} 个{level.DisplayName()}，共 {Format(plan.AddedCount)} 个节点；" +
                      (enabled ? "已直接启用" : "新位置为停用状态，现场调试完成后在节点详情中启用");
        await SaveOrAwaitConfirmationAsync(service,
            new PendingSave(plan, PendingOrigin.Structure, message, Select: parent.Id, Expand: parent.Id));
    }

    private async Task RenumberAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanEditPosition || _selectedItem is not { } item)
            return;

        var node = item.Node;
        if (!TryReadCount(_ordinalInput, "序号", out var ordinal, out var error))
        {
            ReportError(error);
            return;
        }

        if (ordinal == node.Ordinal)
            return;

        var plan = await PlanAsync(() => service.PlanRenumber(node.Id, ordinal));
        if (plan is null)
            return;

        var renumbered = plan.Candidate.GetNode(node.Id).Value.Address;
        var message = $"{node.Level.DisplayName()} {node.Address} 已改为 {renumbered}，" +
                      $"{Format(plan.RenumberedCount)} 个位置的位置编码已改变，请同步更换现场标牌与扫码标签";
        await SaveOrAwaitConfirmationAsync(service, new PendingSave(plan, PendingOrigin.Structure, message, Select: node.Id));
    }

    private async Task CopyAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanEditPosition || _selectedItem is not { } item)
            return;

        var node = item.Node;
        var enabled = _newPositionsEnabledInput;
        var plan = await PlanAsync(() => service.PlanCopy(node.Id, enabled));
        if (plan is null)
            return;

        // 保存后直接选中副本，方便接着改名、启用
        var copy = FindCopy(plan, node);
        var message = $"已把 {node.Address} 复制为 {copy?.Address.ToString() ?? "同级新位置"}，共 {Format(plan.AddedCount)} 个节点；" +
                      (enabled ? "已直接启用" : "副本为停用状态，现场调试完成后在节点详情中启用");
        await SaveOrAwaitConfirmationAsync(service,
            new PendingSave(plan, PendingOrigin.Structure, message, Select: copy?.Id ?? node.Id));
    }

    private async Task RemoveAsync()
    {
        if (_service is not { } service || _state != TopologySaveState.Idle || !CanEditPosition || _selectedItem is not { } item)
            return;

        var node = item.Node;

        // 删除后选中它的上级，而不是跳回系统根
        TopologyNodeId? parentId = null;
        if (Current is { } topology && node.Address.TryGetParent(out var parentAddress) && topology.TryGetNode(parentAddress, out var parent))
            parentId = parent.Id;

        var plan = await PlanAsync(() => service.PlanRemove(node.Id));
        if (plan is null)
            return;

        var message = $"已删除 {node.Level.DisplayName()} {node.Address} 及其下级，" +
                      $"共 {Format(plan.RemovedCount)} 个位置（其中产品位 {Format(plan.RemovedProductCount)} 个）";
        await SaveOrAwaitConfirmationAsync(service, new PendingSave(plan, PendingOrigin.Structure, message, Select: parentId));
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 读取新增请求。与领域层的校验重复的几项（序号用尽、节点上限）只用于实时预览，最终以领域层校验为准。
    /// </summary>
    private Result<AddRequest> ReadAddRequest()
    {
        if (_selectedItem?.Node is not { } parent || Current is not { } topology || !parent.Level.TryGetChildLevel(out var level))
            return Result<AddRequest>.Failure(TopologyErrors.InvalidEdit("选中的节点不能新增下级"));

        if (!TryReadCount(_addCountInput, "新增数量", out var count, out var error))
            return Result<AddRequest>.Failure(TopologyErrors.InvalidEdit(error));

        var counts = new int[_addTemplateInputs.Count];
        for (var i = 0; i < counts.Length; i++)
        {
            var input = _addTemplateInputs[i];
            if (!TryReadCount(input.Value, input.Label + "数", out counts[i], out error))
                return Result<AddRequest>.Failure(TopologyErrors.InvalidEdit(error));
        }

        var available = Math.Max(0, TopologyAddress.MaxOrdinal - parent.NextChildOrdinal + 1);
        if (count > available)
        {
            return Result<AddRequest>.Failure(TopologyErrors.InvalidEdit(
                $"{parent.Address} 下的{level.DisplayName()}序号已用到 {parent.NextChildOrdinal - 1}，最多还能新增 {available} 个"));
        }

        var template = new TopologyTemplate(counts);
        var total = topology.NodeCount + count * template.NodeCountPerInstance;
        if (total > EquipmentTopology.MaxNodeCount)
        {
            return Result<AddRequest>.Failure(TopologyErrors.InvalidEdit(
                $"新增后共 {Format(total)} 个节点，超过上限 {Format(EquipmentTopology.MaxNodeCount)}"));
        }

        return Result<AddRequest>.Success(new AddRequest(parent, level, count, template));
    }

    /// <summary>
    /// 换选节点或拓扑更新后回填结构输入。程序回填直接写字段，不算用户改动，不能触发取消确认。
    /// </summary>
    private void ShowStructureInputs()
    {
        var node = _selectedItem?.Node;
        _addCountInput = 1;
        _newPositionsEnabledInput = false;
        _ordinalInput = node is { Level: not TopologyLevel.System } ? node.Ordinal : null;
        AddTemplateInputs = BuildTemplateInputs(node);

        OnPropertyChanged(nameof(AddCountInput));
        OnPropertyChanged(nameof(NewPositionsEnabledInput));
        OnPropertyChanged(nameof(OrdinalInput));
        OnPropertyChanged(nameof(HasAddTemplate));
        RaiseStructurePreviewChanged();
    }

    private IReadOnlyList<TopologyTemplateInputViewModel> BuildTemplateInputs(TopologyNode? parent)
    {
        if (parent is null || !parent.Level.TryGetChildLevel(out var childLevel))
            return [];

        var inputs = new List<TopologyTemplateInputViewModel>();
        var sample = parent.Children.Count > 0 ? parent.Children[0] : null;
        for (var level = childLevel + 1; level <= TopologyLevel.Product; level++)
        {
            var count = sample is { Children.Count: > 0 } ? sample.Children.Count : 1;
            inputs.Add(new TopologyTemplateInputViewModel(PerParentLabel(level), count, OnStructureInputChanged));
            sample = sample is { Children.Count: > 0 } ? sample.Children[0] : null;
        }

        return inputs;
    }

    private void OnStructureInputChanged()
    {
        RaiseStructurePreviewChanged();
        CancelPending(PendingOrigin.Structure, "结构输入有改动，请重新操作");
    }

    private void RaiseStructurePreviewChanged()
    {
        OnPropertyChanged(nameof(AddPreviewText));
        OnPropertyChanged(nameof(IsAddPreviewError));
        OnPropertyChanged(nameof(HasOrdinalChange));
        RenumberCommand.RaiseCanExecuteChanged();
    }

    private void RaiseStructureSelectionChanged()
    {
        OnPropertyChanged(nameof(IsStructureEditable));
        OnPropertyChanged(nameof(IsStructureInputEnabled));
        OnPropertyChanged(nameof(CanAddChildren));
        OnPropertyChanged(nameof(CanEditPosition));
        OnPropertyChanged(nameof(AddChildLevelText));
        OnPropertyChanged(nameof(RemoveHintText));
        RaiseStructurePreviewChanged();
        AddChildrenCommand.RaiseCanExecuteChanged();
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

    private static string Describe(TopologyNode node)
        => node.Level == TopologyLevel.System ? "系统根" : $"{node.Level.DisplayName()} {node.Address}";

    /// <summary>
    /// 模板输入框的标签，与建档时布局输入框的叫法一致
    /// </summary>
    private static string PerParentLabel(TopologyLevel level) => level switch
    {
        TopologyLevel.Layer => "每柜层位",
        TopologyLevel.Slot => "每层槽位",
        TopologyLevel.Fixture => "每槽治具位",
        TopologyLevel.Product => "每治具产品位",
        _ => level.DisplayName(),
    };

    private readonly record struct AddRequest(TopologyNode Parent, TopologyLevel Level, int Count, TopologyTemplate Template);
}
