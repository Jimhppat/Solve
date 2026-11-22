using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology;

/// <summary>
/// 对单个节点的一次修改：拓扑页上一个节点的属性表单，各项一起预览、一起保存。
/// </summary>
/// <param name="Name">显示名；空白表示不命名（界面显示位置编码）。系统根的名称即系统名称，不能为空</param>
/// <param name="Enabled">是否启用；停用会让整棵子树都视为停用。系统根不可停用</param>
/// <param name="Ordinal">新序号，null 表示不改；系统根没有序号</param>
/// <param name="ChildCount">新的下级数量，null 表示不改；调小删除序号最大的几个，调大照抄最后一个下级的结构</param>
/// <param name="Ports">本位置声明的接点组，null 表示不改；空列表表示不再声明。对它下面的全部产品位生效</param>
public sealed record TopologyNodeEdit(
    string? Name,
    bool Enabled,
    int? Ordinal = null,
    int? ChildCount = null,
    IReadOnlyList<PortGroup>? Ports = null);
