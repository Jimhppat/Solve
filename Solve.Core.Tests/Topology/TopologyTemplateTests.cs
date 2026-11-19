using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

/// <summary>
/// 下级模板：规模计算不溢出、按新节点所在层级校验层数与数量
/// </summary>
public sealed class TopologyTemplateTests
{
    [Fact]
    public void Counts_PerInstance()
    {
        var template = new TopologyTemplate([4, 1, 2]);

        Assert.Equal(1 + 4 + 4 + 8, template.NodeCountPerInstance);
        Assert.Equal(8, template.ProductCountPerInstance(TopologyLevel.Layer));

        // 模板没有生成到产品位：新节点下没有产品位
        Assert.Equal(0, template.ProductCountPerInstance(TopologyLevel.Cabinet));
    }

    [Fact]
    public void Empty_IsJustTheNode()
    {
        Assert.Equal(1, TopologyTemplate.Empty.NodeCountPerInstance);
        Assert.Equal(1, TopologyTemplate.Empty.ProductCountPerInstance(TopologyLevel.Product));
        Assert.Equal(0, TopologyTemplate.Empty.ProductCountPerInstance(TopologyLevel.Fixture));
        Assert.True(TopologyTemplate.Empty.Validate(TopologyLevel.Product).IsSuccess);
    }

    [Fact]
    public void HugeCounts_DoNotOverflow()
    {
        var template = new TopologyTemplate([999, 999, 999, 999]);

        Assert.True(template.NodeCountPerInstance > 900_000_000_000);
    }

    [Fact]
    public void Validate_ChecksDepthAndRange()
    {
        Assert.True(new TopologyTemplate([1, 1, 1, 1]).Validate(TopologyLevel.Cabinet).IsSuccess);
        Assert.True(new TopologyTemplate([2]).Validate(TopologyLevel.Cabinet).IsSuccess); // 允许比剩余层数短

        Assert.Contains("只有 3 层", new TopologyTemplate([1, 1, 1, 1]).Validate(TopologyLevel.Layer).Error.Message);
        Assert.Contains("治具位数量", new TopologyTemplate([1, 1000]).Validate(TopologyLevel.Layer).Error.Message);
        Assert.Contains("槽位数量", new TopologyTemplate([0]).Validate(TopologyLevel.Layer).Error.Message);
        Assert.True(TopologyTemplate.Empty.Validate(TopologyLevel.System).IsFailure);
    }

    [Fact]
    public void Constructor_CopiesInput()
    {
        var counts = new[] { 2, 3 };
        var template = new TopologyTemplate(counts);

        counts[0] = 0;

        Assert.Equal([2, 3], template.CountsBelow);
    }
}
