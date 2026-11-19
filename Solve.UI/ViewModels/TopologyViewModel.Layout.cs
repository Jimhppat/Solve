using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 拓扑页顶部的一键拓扑：按统一布局生成整机（尚未建档时即建档）或选中节点的下级。
/// </summary>
/// <remarks>
/// <para>与现有拓扑按位置编码对齐：同一位置保留原 Id、名称与启用状态，多出的位置删除、缺少的新增。
/// 删除或新增可用位置时，确认框出现在卡片里。</para>
/// <para>输入框按生成对象预填：某一层统一时填现有数量，按它生成不改动结构；不统一时留空，必须由操作员给出数量，
/// 避免把个性化过的结构悄悄抹平。</para>
/// </remarks>
public sealed partial class TopologyViewModel
{
    private const string DefaultSystemName = "设备";

    // 首次建档时每层默认 1 个
    private static readonly int?[] FirstCreationDefaults = [1, 1, 1, 1, 1];

    private string _systemNameInput = DefaultSystemName;
    private bool _isNodeScope;
    private IReadOnlyList<TopologyCountInputViewModel> _layoutInputs = [];

    /// <summary>
    /// 按卡片上的数量生成；尚未建档时即建档
    /// </summary>
    public AsyncRelayCommand GenerateCommand { get; }

    public bool IsLayoutInputEnabled => IsEditable && IsInputPhase;

    /// <summary>
    /// 系统名称只在建档时填写；建档后在系统根的表单里修改
    /// </summary>
    public string SystemNameInput
    {
        get => _systemNameInput;
        set
        {
            if (SetProperty(ref _systemNameInput, value ?? string.Empty))
                OnLayoutInputChanged();
        }
    }

    public bool ShowSystemNameInput => !IsConfigured;

    // ==================== 范围 ====================

    /// <summary>
    /// 建档后才有范围可选
    /// </summary>
    public bool ShowScope => IsConfigured;

    /// <summary>
    /// 可以只生成选中节点的下级：选中的不是系统根（即整机）也不是产品位（没有下级）
    /// </summary>
    public bool CanScopeToNode
        => IsConfigured && _selectedItem is { Node.Level: not (TopologyLevel.System or TopologyLevel.Product) };

    public bool IsWholeDeviceScope
    {
        get => !IsNodeScope;
        set
        {
            if (value)
                SetScope(node: false);
        }
    }

    public bool IsNodeScope
    {
        get => _isNodeScope && CanScopeToNode;
        set
        {
            if (value)
                SetScope(node: true);
        }
    }

    public string NodeScopeText => CanScopeToNode ? $"选中：{SelectedTitle}" : "选中节点";

    // ==================== 数量与预览 ====================

    /// <summary>
    /// 生成对象下面每一层的数量输入：整机 5 个，柜体 4 个……治具位 1 个
    /// </summary>
    public IReadOnlyList<TopologyCountInputViewModel> LayoutInputs
    {
        get => _layoutInputs;
        private set => SetProperty(ref _layoutInputs, value);
    }

    /// <summary>
    /// 卡片标题旁：生成后整台设备的规模，输入有误时是原因
    /// </summary>
    public string LayoutSummaryText
    {
        get
        {
            if (_service is null)
                return "配置库不可用";
            if (_service.LoadError is not null)
                return "拓扑未能读取";

            var request = ReadLayoutRequest();
            return request.IsFailure
                ? request.Error.Message
                : $"生成后共 {Format(request.Value.Total)} 个节点，其中产品位 {Format(request.Value.Products)} 个";
        }
    }

    public bool IsLayoutSummaryError => IsEditable && ReadLayoutRequest().IsFailure;

    // ==================== 命令 ====================

    private async Task GenerateAsync()
    {
        if (_service is not { } service || !IsEditable || _state != TopologySaveState.Idle)
            return;

        var request = ReadLayoutRequest();
        if (request.IsFailure)
        {
            ShowMessage(PendingOrigin.Layout, request.Error.Message, isError: true);
            return;
        }

        var counts = request.Value.Counts;
        if (Current is null)
        {
            var name = _systemNameInput.Trim();
            if (name.Length == 0)
            {
                ShowMessage(PendingOrigin.Layout, "系统名称不能为空", isError: true);
                return;
            }

            var layout = new TopologyLayout(counts[0], counts[1], counts[2], counts[3], counts[4]);
            var created = await PlanAsync(() => service.PlanLayout(name, layout), PendingOrigin.Layout);
            if (created is null)
                return;

            await SaveOrAwaitConfirmationAsync(service,
                new PendingSave(created, PendingOrigin.Layout, $"已建档，共 {Format(created.Candidate.NodeCount)} 个节点"));
            return;
        }

        if (LayoutTarget is not { } target)
            return;

        var isNodeScope = IsNodeScope;
        var plan = await PlanAsync(() => service.PlanChildLayout(target.Id, counts), PendingOrigin.Layout);
        if (plan is null)
            return;

        if (!plan.HasChanges)
        {
            ShowMessage(PendingOrigin.Layout, "结构没有变化");
            return;
        }

        // 只生成某个节点的下级时，保存后选中并展开它，直接看到新结构
        await SaveOrAwaitConfirmationAsync(service, new PendingSave(
            plan, PendingOrigin.Layout, DescribeSaved("已生成", plan),
            Select: isNodeScope ? target.Id : null,
            Expand: isNodeScope ? target.Id : null));
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 生成对象：范围是选中节点时为它，否则为系统根；尚未建档时为 null
    /// </summary>
    private TopologyNode? LayoutTarget => IsNodeScope ? _selectedItem!.Node : Current?.Root;

    /// <summary>
    /// 读取各层数量并估算生成后整台设备的规模（与领域层的上限校验一致，只用于实时预览；最终以领域层为准）
    /// </summary>
    private Result<LayoutRequest> ReadLayoutRequest()
    {
        var counts = new int[_layoutInputs.Count];
        for (var i = 0; i < counts.Length; i++)
        {
            var input = _layoutInputs[i];
            if (!TryReadCount(input.Value, input.ErrorLabel, MinCount, out counts[i], out var error))
                return Result<LayoutRequest>.Failure(new Error(TopologyErrors.InvalidLayoutCode, error));
        }

        // 新下级的规模；每层最多 999、最多 5 层，连乘不会溢出 long
        long below = 0;
        long levelCount = 1;
        foreach (var count in counts)
        {
            levelCount *= count;
            below += levelCount;
        }

        long total = 1 + below;
        var products = levelCount;
        if (Current is { } topology && LayoutTarget is { } target)
        {
            long oldBelow = 0;
            for (var level = target.Level + 1; level <= TopologyLevel.Product; level++)
                oldBelow += topology.GetDescendants(target.Address, level).Count;

            total = topology.NodeCount - oldBelow + below;
            products = topology.CountAt(TopologyLevel.Product) - topology.GetDescendants(target.Address, TopologyLevel.Product).Count + levelCount;
        }

        if (total > EquipmentTopology.MaxNodeCount)
        {
            return Result<LayoutRequest>.Failure(new Error(TopologyErrors.InvalidLayoutCode,
                $"生成后共 {Format(total)} 个节点，超过上限 {Format(EquipmentTopology.MaxNodeCount)}"));
        }

        return Result<LayoutRequest>.Success(new LayoutRequest(counts, total, products));
    }

    /// <summary>
    /// 按生成对象重建输入框并预填。程序预填不算用户改动，不能触发取消确认。
    /// </summary>
    private void ShowLayoutInputs()
    {
        var topology = Current;
        var target = LayoutTarget;
        var values = topology is not null && target is not null
            ? topology.GetUniformChildCounts(target.Id)
            : FirstCreationDefaults;
        var firstLevel = target is null ? TopologyLevel.Cabinet : target.Level + 1;

        var inputs = new List<TopologyCountInputViewModel>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            // 第一层是生成对象自己的下级数（如"层位数"），往下是每个上级的下级数（如"每层槽位"）
            var level = firstLevel + i;
            var label = i == 0 ? $"{level.DisplayName()}数" : PerParentLabel(level);
            inputs.Add(new TopologyCountInputViewModel(label, i == 0 ? label : label + "数", values[i], OnLayoutInputChanged));
        }

        LayoutInputs = inputs;
        RaiseLayoutChanged();
    }

    private void SetScope(bool node)
    {
        if ((node && !CanScopeToNode) || _isNodeScope == node)
            return;

        _isNodeScope = node;
        CancelPending(PendingOrigin.Layout);
        ClearMessage(PendingOrigin.Layout);
        ShowLayoutInputs();
    }

    /// <summary>
    /// 范围是选中节点时，换了节点就换了生成对象：取消卡片里等待确认的生成，按新节点重新预填。
    /// 新节点是系统根或产品位时回到整机。
    /// </summary>
    private void OnSelectionChangedForLayout()
    {
        if (_isNodeScope)
        {
            if (!CanScopeToNode)
                _isNodeScope = false;

            CancelPending(PendingOrigin.Layout);
            ClearMessage(PendingOrigin.Layout);
            ShowLayoutInputs();
            return;
        }

        RaiseLayoutChanged();
    }

    private void OnLayoutInputChanged()
    {
        CancelPending(PendingOrigin.Layout);
        ClearMessage(PendingOrigin.Layout);
        RaiseLayoutChanged();
    }

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(IsLayoutSummaryError));
        OnPropertyChanged(nameof(ShowSystemNameInput));
        OnPropertyChanged(nameof(ShowScope));
        OnPropertyChanged(nameof(CanScopeToNode));
        OnPropertyChanged(nameof(IsWholeDeviceScope));
        OnPropertyChanged(nameof(IsNodeScope));
        OnPropertyChanged(nameof(NodeScopeText));
        OnPropertyChanged(nameof(IsLayoutInputEnabled));
    }

    /// <summary>
    /// 各层的叫法，与建档时一致
    /// </summary>
    private static string PerParentLabel(TopologyLevel level) => level switch
    {
        TopologyLevel.Layer => "每柜层位",
        TopologyLevel.Slot => "每层槽位",
        TopologyLevel.Fixture => "每槽治具位",
        TopologyLevel.Product => "每治具产品位",
        _ => level.DisplayName(),
    };

    /// <param name="Counts">生成对象下面每一层的数量</param>
    /// <param name="Total">生成后整台设备的节点数</param>
    /// <param name="Products">生成后整台设备的产品位数</param>
    private readonly record struct LayoutRequest(int[] Counts, long Total, long Products);
}
