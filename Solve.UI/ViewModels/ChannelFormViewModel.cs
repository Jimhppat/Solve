using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

namespace Solve.UI.ViewModels;

/// <summary>
/// 通道功能下拉框的一项
/// </summary>
public sealed record ChannelKindOption(ChannelKind Kind)
{
    public static IReadOnlyList<ChannelKindOption> All { get; } = ChannelKinds.All.Select(k => new ChannelKindOption(k)).ToList();

    public override string ToString() => Kind.DisplayName();
}

/// <summary>
/// 编辑模块弹窗里的"通道"区：这台模块实际启用哪几路、通道叫什么。
/// </summary>
/// <remarks>
/// <para>有驱动的模块：组、功能、数量由驱动决定，只能勾选启用、填写别名。</para>
/// <para>通用模块：没有协议，系统不知道硬件有哪些端子，由操作员逐组定义名称、功能、数量（只有需要接线时才定义）。</para>
/// <para>只在 UI 线程上使用。</para>
/// </remarks>
public sealed class ChannelFormViewModel : ViewModelBase
{
    private bool _isManual;

    public ChannelFormViewModel()
    {
        AddGroupCommand = new RelayCommand(AddGroup, () => _isManual && Groups.Count < ModuleChannelConfig.MaxGroups);
    }

    public ObservableCollection<ChannelGroupFormViewModel> Groups { get; } = [];

    /// <summary>
    /// 通道由模块手工定义（通用模块）
    /// </summary>
    public bool IsManual
    {
        get => _isManual;
        private set
        {
            if (SetProperty(ref _isManual, value))
                OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>
    /// 驱动声明了通道，或可以手工定义通道；都没有时整个通道区不显示
    /// </summary>
    public bool IsVisible => _isManual || Groups.Count > 0;

    public RelayCommand AddGroupCommand { get; }

    /// <summary>
    /// 按驱动的通道能力与模块已保存的配置生成输入项
    /// </summary>
    public void Load(DriverDescriptor driver, ModuleChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(config);

        Groups.Clear();
        IsManual = driver.AllowsManualChannels;
        if (driver.AllowsManualChannels)
        {
            foreach (var setting in config.Groups)
                Groups.Add(ChannelGroupFormViewModel.Manual(setting.Key, setting.Name, setting.Kind, setting.Count ?? 1, setting, Remove));
        }
        else
        {
            foreach (var declared in driver.Channels)
                Groups.Add(ChannelGroupFormViewModel.Declared(declared, config.Find(declared.Key)));
        }

        RaiseChanged();
    }

    /// <summary>
    /// 校验全部输入并收集为通道配置。有驱动的组全部启用且没有别名时不写设置（按驱动声明即可）
    /// </summary>
    public Result<ModuleChannelConfig> Collect()
    {
        var problems = new List<string>();
        var settings = new List<ChannelGroupSetting>(Groups.Count);
        foreach (var group in Groups)
        {
            if (group.TryCollect(out var setting, out var problem))
            {
                if (setting is not null)
                    settings.Add(setting);
            }
            else
            {
                problems.Add(problem!);
            }
        }

        return problems.Count == 0
            ? Result<ModuleChannelConfig>.Success(new ModuleChannelConfig(settings))
            : Result<ModuleChannelConfig>.Failure(DeviceErrors.InvalidField("Channels", string.Join("；", problems)));
    }

    private void AddGroup()
    {
        // 组标识只给系统用（接线按它找回通道），界面不显示：取 g1、g2… 中下一个未用的
        var used = Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var next = 1;
        while (used.Contains($"g{next}"))
            next++;

        Groups.Add(ChannelGroupFormViewModel.Manual($"g{next}", null, ChannelKind.Switch, 8, null, Remove));
        RaiseChanged();
    }

    private void Remove(ChannelGroupFormViewModel group)
    {
        Groups.Remove(group);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsVisible));
        AddGroupCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// 一组通道的输入项：组信息 + 每路通道的启用与别名
/// </summary>
public sealed class ChannelGroupFormViewModel : ViewModelBase
{
    private readonly ChannelGroup? _declared;
    private string _name;
    private ChannelKindOption? _kind;
    private decimal? _countInput;

    private ChannelGroupFormViewModel(string key, bool isManual, ChannelGroup? declared, string name, ChannelKind kind, int count,
        ChannelGroupSetting? setting, Action<ChannelGroupFormViewModel>? onRemove)
    {
        Key = key;
        IsManual = isManual;
        _declared = declared;
        _name = name;
        _kind = ChannelKindOption.All.FirstOrDefault(o => o.Kind == kind);
        _countInput = count;
        EnableAllCommand = new RelayCommand(() => SetAll(true));
        DisableAllCommand = new RelayCommand(() => SetAll(false));
        RemoveCommand = new RelayCommand(() => onRemove?.Invoke(this), () => onRemove is not null);
        Resize(count, setting);
    }

    public static ChannelGroupFormViewModel Declared(ChannelGroup declared, ChannelGroupSetting? setting)
        => new(declared.Key, isManual: false, declared, declared.Name, declared.Kind, declared.Count, setting, onRemove: null);

    public static ChannelGroupFormViewModel Manual(string key, string? name, ChannelKind? kind, int count, ChannelGroupSetting? setting,
        Action<ChannelGroupFormViewModel> onRemove)
        => new(key, isManual: true, declared: null, name ?? string.Empty, kind ?? ChannelKind.Switch, Math.Clamp(count, 1, ChannelGroup.MaxCount),
            setting, onRemove);

    public string Key { get; }

    public bool IsManual { get; }

    public bool IsDeclared => !IsManual;

    /// <summary>
    /// 有驱动的组的标题，如"继电器（切换，16 路）"
    /// </summary>
    public string? DeclaredTitle => _declared is { } g ? $"{g.Name}（{g.Kind.DisplayName()}，{g.Count} 路）" : null;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
                RefreshPlaceholders();
        }
    }

    public IReadOnlyList<ChannelKindOption> KindOptions => ChannelKindOption.All;

    public ChannelKindOption? Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public decimal MaxCount => ChannelGroup.MaxCount;

    /// <summary>
    /// 通道数（通用模块）；改动后通道行随之增减，已填的启用与别名保留
    /// </summary>
    public decimal? CountInput
    {
        get => _countInput;
        set
        {
            if (!SetProperty(ref _countInput, value))
                return;
            if (value is { } number && number == decimal.Truncate(number) && number is >= 1 and <= ChannelGroup.MaxCount)
                Resize((int)number, null);
        }
    }

    public ObservableCollection<ChannelRowViewModel> Channels { get; } = [];

    public RelayCommand EnableAllCommand { get; }

    public RelayCommand DisableAllCommand { get; }

    public RelayCommand RemoveCommand { get; }

    internal bool TryCollect(out ChannelGroupSetting? setting, out string? problem)
    {
        setting = null;
        problem = null;

        var enabled = Channels.Where(c => c.IsEnabled).Select(c => c.Index).ToList();
        var allEnabled = enabled.Count == Channels.Count;
        var aliases = Channels
            .Where(c => !string.IsNullOrWhiteSpace(c.Alias))
            .Select(c => new KeyValuePair<int, string>(c.Index, c.Alias.Trim()))
            .ToList();

        if (aliases.FirstOrDefault(a => a.Value.Length > ChannelGroupSetting.MaxAliasLength) is { Value: not null } tooLong)
        {
            problem = $"第 {tooLong.Key} 路的别名不能超过 {ChannelGroupSetting.MaxAliasLength} 个字符";
            return false;
        }

        if (IsDeclared)
        {
            // 全部启用、没有别名：按驱动声明，不写设置
            if (!allEnabled || aliases.Count > 0)
                setting = new ChannelGroupSetting(Key, enabled: allEnabled ? null : enabled, aliases: aliases);
            return true;
        }

        var name = _name.Trim();
        if (name.Length == 0)
        {
            problem = "请填写每组通道的名称";
            return false;
        }

        if (name.Length > DriverDescriptor.MaxChannelNameLength)
        {
            problem = $"通道组名称不能超过 {DriverDescriptor.MaxChannelNameLength} 个字符";
            return false;
        }

        if (_kind is null)
        {
            problem = $"请选择「{name}」的功能";
            return false;
        }

        if (_countInput is not { } count || count != decimal.Truncate(count) || count < 1 || count > ChannelGroup.MaxCount)
        {
            problem = $"「{name}」的数量必须是 1～{ChannelGroup.MaxCount} 之间的整数";
            return false;
        }

        setting = new ChannelGroupSetting(Key, name, _kind.Kind, (int)count, allEnabled ? null : enabled, aliases);
        return true;
    }

    /// <summary>
    /// 按数量生成通道行；已有行的启用与别名保留，首次生成时取保存的设置
    /// </summary>
    private void Resize(int count, ChannelGroupSetting? setting)
    {
        while (Channels.Count > count)
            Channels.RemoveAt(Channels.Count - 1);

        for (var index = Channels.Count + 1; index <= count; index++)
        {
            var enabled = setting?.Enabled is not { } list || list.Contains(index);
            var alias = setting is not null && setting.Aliases.TryGetValue(index, out var saved) ? saved : string.Empty;
            Channels.Add(new ChannelRowViewModel(index, enabled, alias, DefaultName(index)));
        }

        RefreshPlaceholders();
    }

    private void RefreshPlaceholders()
    {
        foreach (var row in Channels)
            row.Placeholder = DefaultName(row.Index);
    }

    private string DefaultName(int index)
    {
        // 与接线、映射页的默认名同一规则（ChannelGroup.DefaultName），这里预览的就是别名留空时的显示
        return ChannelGroup.DefaultName(_name.Trim().Length > 0 ? _name.Trim() : "通道", index);
    }

    private void SetAll(bool enabled)
    {
        foreach (var row in Channels)
            row.IsEnabled = enabled;
    }
}

/// <summary>
/// 一路通道：是否启用（停用的端子不出现在接线表单里）、别名（留空用默认名，如"继电器 CH3"）
/// </summary>
public sealed class ChannelRowViewModel(int index, bool isEnabled, string alias, string placeholder) : ViewModelBase
{
    private bool _isEnabled = isEnabled;
    private string _alias = alias;
    private string _placeholder = placeholder;

    public int Index { get; } = index;

    public string IndexText => ChannelGroup.Code(Index);

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value ?? string.Empty);
    }

    /// <summary>
    /// 别名留空时显示的默认名
    /// </summary>
    public string Placeholder
    {
        get => _placeholder;
        set => SetProperty(ref _placeholder, value);
    }
}
