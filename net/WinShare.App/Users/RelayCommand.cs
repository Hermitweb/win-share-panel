using System.Windows.Input;

namespace WinShare.App.Users;

/// <summary>可复命命令（纯 .NET，不依赖 WPF 控件；与 Dashboard.RelayCommand 同构，模块自持）。</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action _sync;
    readonly Func<Task>? _async;

    public RelayCommand(Action action)
    {
        _sync = action ?? throw new ArgumentNullException(nameof(action));
    }

    public RelayCommand(Func<Task> action)
    {
        _async = action ?? throw new ArgumentNullException(nameof(action));
        _sync = () => { };
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (_async is not null) _async().GetAwaiter().GetResult();
        else _sync();
    }

    public async Task ExecuteAsync()
    {
        if (_async is not null) await _async();
        else _sync();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
