using System;

namespace Solve.UI.ViewModels;

/// <summary>
/// 一键拓扑中某一层的数量输入，如"柜体数""每层槽位"。值变化时通知拓扑页刷新预览、取消卡片里等待确认的生成。
/// </summary>
public sealed class TopologyCountInputViewModel : ViewModelBase
{
    private readonly Action _changed;
    private decimal? _value;

    /// <param name="label">输入框标签，如"每层槽位"</param>
    /// <param name="errorLabel">错误提示里的叫法，如"每层槽位数"</param>
    /// <param name="value">预填值；null 表示现有结构在这一层不统一，留空等操作员填写</param>
    /// <param name="changed">用户改动后的回调（程序预填不触发）</param>
    public TopologyCountInputViewModel(string label, string errorLabel, int? value, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Label = label;
        ErrorLabel = errorLabel;
        _value = value;
        Placeholder = value is null ? "不统一" : null;
        _changed = changed;
    }

    public string Label { get; }

    public string ErrorLabel { get; }

    /// <summary>
    /// 留空时的灰字：这一层现有结构不统一
    /// </summary>
    public string? Placeholder { get; }

    public decimal? Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
                _changed();
        }
    }
}
