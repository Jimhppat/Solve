using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.UI.ViewModels;

/// <summary>
/// 整模块接线预览的一行：一路通道，以及这一行的问题（如这路通道已接在这个位置上）
/// </summary>
/// <param name="CodeText">通道编号，如"CH1"</param>
/// <param name="LabelText">通道别名或组名，如"负载"</param>
/// <param name="Problem">校验问题；null 表示这一行可以写入</param>
public sealed record ModuleDropRow(string CodeText, string LabelText, string? Problem)
{
    public bool HasProblem => Problem is not null;
}

/// <summary>
/// 把整个模块拖到硬件映射页中间后的接线预览：模块的全部启用通道都接到放下的位置上，逐行校验，确认才写库。
/// </summary>
/// <remarks>
/// <para>一个位置可以接任意多路通道，不限数量、不分配到下级；同一位置上的多路通道按 CHn 区分（命令也按通道定位），不写用途。</para>
/// <para>整批要么全部接上、要么一条不写（<see cref="IWiringService.PlanAssign"/>）；确认时按当前接线与拓扑重新校验，
/// 预览之后被别处改过也不会写入过期的配对。只能在 UI 线程上使用；校验在线程池上计算。</para>
/// </remarks>
public sealed class ModuleDropViewModel : ViewModelBase
{
    private readonly Action<TopologyNode, string> _onApplied;
    private WiringInspector? _inspector;
    private MappingModuleOption? _module;
    private TopologyNode? _node;
    private IReadOnlyList<WiringDraftRow> _pairs = [];
    private IReadOnlyList<ModuleDropRow> _rows = [];
    private WiringAssignPreview? _preview;
    private string? _message;
    private bool _isError;
    private bool _busy;
    private int _validation;

    /// <param name="onApplied">写库成功后：放下的位置与结果说明</param>
    public ModuleDropViewModel(Action<TopologyNode, string> onApplied)
    {
        ArgumentNullException.ThrowIfNull(onApplied);
        _onApplied = onApplied;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !_busy && _preview?.Plan is not null);
        CancelCommand = new RelayCommand(Close, () => !_busy);
    }

    public bool IsOpen => _module is not null && _node is not null;

    /// <summary>
    /// 如"GJ DA750-4-60A-01 · 地址 1 → 治具位 C01-L01-S01-F01"
    /// </summary>
    public string? TitleText => _module is { } module && _node is { } node
        ? $"{module.Text} → {node.Level.DisplayName()} {node.Address}{(node.Name is { } name ? $"（{name}）" : string.Empty)}"
        : null;

    public IReadOnlyList<ModuleDropRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
                OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => _rows.Count > 0;

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message is not null;

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public bool IsBusy => _busy;

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 接入接线运行视图。须在 UI 线程上调用。
    /// </summary>
    public void Attach(WiringInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>
    /// 模块放到了一个位置上：模块的全部启用通道都配到这个位置，生成预览并校验
    /// </summary>
    public void Open(MappingModuleOption module, TopologyNode node)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(node);
        if (_busy || node.Level == TopologyLevel.System || module.Channels.Count == 0)
            return;

        _validation++;
        _module = module;
        _node = node;
        _pairs = module.Channels.Select(c => new WiringDraftRow(node.Id, c.Channel)).ToList();
        _preview = null;
        Message = null;
        Rows = module.Channels.Select(c => new ModuleDropRow(c.CodeText, c.LabelText, null)).ToList();
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(TitleText));
        RaiseCommands();
        _ = ValidateAsync();
    }

    /// <summary>
    /// 关闭预览，不写入任何接线
    /// </summary>
    public void Close()
    {
        if (_busy)
            return;

        _validation++;
        _module = null;
        _node = null;
        _pairs = [];
        _preview = null;
        Rows = [];
        Message = null;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(TitleText));
        RaiseCommands();
    }

    /// <summary>
    /// 接线在别处变了：重新校验仍打开的预览
    /// </summary>
    public void OnExternalChanged()
    {
        if (IsOpen && !_busy)
            _ = ValidateAsync();
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 逐行校验（线程池上计算）。连续多次触发时只采用最后一次的结果。
    /// </summary>
    private async Task ValidateAsync()
    {
        if (_inspector is not { } inspector || _pairs.Count == 0)
            return;

        var version = ++_validation;
        var pairs = _pairs;
        Result<WiringAssignPreview> preview;
        try
        {
            preview = await Task.Run(() => inspector.Wiring.PlanAssign(pairs));
        }
        catch (Exception ex)
        {
            // 在 UI 线程上继续：异常不能逃逸，否则会触发全站安全跳闸
            preview = Result<WiringAssignPreview>.Failure(new Error("Wiring.ModuleDropFailed", ex.Message));
        }

        if (version != _validation || !IsOpen)
            return;

        if (preview.IsFailure)
        {
            _preview = null;
            ShowMessage(preview.Error.Message, isError: true);
            RaiseCommands();
            return;
        }

        ApplyPreview(preview.Value);
    }

    private async Task ConfirmAsync()
    {
        if (_inspector is not { } inspector || _node is not { } node || _pairs.Count == 0 || _busy)
            return;

        var pairs = _pairs;
        SetBusy(true);
        try
        {
            Result result;
            int added;
            try
            {
                // 确认时按当前接线与拓扑重新校验：预览之后位置可能已被删除，或同一位置已接了同一路通道
                var preview = await Task.Run(() => inspector.Wiring.PlanAssign(pairs));
                if (preview.IsFailure)
                {
                    result = Result.Failure(preview.Error);
                    added = 0;
                }
                else if (preview.Value.Plan is not { } plan)
                {
                    ApplyPreview(preview.Value);
                    return;
                }
                else
                {
                    result = await inspector.Wiring.ApplyAsync(plan, CancellationToken.None);
                    added = plan.Added.Count;
                }
            }
            catch (Exception ex)
            {
                // 命令在 UI 线程上执行：异常不能逃逸，否则会触发全站安全跳闸
                result = Result.Failure(new Error("Wiring.ModuleDropFailed", ex.Message));
                added = 0;
            }

            if (result.IsFailure)
            {
                ShowMessage(result.Error.Message, isError: true);
                return;
            }

            var text = $"已接线 {added} 路：{TitleText}";
            SetBusy(false);
            Close();
            _onApplied(node, text);
        }
        finally
        {
            if (_busy)
                SetBusy(false);
        }
    }

    private void ApplyPreview(WiringAssignPreview preview)
    {
        // 校验结果按通道顺序逐行对应
        if (preview.Rows.Count != _rows.Count)
        {
            _preview = null;
            RaiseCommands();
            return;
        }

        _preview = preview;
        Rows = _rows.Select((row, i) => row with { Problem = preview.Rows[i].Problem }).ToList();
        var problems = preview.ProblemCount;
        ShowMessage(problems == 0
            ? $"接 {_rows.Count} 路通道，可以确认。"
            : $"接 {_rows.Count} 路通道，其中 {problems} 行有问题，不能确认。", isError: problems > 0);
        RaiseCommands();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommands();
    }

    private void ShowMessage(string message, bool isError = false)
    {
        IsError = isError;
        Message = message;
    }

    private void RaiseCommands()
    {
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
