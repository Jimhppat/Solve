using System.Globalization;

namespace Solve.Core.Domain.Twin;

/// <summary>
/// 一个节点（或一棵子树）下产品位的状态统计（不可变）。停用的产品位不计入各状态，单独计为 <see cref="Disabled"/>。
/// </summary>
public sealed class TwinStatusSummary
{
    private readonly int[] _counts;

    public TwinStatusSummary(ReadOnlySpan<int> counts, int disabled)
    {
        if (counts.Length != ProductStates.Count)
            throw new ArgumentException($"必须给出全部 {ProductStates.Count} 种状态的计数。", nameof(counts));
        ArgumentOutOfRangeException.ThrowIfNegative(disabled);

        _counts = counts.ToArray();
        Disabled = disabled;
        foreach (var count in _counts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(counts));
            Active += count;
        }
    }

    public static TwinStatusSummary None { get; } = new(new int[ProductStates.Count], 0);

    /// <summary>
    /// 某一状态的产品位数（只含启用的）
    /// </summary>
    public int this[ProductState state] => _counts[(int)state];

    /// <summary>
    /// 启用的产品位数
    /// </summary>
    public int Active { get; }

    /// <summary>
    /// 停用（维护屏蔽）的产品位数
    /// </summary>
    public int Disabled { get; }

    public int Total => Active + Disabled;

    /// <summary>
    /// 告警类（不良品）的产品位数，用于柜体切换胶囊上的红色计数
    /// </summary>
    public int AlarmCount
    {
        get
        {
            var count = 0;
            foreach (var state in ProductStates.BySeverity)
            {
                if (state.IsAlarm())
                    count += _counts[(int)state];
            }

            return count;
        }
    }

    /// <summary>
    /// 最严重的状态。没有启用的产品位时为 null（全部停用或没有产品位），界面按"停用 / 空"显示，而不是任何状态色。
    /// </summary>
    public ProductState? Worst
    {
        get
        {
            foreach (var state in ProductStates.BySeverity)
            {
                if (_counts[(int)state] > 0)
                    return state;
            }

            return null;
        }
    }

    /// <summary>
    /// 告警类计数，如"不良品 3"；没有时为空串
    /// </summary>
    public string DescribeAlarms() => Describe(alarmsOnly: true);

    /// <summary>
    /// 全部非零计数，按严重度排列，如"不良品 1 · 待机 30 · 良品 5 · 空闲 12 · 停用 8"
    /// </summary>
    public string DescribeAll() => Describe(alarmsOnly: false);

    public bool SameAs(TwinStatusSummary other)
        => ReferenceEquals(this, other) || (Disabled == other.Disabled && _counts.AsSpan().SequenceEqual(other._counts));

    public override string ToString() => DescribeAll();

    private string Describe(bool alarmsOnly)
    {
        var parts = new List<string>();
        foreach (var state in ProductStates.BySeverity)
        {
            var count = _counts[(int)state];
            if (count > 0 && (!alarmsOnly || state.IsAlarm()))
                parts.Add($"{state.DisplayName()} {count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!alarmsOnly && Disabled > 0)
            parts.Add($"停用 {Disabled.ToString(CultureInfo.InvariantCulture)}");

        return string.Join(" · ", parts);
    }
}
