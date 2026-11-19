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
    public void Default_IsIdle_AndOnlyFourStatesExist()
    {
        // 产品位默认空闲：枚举的 default 就是空闲，状态库新建的产品位不需要额外初始化
        Assert.Equal(ProductState.Idle, default(ProductState));
        Assert.Equal(4, ProductStates.Count);
        Assert.Equal([ProductState.Idle, ProductState.Standby, ProductState.Good, ProductState.Defective], ProductStates.All);
        Assert.Equal(["空闲", "待机", "良品", "不良品"], ProductStates.All.Select(state => state.DisplayName()));
        Assert.All(ProductStates.All, state => Assert.True(state.IsDefined()));
        Assert.False(((ProductState)4).IsDefined());
        Assert.False(((ProductState)(-1)).IsDefined());
    }

    [Fact]
    public void SeverityOrder_IsDefectiveStandbyGoodIdle()
    {
        Assert.Equal(
            [ProductState.Defective, ProductState.Standby, ProductState.Good, ProductState.Idle],
            ProductStates.BySeverity);
        Assert.Equal(ProductStates.Count, ProductStates.BySeverity.Distinct().Count());
        Assert.All(ProductStates.BySeverity.Zip(ProductStates.BySeverity.Skip(1)),
            pair => Assert.True(pair.First.Severity() > pair.Second.Severity()));
    }

    [Fact]
    public void Alarms_AreDefectiveOnly()
    {
        Assert.Equal([ProductState.Defective], ProductStates.All.Where(state => state.IsAlarm()));
    }

    [Fact]
    public void Worst_IsMostSevereNonZero_OrNullWhenNothingActive()
    {
        Assert.Equal(ProductState.Defective, Summary(0, (ProductState.Good, 9), (ProductState.Defective, 1)).Worst);
        Assert.Equal(ProductState.Standby, Summary(0, (ProductState.Good, 9), (ProductState.Standby, 1)).Worst);
        Assert.Equal(ProductState.Idle, Summary(0, (ProductState.Idle, 3)).Worst);
        Assert.Null(Summary(disabled: 5).Worst);
        Assert.Null(TwinStatusSummary.None.Worst);
    }

    [Fact]
    public void Describe_ListsBySeverity()
    {
        var summary = Summary(3, (ProductState.Good, 5), (ProductState.Defective, 1), (ProductState.Standby, 30),
            (ProductState.Idle, 4));

        Assert.Equal("不良品 1", summary.DescribeAlarms());
        Assert.Equal("不良品 1 · 待机 30 · 良品 5 · 空闲 4 · 停用 3", summary.DescribeAll());
        Assert.Equal(40, summary.Active);
        Assert.Equal(43, summary.Total);
    }

    [Fact]
    public void AlarmCount_CountsDefectiveOnly()
    {
        var summary = Summary(4, (ProductState.Defective, 3), (ProductState.Idle, 7), (ProductState.Standby, 9),
            (ProductState.Good, 2));

        // 空闲、待机、良品、停用都不算告警
        Assert.Equal(3, summary.AlarmCount);
        Assert.Equal(0, Summary(2, (ProductState.Idle, 5)).AlarmCount);
        Assert.Equal(0, TwinStatusSummary.None.AlarmCount);
    }

    [Fact]
    public void SameAs_ComparesValues()
    {
        Assert.True(Summary(1, (ProductState.Good, 2)).SameAs(Summary(1, (ProductState.Good, 2))));
        Assert.False(Summary(1, (ProductState.Good, 2)).SameAs(Summary(0, (ProductState.Good, 2))));
        Assert.False(Summary(0, (ProductState.Good, 2)).SameAs(Summary(0, (ProductState.Defective, 2))));
    }

    [Fact]
    public void InvalidCounts_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new TwinStatusSummary(new int[3], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TwinStatusSummary(new int[ProductStates.Count], -1));
    }
}
