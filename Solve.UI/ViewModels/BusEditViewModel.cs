using System;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.UI.ViewModels;

public sealed class BusEditViewModel : ViewModelBase
{
    private bool _isCreateMode = true;
    private BusId _id;
    private string _name = "新总线";
    private int _selectedKindIndex = 0; // 0 = 串口, 1 = TCP

    // 串口
    private string _portName = "COM1";
    private int _baudRate = 115200;
    private int _dataBits = 8;
    private SerialParity _parity = SerialParity.None;
    private SerialStopBits _stopBits = SerialStopBits.One;

    // TCP
    private string _host = "127.0.0.1";
    private int _port = 502;

    // 超时与重连
    private int _connectTimeoutMs = 3000;
    private int _readTimeoutMs = 1000;
    private int _writeTimeoutMs = 1000;
    private int _reconnectInitialDelayMs = 500;
    private int _reconnectMaxDelayMs = 10000;
    private int _reconnectMaxAttempts = 5;

    private string? _errorMessage;

    public bool IsCreateMode
    {
        get => _isCreateMode;
        set
        {
            if (SetProperty(ref _isCreateMode, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public string Title => IsCreateMode ? "新增总线" : $"编辑总线 - {Name}";

    public BusId Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                OnPropertyChanged(nameof(Title));
        }
    }

    public int SelectedKindIndex
    {
        get => _selectedKindIndex;
        set
        {
            if (SetProperty(ref _selectedKindIndex, value))
            {
                OnPropertyChanged(nameof(IsSerial));
                OnPropertyChanged(nameof(IsTcp));
            }
        }
    }

    // 供分段单选按钮双向绑定：只响应“选中”，取消选中由同组另一项的选中驱动
    public bool IsSerial
    {
        get => SelectedKindIndex == 0;
        set { if (value) SelectedKindIndex = 0; }
    }

    public bool IsTcp
    {
        get => SelectedKindIndex == 1;
        set { if (value) SelectedKindIndex = 1; }
    }

    // 串口下拉选项。波特率下拉框可编辑，允许 250000 等非标值，列表只是常用档位
    public static IReadOnlyList<int> BaudRateOptions { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];
    public static IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];
    public static IReadOnlyList<SerialParity> ParityOptions { get; } = Enum.GetValues<SerialParity>();
    public static IReadOnlyList<Choice<SerialStopBits>> StopBitsOptions { get; } =
    [
        new(SerialStopBits.One, "1"),
        new(SerialStopBits.OnePointFive, "1.5"),
        new(SerialStopBits.Two, "2"),
    ];

    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value);
    }

    public SerialParity Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    public SerialStopBits StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
                OnPropertyChanged(nameof(TcpEndpoint));
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
                OnPropertyChanged(nameof(TcpEndpoint));
        }
    }

    /// <summary>
    /// 最终拨号的端点预览；IPv6 字面量按 RFC 3986 加方括号。
    /// </summary>
    public string TcpEndpoint
    {
        get
        {
            var host = (_host ?? string.Empty).Trim();
            if (host.Contains(':') && !host.StartsWith('['))
                host = $"[{host}]";
            return $"tcp://{host}:{_port}";
        }
    }

    public int ConnectTimeoutMs
    {
        get => _connectTimeoutMs;
        set => SetProperty(ref _connectTimeoutMs, value);
    }

    public int ReadTimeoutMs
    {
        get => _readTimeoutMs;
        set => SetProperty(ref _readTimeoutMs, value);
    }

    public int WriteTimeoutMs
    {
        get => _writeTimeoutMs;
        set => SetProperty(ref _writeTimeoutMs, value);
    }

    public int ReconnectInitialDelayMs
    {
        get => _reconnectInitialDelayMs;
        set => SetProperty(ref _reconnectInitialDelayMs, value);
    }

    public int ReconnectMaxDelayMs
    {
        get => _reconnectMaxDelayMs;
        set => SetProperty(ref _reconnectMaxDelayMs, value);
    }

    public int ReconnectMaxAttempts
    {
        get => _reconnectMaxAttempts;
        set => SetProperty(ref _reconnectMaxAttempts, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public void ResetForCreate()
    {
        IsCreateMode = true;
        Id = BusId.New();
        Name = "新设备链路";
        SelectedKindIndex = 0;
        PortName = "COM1";
        BaudRate = 115200;
        DataBits = 8;
        Parity = SerialParity.None;
        StopBits = SerialStopBits.One;
        Host = "192.168.1.100";
        Port = 502;
        ConnectTimeoutMs = 3000;
        ReadTimeoutMs = 1000;
        WriteTimeoutMs = 1000;
        ReconnectInitialDelayMs = 500;
        ReconnectMaxDelayMs = 10000;
        ReconnectMaxAttempts = 5;
        ErrorMessage = null;
    }

    public void LoadFrom(BusDescriptor descriptor)
    {
        IsCreateMode = false;
        Id = descriptor.Id;
        Name = descriptor.Name;
        ConnectTimeoutMs = (int)descriptor.Timeouts.Connect.TotalMilliseconds;
        ReadTimeoutMs = (int)descriptor.Timeouts.Read.TotalMilliseconds;
        WriteTimeoutMs = (int)descriptor.Timeouts.Write.TotalMilliseconds;
        ReconnectInitialDelayMs = (int)descriptor.Reconnect.InitialDelay.TotalMilliseconds;
        ReconnectMaxDelayMs = (int)descriptor.Reconnect.MaxDelay.TotalMilliseconds;
        ReconnectMaxAttempts = descriptor.Reconnect.MaxAttempts ?? 0;
        ErrorMessage = null;

        if (descriptor is SerialBusDescriptor serial)
        {
            SelectedKindIndex = 0;
            PortName = serial.PortName;
            BaudRate = serial.BaudRate;
            DataBits = serial.DataBits;
            Parity = serial.Parity;
            StopBits = serial.StopBits;
        }
        else if (descriptor is TcpBusDescriptor tcp)
        {
            SelectedKindIndex = 1;
            Host = tcp.Host;
            Port = tcp.Port;
        }
    }

    public Result<BusDescriptor> BuildDescriptor()
    {
        var timeouts = new BusTimeouts(
            TimeSpan.FromMilliseconds(Math.Max(10, ConnectTimeoutMs)),
            TimeSpan.FromMilliseconds(Math.Max(10, ReadTimeoutMs)),
            TimeSpan.FromMilliseconds(Math.Max(10, WriteTimeoutMs)));

        var reconnect = new ReconnectPolicy(
            TimeSpan.FromMilliseconds(Math.Max(10, ReconnectInitialDelayMs)),
            TimeSpan.FromMilliseconds(Math.Max(10, ReconnectMaxDelayMs)),
            2.0,
            ReconnectMaxAttempts <= 0 ? null : ReconnectMaxAttempts);

        BusDescriptor descriptor;
        if (SelectedKindIndex == 0)
        {
            descriptor = new SerialBusDescriptor(Id, Name, PortName)
            {
                BaudRate = BaudRate,
                DataBits = DataBits,
                Parity = Parity,
                StopBits = StopBits,
                Timeouts = timeouts,
                Reconnect = reconnect,
            };
        }
        else
        {
            descriptor = new TcpBusDescriptor(Id, Name, Host, Port)
            {
                Timeouts = timeouts,
                Reconnect = reconnect,
            };
        }

        var validationErrors = descriptor.Validate();
        if (validationErrors.Count > 0)
        {
            ErrorMessage = string.Join("\n", validationErrors);
            return Result<BusDescriptor>.Failure(BusErrors.InvalidDescriptor(validationErrors));
        }

        ErrorMessage = null;
        return Result<BusDescriptor>.Success(descriptor);
    }
}

/// <summary>
/// 下拉框选项：值与显示文本分离（如 SerialStopBits.OnePointFive 显示为 "1.5"）。
/// </summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
