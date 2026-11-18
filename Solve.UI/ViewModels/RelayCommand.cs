using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;

namespace Solve.UI.ViewModels;

/// <summary>
/// CanExecuteChanged 的订阅者是按钮等 UI 控件，只能在 UI 线程上通知；其他线程调用时转投 UI 线程。
/// </summary>
internal static class CommandNotifier
{
    public static void Raise(ICommand command, EventHandler? handler)
    {
        if (handler is null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            handler(command, EventArgs.Empty);
        else
            Dispatcher.UIThread.Post(() => handler(command, EventArgs.Empty));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CommandNotifier.Raise(this, CanExecuteChanged);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();

            // 不能 ConfigureAwait(false)：命令由 UI 发起，收尾（_isExecuting 复位、通知按钮）必须回到 UI 线程。
            // 否则 _execute 在线程池上完成时，按钮在非 UI 线程读取 Command 会抛异常并导致进程退出
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CommandNotifier.Raise(this, CanExecuteChanged);
}
