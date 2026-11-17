using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

public sealed class BusIdTests
{
    [Fact]
    public void New_IsNotEmpty()
    {
        Assert.False(BusId.New().IsEmpty);
        Assert.True(default(BusId).IsEmpty);
    }

    [Fact]
    public void TryParse_ValidGuid_RoundTrips()
    {
        var id = BusId.New();

        Assert.True(BusId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryParse_InvalidOrEmpty_Fails(string? input)
    {
        Assert.False(BusId.TryParse(input, out var parsed));
        Assert.True(parsed.IsEmpty);
    }

    [Fact]
    public void DefaultResult_ReportsUninitialized()
    {
        var result = default(Result<int>);

        Assert.True(result.IsFailure);
        Assert.Same(Error.Uninitialized, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
