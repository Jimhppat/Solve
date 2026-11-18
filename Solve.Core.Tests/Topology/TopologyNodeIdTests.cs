using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

public sealed class TopologyNodeIdTests
{
    [Fact]
    public void New_IsNonEmptyUuidV7()
    {
        var id = TopologyNodeId.New();

        Assert.False(id.IsEmpty);
        Assert.Equal(7, id.Value.Version);
        Assert.True(default(TopologyNodeId).IsEmpty);
        Assert.Equal(TopologyNodeId.Empty, default);
    }

    [Fact]
    public void New_IsUnique()
    {
        var ids = Enumerable.Range(0, 10_000).Select(_ => TopologyNodeId.New()).ToHashSet();

        Assert.Equal(10_000, ids.Count);
    }

    [Fact]
    public void TryParse_RoundTrips()
    {
        var id = TopologyNodeId.New();

        Assert.True(TopologyNodeId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(id, TopologyNodeId.Parse(id.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C01-L02")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryParse_InvalidOrEmpty_Fails(string? input)
    {
        Assert.False(TopologyNodeId.TryParse(input, out var parsed));
        Assert.True(parsed.IsEmpty);
    }
}
