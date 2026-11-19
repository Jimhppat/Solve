using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

namespace Solve.UI.ViewModels;

/// <summary>
/// 按驱动声明自动生成的模块参数输入区：添加表单与编辑弹窗共用。
/// </summary>
public sealed class ParameterFormViewModel : ViewModelBase
{
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    /// <summary>
    /// 按驱动声明生成输入项；<paramref name="values"/> 中有的用填写值，没有的显示默认值
    /// </summary>
    public void Load(IReadOnlyList<ParameterInfo> declarations, ParameterValues values)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(values);

        Fields.Clear();
        foreach (var declaration in declarations)
            Fields.Add(new ParameterFieldViewModel(declaration, values.TryGetValue(declaration.Key, out var value) ? value : declaration.DefaultValue));

        OnPropertyChanged(nameof(HasFields));
    }

    /// <summary>
    /// 校验全部输入并收集为参数值；与默认值相同的由服务在保存时去掉
    /// </summary>
    public Result<ParameterValues> Collect()
    {
        var problems = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var normalized = field.Info.Normalize(field.RawValue, out var error);
            if (normalized is null)
                problems.Add(error!);
            else
                values[field.Info.Key] = normalized;
        }

        return problems.Count == 0
            ? Result<ParameterValues>.Success(new ParameterValues(values))
            : Result<ParameterValues>.Failure(DeviceErrors.InvalidField("Parameters", string.Join("；", problems)));
    }
}

/// <summary>
/// 一个参数的输入项：整数、实数、文本用输入框，是/否用复选框，选项用下拉框
/// </summary>
public sealed class ParameterFieldViewModel : ViewModelBase
{
    private string _text;
    private bool _boolValue;
    private ParameterChoice? _selectedChoice;

    public ParameterFieldViewModel(ParameterInfo info, string value)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _text = value ?? string.Empty;
        _boolValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        _selectedChoice = info.Choices.FirstOrDefault(c => c.Value == value) ?? info.Choices.FirstOrDefault();
    }

    public ParameterInfo Info { get; }

    public string Label => string.IsNullOrEmpty(Info.Unit) ? Info.Name : $"{Info.Name}（{Info.Unit}）";

    public bool IsBoolean => Info.Kind == ParameterKind.Boolean;

    public bool IsChoice => Info.Kind == ParameterKind.Choice;

    public bool IsTextInput => !IsBoolean && !IsChoice;

    public IReadOnlyList<ParameterChoice> Choices => Info.Choices;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool BoolValue
    {
        get => _boolValue;
        set => SetProperty(ref _boolValue, value);
    }

    public ParameterChoice? SelectedChoice
    {
        get => _selectedChoice;
        set => SetProperty(ref _selectedChoice, value);
    }

    /// <summary>
    /// 当前输入的文本值（未校验）
    /// </summary>
    public string RawValue => Info.Kind switch
    {
        ParameterKind.Boolean => _boolValue ? "true" : "false",
        ParameterKind.Choice => _selectedChoice?.Value ?? string.Empty,
        _ => _text,
    };
}
