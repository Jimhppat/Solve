using Solve.Core.Domain.Twin;

namespace Solve.Core.Tests.Twin;

public sealed class TwinStatusSummaryTests
{
    private static TwinStatusSummary Summary(int disabled = 0, params (ProductState State, int Count)[] counts)
    {
        var values = new int[ProductStates.Count];
        foreach (var (state, count) in counts)
            values[(int)state] = count;
        return new TwinStatusSummary(values, disabled);
    }

    [Fact]
    public void Default_IsUnknown_AndSeverityOrderIsFixed()
    {
        Assert.Equal(ProductState.Unknown, default(ProductState));
        Assert.Equal(
            [ProductState.Fault, ProductState.Alarm, ProductState.Failed, ProductState.Unknown,
             ProductState.Running, ProductState.Ready, ProductState.Passed, ProductState.Empty],
            ProductStates.BySeverity);
        Assert.Equal(ProductStates.Count, ProductStates.BySeverity.Distinct().Count());
        Assert.All(ProductStates.BySeverity.Zip(ProductStates.BySeverity.Skip(1)),
            pair => Assert.True(pair.First.Severity() > pair.Second.Severity()));
    }

    [Fact]
    public void Alarms_AreFaultAlarmAndFailed_NotUnknown()
    {
        // 无数据不进红字：没接数据源时它会遍布全屏
        Assert.Equal(
            [ProductState.Fault, ProductState.Alarm, ProductState.Failed],
            ProductStates.BySeverity.Where(state => state.IsAlarm()));
    }

    [Fact]
    public void Worst_IsMostSevereNonZero_OrNullWhenNothingActive()
    {
        Assert.Equal(ProductState.Alarm, Summary(0, (ProductState.Passed, 9), (ProductState.Alarm, 1)).Worst);
        Assert.Equal(ProductState.Running, Summary(0, (ProductState.Passed, 9), (ProductState.Running, 1)).Worst);
        Assert.Null(Summary(disabled: 5).Worst);
        Assert.Null(TwinStatusSummary.None.Worst);
    }

    [Fact]
    public void Describe_ListsBySeverity()
    {
        var summary = Summary(3, (ProductState.Passed, 5), (ProductState.Fault, 1), (ProductState.Running, 30),
            (ProductState.Alarm, 2), (ProductState.Unknown, 4));

        Assert.Equal("故障 1 · 报警 2", summary.DescribeAlarms());
        Assert.Equal("故障 1 · 报警 2 · 无数据 4 · 测试中 30 · 合格 5 · 停用 3", summary.DescribeAll());
        Assert.Equal(42, summary.Active);
        Assert.Equal(45, summary.Total);
    }

    [Fact]
    public void SameAs_ComparesValues()
    {
        Assert.True(Summary(1, (ProductState.Passed, 2)).SameAs(Summary(1, (ProductState.Passed, 2))));
        Assert.False(Summary(1, (ProductState.Passed, 2)).SameAs(Summary(0, (ProductState.Passed, 2))));
        Assert.False(Summary(0, (ProductState.Passed, 2)).SameAs(Summary(0, (ProductState.Failed, 2))));
    }

    [Fact]
    public void InvalidCounts_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new TwinStatusSummary(new int[3], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TwinStatusSummary(new int[ProductStates.Count], -1));
    }
}
