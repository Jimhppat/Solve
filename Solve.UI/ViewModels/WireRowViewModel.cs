using System;
using System.Collections.Generic;
using System.Linq;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Device.Sdk;

namespace Solve.UI.ViewModels;

/// <summary>
/// 接线行上的一个命令按钮
/// </summary>
public sealed class WireCommandViewModel(DeviceCommandOption option, RelayCommand command)
{
    public DeviceCommandOption Option { get; } = option;

    public string Name => Option.Name;

    /// <summary>
    /// 高危命令用危险色按钮，每次执行前都要确认
    /// </summary>
    public bool IsHazardous => Option.Safety == CommandSafety.Hazardous;

    public RelayCommand Command { get; } = command;
}

/// <summary>
/// 接线面板中的一行：一路有效接线（本位置的，或继承自上级的）。配置部分不可变，健康度与读数由面板定时刷新。
/// 只显示人用信息：层级、位置编码、模块名、通道名；接线、模块、节点的 Id 不显示。
/// </summary>
public sealed class WireRowViewModel : ViewModelBase
{
    private string _healthText = "-";
    private string _healthColor = "#94A3B8";
    private string _problem = string.Empty;
    private string _readings = string.Empty;

    public WireRowViewModel(WireStatus status, Action<WireRowViewModel> onEdit, Action<WireRowViewModel> onRemove)
        : this(status, onEdit, onRemove, [], (_, _) => { }, () => false)
    {
    }

    /// <param name="commands">这路接线上可以从界面下发的命令</param>
    /// <param name="onCommand">点了某条命令（由面板解析、过联锁、必要时请操作员确认）</param>
    /// <param name="canCommand">当前能否下发命令（面板忙时禁用，防止重入）</param>
    /// <param name="onLocateChannel">"定位通道"：在硬件列表里显示这路接线的通道；为 null 时不显示该按钮</param>
    public WireRowViewModel(
        WireStatus status,
        Action<WireRowViewModel> onEdit,
        Action<WireRowViewModel> onRemove,
        IReadOnlyList<DeviceCommandOption> commands,
        Action<WireRowViewModel, DeviceCommandOption> onCommand,
        Func<bool> canCommand,
        Action<WireRowViewModel>? onLocateChannel = null)
    {
        CanLocateChannel = onLocateChannel is not null;
        LocateChannelCommand = new RelayCommand(() => onLocateChannel?.Invoke(this), () => CanLocateChannel);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(onEdit);
        ArgumentNullException.ThrowIfNull(onRemove);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(onCommand);
        ArgumentNullException.ThrowIfNull(canCommand);

        Wire = status.Wire;
        IsOwn = !status.IsInherited;
        SourceText = status.IsInherited ? $"来自{status.Source.Level.DisplayName()} {status.Source.Address}" : string.Empty;
        ModuleText = status.ModuleText;
        ChannelText = status.ChannelText;
        EditCommand = new RelayCommand(() => onEdit(this));
        RemoveCommand = new RelayCommand(() => onRemove(this));
        Commands = commands
            .Select(option => new WireCommandViewModel(option, new RelayCommand(() => onCommand(this, option), canCommand)))
            .ToList();
        Apply(status);
    }

    public Wire Wire { get; }

    public WireId Id => Wire.Id;

    /// <summary>
    /// 直接接在当前位置上（可修改、拆除）；继承自上级的只读，要到上级位置去改
    /// </summary>
    public bool IsOwn { get; }

    public bool IsInherited => !IsOwn;

    /// <summary>
    /// 继承来源，如"来自层位 C01-L02"；本位置的为空串
    /// </summary>
    public string SourceText { get; }

    /// <summary>
    /// 功能与用途，如"供电 VBAT"
    /// </summary>
    public string KindText => Wire.KindText;

    /// <summary>
    /// "总线 · 模块  通道"，如"1# RS-485 · 电源-01  输出"
    /// </summary>
    public string HardwareText => $"{ModuleText}  {ChannelText}";

    public string ModuleText { get; }

    public string ChannelText { get; }

    public string LabelText => Wire.Label is { } label ? $"线号 {label}" : string.Empty;

    public bool HasLabel => Wire.Label is not null;

    public string HealthText
    {
        get => _healthText;
        private set => SetProperty(ref _healthText, value);
    }

    public string HealthColor
    {
        get => _healthColor;
        private set => SetProperty(ref _healthColor, value);
    }

    /// <summary>
    /// 非就绪的原因，悬停健康标记时显示
    /// </summary>
    public string Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public string Readings
    {
        get => _readings;
        private set
        {
            if (SetProperty(ref _readings, value))
                OnPropertyChanged(nameof(HasReadings));
        }
    }

    public bool HasReadings => _readings.Length > 0;

    public RelayCommand EditCommand { get; }

    public RelayCommand RemoveCommand { get; }

    /// <summary>
    /// 在硬件列表里显示这路接线的通道（上级继承的接线也可以）
    /// </summary>
    public RelayCommand LocateChannelCommand { get; }

    public bool CanLocateChannel { get; }

    /// <summary>
    /// 可以下发的命令按钮，如"打开输出"、"关闭输出"
    /// </summary>
    public IReadOnlyList<WireCommandViewModel> Commands { get; }

    public bool HasCommands => Commands.Count > 0;

    /// <summary>
    /// 与当前可下发的命令是否一致（驱动被启用、停用或升级后命令会变）
    /// </summary>
    public bool HasSameCommands(IReadOnlyList<DeviceCommandOption> commands)
        => commands.Count == Commands.Count && commands.Select((c, i) => c == Commands[i].Option).All(same => same);

    public void RaiseCommandsChanged()
    {
        foreach (var command in Commands)
            command.Command.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 刷新健康度与读数（配置部分不变）
    /// </summary>
    public void Apply(WireStatus status)
    {
        (HealthText, HealthColor) = status.Health switch
        {
            WireHealth.Ready => ("就绪", "#10B981"),
            WireHealth.NoProtocol => ("无协议", "#94A3B8"),
            WireHealth.Waiting => ("等待", "#94A3B8"),
            WireHealth.Fault => ("故障", "#EF4444"),
            WireHealth.Invalid => ("无效", "#EF4444"),
            _ => ("-", "#94A3B8"),
        };
        Problem = status.Problem ?? string.Empty;
        Readings = status is { Driver: { } driver, Module: { } module }
            ? ReadingFormat.Channel(driver.Points, module.Points, Wire.Channel.Group, Wire.Channel.Index)
            : string.Empty;
    }
}
