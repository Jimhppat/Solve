using Solve.Core.Domain.Buses;

namespace Solve.Core.Tests.Buses;

public sealed class ReconnectPolicyTests
{
    private static ReconnectPolicy Policy(
        double initialMs = 500, double maxMs = 30_000, double multiplier = 2.0, int? maxAttempts = 5)
        => new(TimeSpan.FromMilliseconds(initialMs), TimeSpan.FromMilliseconds(maxMs), multiplier, maxAttempts);

    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1_000)]
    [InlineData(3, 2_000)]
    [InlineData(6, 16_000)]
    [InlineData(7, 30_000)] // 32 s 被夹紧到 30 s
    [InlineData(100, 30_000)]
    public void GetDelay_GrowsExponentially_AndClampsAtMax(int attempt, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Policy().GetDelay(attempt));
    }

    [Fact]
    public void GetDelay_HugeAttempt_DoesNotOverflow()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Policy(multiplier: 10).GetDelay(int.MaxValue));
    }

    [Fact]
    public void GetDelay_MultiplierOne_IsConstant()
    {
        var policy = Policy(initialMs: 1_000, multiplier: 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetDelay_NonPositiveAttempt_Throws(int attempt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().GetDelay(attempt));
    }

    [Theory]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, true)]
    [InlineData(0, 0, true)] // 0 次：首次失败即 Faulted
    public void IsExhausted_ComparesAgainstMaxAttempts(int maxAttempts, int attemptsMade, bool expected)
    {
        Assert.Equal(expected, Policy(maxAttempts: maxAttempts).IsExhausted(attemptsMade));
    }

    [Fact]
    public void IsExhausted_Unlimited_NeverExhausts()
    {
        Assert.False(Policy(maxAttempts: null).IsExhausted(int.MaxValue));
    }

    [Fact]
    public void IsExhausted_NegativeAttempts_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().IsExhausted(-1));
    }
}
