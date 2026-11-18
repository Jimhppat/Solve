using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;

namespace Solve.UI.ViewModels;

public sealed class BusItemViewModel : ViewModelBase, IDisposable
{
    private readonly BusRuntime? _runtime;
    private readonly Action<BusItemViewModel>? _onEdit;
    private readonly Action<BusItemViewModel>? _onDelete;
    private readonly CancellationTokenSource _cts = new();

    private BusState _state;
    private string _name = string.Empty;
    private string _kind = string.Empty;
    private string _portParams = string.Empty;
    private string _note = string.Empty;
    private string _stateText = string.Empty;
    private string _stateColor = "#94A3B8";

    /// <summary>
    /// 设计模式 / 预览器专用构造函数
    /// </summary>
    public BusItemViewModel(string name, string kind, string portParams, BusState state, string note)
    {
        _name = name;
        _kind = kind;
        _portParams = portParams;
        _note = note;
        UpdateStateDisplay(state);
    }

    public BusItemViewModel(
        BusRuntime runtime,
        Action<BusItemViewModel> onEdit,
        Action<BusItemViewModel> onDelete)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _onEdit = onEdit;
        _onDelete = onDelete;

        RefreshDescriptor(runtime.Descriptor);
        UpdateStateDisplay(runtime.State);

        ConnectCommand = new RelayCommand(
            () => _runtime.Connect(),
            () => CanConnect);

        DisconnectCommand = new AsyncRelayCommand(
            async () => await _runtime.DisconnectAsync(CancellationToken.None),
            () => CanDisconnect);

        ResetCommand = new RelayCommand(
            () => _runtime.Reset(),
            () => CanReset);

        EditCommand = new RelayCommand(() => _onEdit?.Invoke(this));
        DeleteCommand = new RelayCommand(() => _onDelete?.Invoke(this));

        _ = Task.Run(ListenStateChangesAsync);
    }

    public BusId Id => _runtime?.Id ?? BusId.Empty;

    public BusRuntime? Runtime => _runtime;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    public string PortParams
    {
        get => _portParams;
        private set => SetProperty(ref _portParams, value);
    }

    public BusState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                UpdateStateDisplay(value);
                RaiseCommandCanExecute();
            }
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string StateColor
    {
        get => _stateColor;
        private set => SetProperty(ref _stateColor, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public bool CanConnect => _state == BusState.Disconnected;

    public bool CanDisconnect => _state is BusState.Connected or BusState.Connecting or BusState.Reconnecting;

    public bool CanReset => _state is BusState.Faulted or BusState.SafeStop;

    public RelayCommand? ConnectCommand { get; }
    public AsyncRelayCommand? DisconnectCommand { get; }
    public RelayCommand? ResetCommand { get; }
    public RelayCommand? EditCommand { get; }
    public RelayCommand? DeleteCommand { get; }

    public void RefreshDescriptor(BusDescriptor descriptor)
    {
        Name = descriptor.Name;
        Kind = descriptor switch
        {
            SerialBusDescriptor => "串口",
            TcpBusDescriptor => "TCP 客户端",
            _ => "通用总线",
        };

        PortParams = descriptor switch
        {
            SerialBusDescriptor s => $"{s.PortName} · {s.BaudRate} · {s.DataBits}{s.Parity.ToString()[0]}{FormatStopBits(s.StopBits)}",
            TcpBusDescriptor t => $"{t.Host}:{t.Port}",
            _ => "-",
        };
    }

    private static string FormatStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => "1",
        SerialStopBits.Two => "2",
        SerialStopBits.OnePointFive => "1.5",
        _ => "1",
    };

    private void UpdateStateDisplay(BusState state)
    {
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanReset));

        (StateText, StateColor) = state switch
        {
            BusState.Connected => ("已连接", "#10B981"),       // Green
            BusState.Connecting => ("连接中", "#0EA5E9"),      // Blue
            BusState.Reconnecting => ("重连中", "#F59E0B"),    // Amber
            BusState.Disconnected => ("未连接", "#94A3B8"),    // Gray
            BusState.Disconnecting => ("断开中", "#A855F7"),   // Purple
            BusState.Faulted => ("故障", "#EF4444"),          // Red
            BusState.SafeStop => ("安全停止", "#DC2626"),     // Dark Red
            _ => ("未知", "#94A3B8"),
        };

        if (string.IsNullOrEmpty(_note) || state == BusState.Connected)
        {
            Note = state switch
            {
                BusState.Connected => "收发正常",
                BusState.Disconnected => "通道已关闭，就绪",
                BusState.Connecting => "正在打开通道...",
                BusState.Reconnecting => "通讯中断，尝试重连中",
                BusState.Disconnecting => "正在关闭物理通道...",
                BusState.Faulted => "重连失败耗尽，已故障锁存",
                BusState.SafeStop => "安全联锁跳闸，已安全停止",
                _ => "-",
            };
        }
    }

    private async Task ListenStateChangesAsync()
    {
        if (_runtime is null) return;
        var reader = _runtime.StateChanges;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        State = evt.Transition.To;
                        if (evt.Cause is not null)
                            Note = evt.Cause.Message;
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit on dispose
        }
    }

    private void RaiseCommandCanExecute()
    {
        ConnectCommand?.RaiseCanExecuteChanged();
        DisconnectCommand?.RaiseCanExecuteChanged();
        ResetCommand?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
