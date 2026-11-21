using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.Core.Application.Wiring;

/// <summary>
/// 可以接线的模块：模块上有启用的通道
/// </summary>
/// <param name="BusName">所在总线的名称（界面显示）</param>
/// <param name="Channels">模块实际可用的通道组（驱动的通道能力 + 模块的通道配置），只含有启用通道的组</param>
public sealed record WiringModule(BusId BusId, string BusName, DeviceDescriptor Device, IReadOnlyList<ChannelGroup> Channels);

/// <summary>
/// 一次接线变更的预览：保存时据 <see cref="Base"/> 判断预览是否过期
/// </summary>
public sealed class WiringPlan
{
    internal WiringPlan(WiringMap @base, WiringMap candidate, IReadOnlyList<Wire> added, string operation)
    {
        Base = @base;
        Candidate = candidate;
        Added = added;
        Operation = operation;
    }

    public WiringMap Base { get; }

    public WiringMap Candidate { get; }

    /// <summary>
    /// 新增的接线，按落点位置编码顺序
    /// </summary>
    public IReadOnlyList<Wire> Added { get; }

    /// <summary>
    /// 人读的操作说明，保存时写入日志
    /// </summary>
    public string Operation { get; }
}

/// <summary>
/// 映射草稿的一行：把一路通道接到一个位置
/// </summary>
public sealed record WiringDraftRow(TopologyNodeId Node, ChannelRef Channel)
{
    public string? Purpose { get; init; }

    public string? Label { get; init; }
}

/// <summary>
/// 草稿一行的校验结果
/// </summary>
/// <param name="Wire">按当前模块通道生成的接线；通道无法解析（模块已删除、通道已停用等）时为 null</param>
/// <param name="Problem">这一行的问题（面向操作员）；没有问题为 null</param>
public sealed record WiringRowCheck(Wire? Wire, string? Problem)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// 映射草稿的预览：逐行的校验结果；全部通过时 <see cref="Plan"/> 可直接保存
/// </summary>
public sealed class WiringAssignPreview(IReadOnlyList<WiringRowCheck> rows, WiringPlan? plan)
{
    /// <summary>
    /// 与草稿各行一一对应
    /// </summary>
    public IReadOnlyList<WiringRowCheck> Rows { get; } = rows;

    /// <summary>
    /// 全部行都没有问题时的保存计划；有任何问题为 null（整批不写，避免只接上一部分）
    /// </summary>
    public WiringPlan? Plan { get; } = plan;

    public int ProblemCount => Rows.Count(r => !r.IsValid);
}

/// <summary>
/// 接线用例服务：持有当前生效的接线（跟随拓扑），负责校验、预览与保存。
/// </summary>
/// <remarks>
/// <para>新增分两步：Plan* 生成预览（校验通道是否声明、功能、通道独占、路径唯一）→ <see cref="ApplyAsync"/> 写库并生效。
/// 预览期间接线或拓扑被改动，保存以 Wiring.StalePlan 拒绝。</para>
/// <para>只管配置，不访问硬件。线程：<see cref="Current"/> 可在任意线程无锁读取；写操作串行执行，磁盘 I/O 在线程池上。</para>
/// </remarks>
public interface IWiringService
{
    /// <summary>
    /// 当前接线快照，按当前拓扑定位；尚未读取、拓扑未建档时为空快照
    /// </summary>
    WiringMap Current { get; }

    /// <summary>
    /// 最近一次读取失败的原因；读取失败后禁止修改，避免在不完整的数据上做冲突判断
    /// </summary>
    Error? LoadError { get; }

    Task<Result> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 可以接线的模块（模块上有启用的通道），按总线名、地址排序
    /// </summary>
    IReadOnlyList<WiringModule> ListModules();

    /// <summary>
    /// 一路通道的人读名称，如"电源-01 输出"、"采集卡-02 输入 7"；模块或通道组已不存在时退回组标识与通道号
    /// </summary>
    string DescribeChannel(ChannelRef channel);

    /// <summary>
    /// 在一个位置上接一路通道
    /// </summary>
    Result<WiringPlan> PlanAdd(TopologyNodeId node, ChannelRef channel, string? purpose, string? label);

    /// <summary>
    /// 批量接线：把一个位置下面某一层的全部位置，依次接到连续的通道上
    /// </summary>
    Result<WiringPlan> PlanBatch(WiringBatchRequest request);

    /// <summary>
    /// 映射草稿：按行校验任意一组"位置 ← 通道"配对（可跨模块、跨总线、跳着选），逐行给出问题。
    /// 只在读取失败或尚未建档时返回失败；行内问题放在预览里，全部通过才给出保存计划。纯内存计算，不写库。
    /// </summary>
    Result<WiringAssignPreview> PlanAssign(IReadOnlyList<WiringDraftRow> rows);

    Task<Result> ApplyAsync(WiringPlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// 修改用途与线号
    /// </summary>
    Task<Result> UpdateAsync(WireId id, string? purpose, string? label, CancellationToken cancellationToken);

    /// <summary>
    /// 拆除接线，返回实际拆除的条数
    /// </summary>
    Task<Result<int>> RemoveAsync(IReadOnlyList<WireId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// 接线有变化（增删改、重新读取）之后触发，在执行变更的线程上调用。界面处理程序须自行切回 UI 线程。
    /// </summary>
    event EventHandler? Changed;
}
