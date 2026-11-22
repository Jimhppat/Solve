using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 节点表单里的一组接点：功能、名称、数量（如"采集 V × 4"展开为 V1–V4）。只在 UI 线程上使用。
/// </summary>
public sealed class PortRowViewModel : ViewModelBase
{
    private readonly Action _changed;
    private ChannelKindOption? _kind;
    private string _name;
    private decimal? _countInput;

    public PortRowViewModel(PortGroup? group, Action changed, Action<PortRowViewModel> remove)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(remove);
        _changed = changed;
        _kind = ChannelKindOption.All.FirstOrDefault(o => o.Kind == group?.Kind) ?? ChannelKindOption.All[0];
        _name = group?.Name ?? string.Empty;
        _countInput = group?.Count ?? 1;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public IReadOnlyList<ChannelKindOption> KindOptions => ChannelKindOption.All;

    public int MaxCount => PortGroup.MaxCount;

    public int MaxNameLength => PortGroup.MaxNameLength;

    public ChannelKindOption? Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
                _changed();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
                _changed();
        }
    }

    public decimal? CountInput
    {
        get => _countInput;
        set
        {
            if (SetProperty(ref _countInput, value))
                _changed();
        }
    }

    public RelayCommand RemoveCommand { get; }

    /// <summary>
    /// 读出这一行。只检查能否构成接点组；名称长度、重复、数量范围等由领域层统一校验并给出原因
    /// </summary>
    public bool TryRead(out PortGroup group, out string? error)
    {
        group = null!;
        error = null;
        if (_kind is not { } kind)
        {
            error = "请为每组接点选择功能";
            return false;
        }

        if (_countInput is not { } count || count != decimal.Truncate(count) || count < 1 || count > PortGroup.MaxCount)
        {
            error = $"接点数量必须是 1–{PortGroup.MaxCount} 的整数";
            return false;
        }

        group = new PortGroup(kind.Kind, _name.Trim(), (int)count);
        return true;
    }
}

/// <summary>
/// 拓扑页节点表单的"接点"区：本位置声明的接点组（对下面全部产品位生效），以及从上级继承的接点要求。
/// </summary>
public sealed partial class TopologyViewModel
{
    private RelayCommand? _addPortCommand;
    private IReadOnlyList<string> _inheritedPorts = [];

    /// <summary>
    /// 本位置声明的接点组（编辑中）
    /// </summary>
    public ObservableCollection<PortRowViewModel> PortRows { get; } = [];

    public bool HasPortRows => PortRows.Count > 0;

    public RelayCommand AddPortCommand => _addPortCommand ??= new RelayCommand(AddPort);

    /// <summary>
    /// 上级声明、本位置下的产品位同样要求的接点，每项如"C01：采集 V × 4"
    /// </summary>
    public IReadOnlyList<string> InheritedPorts
    {
        get => _inheritedPorts;
        private set
        {
            if (SetProperty(ref _inheritedPorts, value))
                OnPropertyChanged(nameof(HasInheritedPorts));
        }
    }

    public bool HasInheritedPorts => _inheritedPorts.Count > 0;

    /// <summary>
    /// 接点区的标题：产品位上是"它要求的接点"，上级位置上是"它下面每个产品位都要求的接点"
    /// </summary>
    public string PortsLabel => _selectedItem?.Node.Level == TopologyLevel.Product ? "接点" : "产品位接点";

    private void AddPort()
    {
        if (PortRows.Count >= PortGroup.MaxGroupsPerNode)
            return;

        PortRows.Add(new PortRowViewModel(null, OnPortsChanged, RemovePort));
        OnPortsChanged();
    }

    private void RemovePort(PortRowViewModel row)
    {
        if (PortRows.Remove(row))
            OnPortsChanged();
    }

    private void OnPortsChanged()
    {
        OnPropertyChanged(nameof(HasPortRows));
        OnNodeInputChanged();
    }

    /// <summary>
    /// 表单回填：本位置的接点组与上级的接点要求。程序回填不算用户改动
    /// </summary>
    private void ShowPortInputs(TopologyNode? node)
    {
        PortRows.Clear();
        foreach (var group in node?.Ports ?? [])
            PortRows.Add(new PortRowViewModel(group, OnPortsChanged, RemovePort));

        InheritedPorts = node is null ? [] : DescribeInheritedPorts(node);
        OnPropertyChanged(nameof(HasPortRows));
        OnPropertyChanged(nameof(PortsLabel));
    }

    /// <summary>
    /// 接点组输入与当前保存的不同（行内容无法读出时也算有改动，保存时报出原因）
    /// </summary>
    private bool HasPortChanges(TopologyNode node)
    {
        if (PortRows.Count != node.Ports.Count)
            return true;

        for (var i = 0; i < PortRows.Count; i++)
        {
            if (!PortRows[i].TryRead(out var group, out _) || group != node.Ports[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 读出全部接点组；任一行无法读出时返回 null 并给出原因
    /// </summary>
    private IReadOnlyList<PortGroup>? ReadPorts(out string? error)
    {
        error = null;
        var groups = new List<PortGroup>(PortRows.Count);
        foreach (var row in PortRows)
        {
            if (!row.TryRead(out var group, out error))
                return null;
            groups.Add(group);
        }

        return groups;
    }

    private IReadOnlyList<string> DescribeInheritedPorts(TopologyNode node)
    {
        if (Current is not { } topology || node.Level == TopologyLevel.System)
            return [];

        var result = new List<string>();
        if (topology.Root.Ports.Count > 0)
            result.Add($"系统：{string.Join("、", topology.Root.Ports)}");

        for (var level = TopologyLevel.Cabinet; level < node.Level; level++)
        {
            if (topology.TryGetNode(node.Address.AncestorAt(level), out var ancestor) && ancestor.Ports.Count > 0)
                result.Add($"{ancestor.Address}：{string.Join("、", ancestor.Ports)}");
        }

        return result;
    }
}
