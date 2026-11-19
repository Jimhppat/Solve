using System;

namespace Solve.UI.ViewModels;

/// <summary>
/// 新增下级时，下级模板中某一层的数量输入（如"每层槽位"）。值变化时通知拓扑页刷新预览、取消等待确认的结构修改。
/// </summary>
public sealed class TopologyTemplateInputViewModel : ViewModelBase
{
    private readonly Action _changed;
    private decimal? _value;

    /// <param name="label">输入框标签，如"每层槽位"</param>
    /// <param name="value">预填值</param>
    /// <param name="changed">用户改动后的回调（程序预填不触发）</param>
    public TopologyTemplateInputViewModel(string label, decimal? value, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Label = label;
        _value = value;
        _changed = changed;
    }

    public string Label { get; }

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
